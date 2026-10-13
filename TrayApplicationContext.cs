using Microsoft.Win32;

namespace ClockInTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan WindowStart = new(7, 30, 0);
    private static readonly TimeSpan WindowEnd = new(10, 30, 0);
    private static readonly TimeSpan WorkDay = TimeSpan.FromHours(9);
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryRetryInterval = TimeSpan.FromSeconds(10);

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClockInTray";

    private readonly object _sync = new();
    private readonly Control _ui = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly ToolStripMenuItem _statusItem = new("Not clocked in") { Enabled = false };
    private readonly ToolStripMenuItem _autoStartItem = new("Start with Windows") { CheckOnClick = true };
    private readonly List<WorkDayState> _pendingHistory = [];
    private readonly ToolStripMenuItem _setItem = new("Set clock-in time...");
    private readonly ToolStripMenuItem _clockOutItem = new("Clock out...");

    public bool RestartRequested { get; private set; }

    private WorkDayState _state;
    private string? _iconText;
    private Color _iconColor;
    private DateTime? _lastTick;
    private DateTime _lastSaved = DateTime.MinValue;
    private DateTime _lastHistoryAttempt = DateTime.MinValue;
    private DateTime _nextReminder = DateTime.MinValue;
    private bool _warnedHistoryLocked;
    private TimeDialog? _clockOutDialog;
    private bool _dialogOpen;

    public TrayApplicationContext()
    {
        var firstRun = !WorkDayState.Exists;
        _state = WorkDayState.Load();
        _ = _ui.Handle;

        _setItem.Click += (_, _) => PromptClockIn();
        _clockOutItem.Click += (_, _) => PromptClockOut(reminder: false);
        var historyItem = new ToolStripMenuItem("Open history in Excel", null, (_, _) => OpenHistory());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());
        var restartItem = new ToolStripMenuItem("Restart", null, (_, _) =>
        {
            RestartRequested = true;
            ExitThread();
        });

        _autoStartItem.Checked = IsAutoStartEnabled();
        _autoStartItem.CheckedChanged += (_, _) => SetAutoStart(_autoStartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem, new ToolStripSeparator(), _setItem, _clockOutItem, historyItem, _autoStartItem,
            new ToolStripSeparator(), restartItem, exitItem]);

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowStatus();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (firstRun)
        {
            _autoStartItem.Checked = true;
        }

        // Defer so the message loop is running before showing the dialog.
        var startup = new System.Windows.Forms.Timer { Interval = 500 };
        startup.Tick += (_, _) =>
        {
            startup.Stop();
            startup.Dispose();
            OnStartup();
        };
        startup.Start();
    }

    private void OnStartup()
    {
        lock (_sync)
        {
            RolloverIfNeeded(DateTime.Now);
            _state.Save();
            if (_state.ClockIn is not null)
            {
                RecordHistory(_state);
            }
        }

        Tick();
    }

    private static bool IsInPromptWindow(DateTime now) =>
        now.TimeOfDay >= WindowStart && now.TimeOfDay <= WindowEnd;

    private static bool IsWeekend(DateTime now) =>
        now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private void PromptClockIn()
    {
        if (_dialogOpen || (IsWeekend(DateTime.Now) && _state.ClockIn is null))
        {
            return;
        }

        DateTime defaultTime;
        lock (_sync)
        {
            _state.Prompted = true;
            defaultTime = _state.ClockIn ?? DateTime.Now;
        }

        _dialogOpen = true;
        using var dialog = new TimeDialog(
            "Clock in", "What time are you clocking in today?", defaultTime, DateTime.Today, "Start timer", "Not now");
        var accepted = dialog.ShowDialog() == DialogResult.OK;
        _dialogOpen = false;

        lock (_sync)
        {
            if (accepted)
            {
                _state.ClockIn = dialog.Time;
                _nextReminder = DateTime.MinValue;
                if (_state.ClockOut <= _state.ClockIn)
                {
                    _state.ClockOut = null;
                    _state.ClockOutAuto = false;
                    _state.ClockOutUnknownIssue = false;
                }
            }

            _state.Save();
            RecordHistory(_state);
        }

        Tick();
    }

    private void PromptClockOut(bool reminder)
    {
        if (_dialogOpen)
        {
            return;
        }

        DateTime clockIn;
        DateTime defaultTime;
        lock (_sync)
        {
            if (_state.ClockIn is not { } ci)
            {
                _tray.ShowBalloonTip(5000, "Clock out", "You haven't clocked in today.", ToolTipIcon.Info);
                return;
            }

            clockIn = ci;
            defaultTime = _state.ClockOut ?? DateTime.Now;
        }

        var message = reminder
            ? $"You've reached 9 hours (in at {clockIn:HH:mm}).\nWhat time are you clocking out?"
            : $"You clocked in at {clockIn:HH:mm}.\nWhat time are you clocking out?";

        _dialogOpen = true;
        using var dialog = new TimeDialog("Time to clock out", message, defaultTime, clockIn, "Clock out", "Later");
        _clockOutDialog = dialog;
        dialog.EnableLive(clockIn, WorkDay, message);
        var result = dialog.ShowDialog();
        _clockOutDialog = null;
        _dialogOpen = false;

        lock (_sync)
        {
            if (result == DialogResult.OK)
            {
                _state.ClockOut = dialog.Time;
                _state.ClockOutAuto = false;
                _state.ClockOutUnknownIssue = false;
                _state.Save();
                RecordHistory(_state);
            }
            else if (result == DialogResult.Cancel && reminder)
            {
                _nextReminder = DateTime.Now + ReminderInterval;
            }
        }

        Tick();
    }

    private void Tick()
    {
        bool promptClockIn;
        bool promptClockOut;
        lock (_sync)
        {
            var now = DateTime.Now;
            RolloverIfNeeded(now);

            // A long gap between ticks means the PC slept (lid closed): clock out at the last moment we were awake.
            if (_lastTick is { } last && now - last > SleepGap)
            {
                AutoClockOutIfDue(last);
            }

            _lastTick = now;
            _state.LastSeen = now;
            if (now - _lastSaved >= SaveInterval)
            {
                _state.Save();
                _lastSaved = now;
            }

            if (_pendingHistory.Count > 0 && now - _lastHistoryAttempt >= HistoryRetryInterval)
            {
                FlushHistory(silent: false);
            }

            UpdateDisplay(now);
            promptClockIn = _state.ClockIn is null && !_state.Prompted && !IsWeekend(now)
                && IsInPromptWindow(now) && !_dialogOpen;
            promptClockOut = _state.ClockIn is { } ci && _state.ClockOut is null
                && now - ci >= WorkDay && now >= _nextReminder && !_dialogOpen;
        }

        if (promptClockIn)
        {
            PromptClockIn();
        }
        else if (promptClockOut)
        {
            PromptClockOut(reminder: true);
        }
    }

    // Closes a previous day that was never clocked out, using the last time the app saw the PC awake.
    private void RolloverIfNeeded(DateTime now)
    {
        if (_state.Date == DateOnly.FromDateTime(now))
        {
            return;
        }

        if (_state.ClockIn is { } clockIn && _state.ClockOut is null)
        {
            // The app never saw the PC leave (crash or power cut), so the day is closed with an error marker.
            // Even then the clock-out is never earlier than the 9 hours.
            var planned = clockIn + WorkDay;
            _state.ClockOut = _state.LastSeen is { } seen && seen > planned ? seen : planned;
            _state.ClockOutAuto = true;
            _state.ClockOutUnknownIssue = true;
        }

        if (_state.ClockIn is not null)
        {
            RecordHistory(_state);
        }

        _state = WorkDayState.Fresh();
        _nextReminder = DateTime.MinValue;
        _state.Save();
    }

    // Registers an automatic clock-out when 9 hours were already reached at the given moment.
    private bool AutoClockOutIfDue(DateTime when)
    {
        if (_state.ClockIn is not { } clockIn || _state.ClockOut is not null || when < clockIn + WorkDay
            || DateOnly.FromDateTime(when) != _state.Date)
        {
            return false;
        }

        _state.ClockOut = when;
        _state.ClockOutAuto = true;
        _state.Save();
        RecordHistory(_state);

        // The dialog belongs to the UI thread, and this can be called from a system-events thread.
        try
        {
            _ui.BeginInvoke(() =>
            {
                if (_clockOutDialog is { } dialog)
                {
                    dialog.DialogResult = DialogResult.Abort;
                }
            });
        }
        catch (InvalidOperationException)
        {
            // UI is already shutting down.
        }

        return true;
    }

    private void RecordHistory(WorkDayState day)
    {
        _pendingHistory.RemoveAll(p => p.Date == day.Date);
        _pendingHistory.Add(day.Clone());
        FlushHistory(silent: true);
    }

    // Writes pending days; stays pending while Excel has the file open and is retried on later ticks.
    private void FlushHistory(bool silent)
    {
        _lastHistoryAttempt = DateTime.Now;
        try
        {
            foreach (var day in _pendingHistory)
            {
                ClockInHistory.Upsert(day, WorkDay);
            }

            _pendingHistory.Clear();
            _warnedHistoryLocked = false;
        }
        catch (IOException)
        {
            if (!silent && !_warnedHistoryLocked)
            {
                _warnedHistoryLocked = true;
                _tray.ShowBalloonTip(
                    5000, "History not saved yet", "Close ClockInHistory.csv in Excel; it will be saved automatically.", ToolTipIcon.Warning);
            }
        }
    }

    private void UpdateDisplay(DateTime now)
    {
        var weekendOff = _state.ClockIn is null && IsWeekend(now);
        _setItem.Enabled = !weekendOff;
        _clockOutItem.Enabled = !weekendOff;

        if (weekendOff)
        {
            SetDisplay("WE", Color.Gray, "Weekend: the clock is off", "Clock-in tracker: weekend, clock is off");
            return;
        }

        if (_state.ClockIn is not { } clockIn)
        {
            SetDisplay("--", Color.Gray, "Not clocked in", "Clock-in tracker: not clocked in");
            return;
        }

        if (_state.ClockOut is { } clockOut)
        {
            var worked = clockOut - clockIn;
            var how = _state.ClockOutUnknownIssue ? "unknown issue" : _state.ClockOutAuto ? "auto" : "manual";
            var summary = $"Clocked out at {clockOut:HH:mm} ({how})\nWorked {FormatDuration(worked)} (in at {clockIn:HH:mm})";
            SetDisplay("OUT", Color.SteelBlue, summary.Replace("\n", " | "), summary);
            return;
        }

        var elapsed = now - clockIn;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var remaining = WorkDay - elapsed;
        var done = remaining <= TimeSpan.Zero;
        if (done)
        {
            remaining = TimeSpan.Zero;
        }

        // Round up to the minute so the icon reads 0:00 exactly when the 9 hours are reached.
        var iconRemaining = TimeSpan.FromMinutes(Math.Ceiling(remaining.TotalMinutes));
        var iconText = $"{(int)iconRemaining.TotalHours}:{iconRemaining.Minutes:00}";
        var color = done ? Color.Firebrick : remaining < TimeSpan.FromHours(1) ? Color.DarkOrange : Color.SeaGreen;

        var details = $"Remaining {FormatDuration(remaining)}\nWorked {FormatDuration(elapsed)} (in at {clockIn:HH:mm})\nClock out at {clockIn + WorkDay:HH:mm}";
        SetDisplay(iconText, color, details.Replace("\n", " | "), details);
    }

    private void SetDisplay(string iconText, Color color, string menuText, string tooltip)
    {
        _statusItem.Text = menuText;
        // NotifyIcon.Text is limited to 127 characters.
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        if (iconText == _iconText && color == _iconColor)
        {
            return;
        }

        _iconText = iconText;
        _iconColor = color;
        var old = _tray.Icon;
        _tray.Icon = TrayIconRenderer.Render(iconText, color);
        old?.Dispose();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            ClockOutOnLeaving();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            ClockOutOnLeaving();
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => ClockOutOnLeaving();

    // Runs on a system-events thread: touch only state and files here, never UI.
    private void ClockOutOnLeaving()
    {
        lock (_sync)
        {
            AutoClockOutIfDue(DateTime.Now);
        }
    }

    private void ShowStatus() =>
        _tray.ShowBalloonTip(5000, "Clock-in tracker", _statusItem.Text ?? string.Empty, ToolTipIcon.Info);

    private void OpenHistory()
    {
        lock (_sync)
        {
            FlushHistory(silent: true);
        }

        if (!ClockInHistory.Exists)
        {
            _tray.ShowBalloonTip(5000, "Clock-in history", "No history yet.", ToolTipIcon.Info);
            return;
        }

        ClockInHistory.Open();
    }

    private static string FormatDuration(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(RunValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
