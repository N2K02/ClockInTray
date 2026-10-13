namespace ClockInTray;

internal sealed class TimeDialog : Form
{
    private readonly DateTimePicker _picker = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 90,
        Location = new Point(20, 70),
    };

    private readonly DateTime _earliest;
    private readonly string _title;

    public DateTime Time { get; private set; }

    public TimeDialog(string title, string message, DateTime defaultTime, DateTime earliest, string okText, string cancelText)
    {
        _title = title;
        _earliest = earliest;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(340, 160);

        var label = new Label { Text = message, AutoSize = false, Location = new Point(20, 15), Size = new Size(300, 50) };

        _picker.Value = defaultTime;

        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, Location = new Point(110, 115), Size = new Size(110, 32) };
        var cancel = new Button { Text = cancelText, DialogResult = DialogResult.Cancel, Location = new Point(230, 115), Size = new Size(90, 32) };
        ok.Click += OnOk;

        Controls.AddRange([label, _picker, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    // Keeps the picker on the current time (until the user edits it) and shows the time worked past the work day.
    public void EnableLive(DateTime clockIn, TimeSpan workDay, string message)
    {
        var label = Controls.OfType<Label>().First();
        var edited = false;
        var setting = false;
        _picker.ValueChanged += (_, _) => edited |= !setting;

        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        void Refresh()
        {
            var now = DateTime.Now;
            if (!edited)
            {
                setting = true;
                _picker.Value = now;
                setting = false;
            }

            var over = now - clockIn - workDay;
            label.Text = over > TimeSpan.Zero
                ? $"{message}\nOvertime: +{(int)over.TotalHours}:{over.Minutes:00}"
                : message;
        }

        timer.Tick += (_, _) => Refresh();
        Disposed += (_, _) => timer.Dispose();
        Refresh();
        timer.Start();
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var chosen = DateTime.Today.AddHours(_picker.Value.Hour).AddMinutes(_picker.Value.Minute);
        string? error = null;
        if (chosen > DateTime.Now.AddMinutes(1))
        {
            error = "The time cannot be in the future.";
        }
        else if (chosen < _earliest)
        {
            error = $"The time cannot be before {_earliest:HH:mm}.";
        }

        if (error is not null)
        {
            MessageBox.Show(this, error, _title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        Time = chosen;
    }
}
