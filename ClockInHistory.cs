using System.Diagnostics;
using System.Text;

namespace ClockInTray;

internal static class ClockInHistory
{
    private const string Header = "Date,Clock in,Planned clock out,Clock out,Worked,Clock-out type";

    private static readonly string FilePath = Path.Combine(ResolveDirectory(), "ClockInHistory.csv");

    public static bool Exists => File.Exists(FilePath);

    // The history lives next to the project; falls back to app data if the project folder is not found.
    private static string ResolveDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClockInTray.csproj")))
            {
                return dir.FullName;
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClockInTray");
    }

    // One row per day: writing the same day again replaces its row. Throws IOException while Excel has the file open.
    public static void Upsert(WorkDayState day, TimeSpan workDay)
    {
        if (day.ClockIn is not { } clockIn)
        {
            return;
        }

        var date = day.Date.ToString("yyyy-MM-dd");
        var clockOut = day.ClockOut;
        var row = string.Join(',',
            date,
            $"{clockIn:HH:mm}",
            $"{clockIn + workDay:HH:mm}",
            clockOut is { } o ? $"{o:HH:mm}" : "",
            clockOut is { } o2 ? FormatWorked(o2 - clockIn) : "",
            clockOut is null ? "" : day.ClockOutUnknownIssue ? "Error: an unknown issue occurred" : day.ClockOutAuto ? "Auto" : "Manual");

        var rows = Read()
            .Where(l => l.Length > 0)
            .Select(Normalize)
            .Where(r => r.Date != day.Date)
            .Append((Date: day.Date, Line: row))
            .OrderBy(r => r.Date)
            .Select(r => r.Line);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllLines(FilePath, rows.Prepend(Header), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // Excel may have re-saved the file with dates in the local format (05/10/2026), so read the date
    // either way and write it back as yyyy-MM-dd.
    private static (DateOnly Date, string Line) Normalize(string line)
    {
        var comma = line.IndexOf(',');
        var field = (comma < 0 ? line : line[..comma]).Trim('"', ' ');
        var parsed = DateOnly.TryParseExact(field, "yyyy-MM-dd", out var date)
            || DateOnly.TryParse(field, System.Globalization.CultureInfo.CurrentCulture, out date);
        return parsed
            ? (date, date.ToString("yyyy-MM-dd") + (comma < 0 ? "" : line[comma..]))
            : (DateOnly.MaxValue, line);
    }

    public static void Open() => Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });

    private static string FormatWorked(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";

    // Shared read access so the history can still be read while Excel has it open.
    private static List<string> Read()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            lines.Add(line);
        }

        return lines.Skip(1).ToList();
    }
}


