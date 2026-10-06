using System.Text.Json;

namespace ClockInTray;

internal sealed class WorkDayState
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClockInTray", "state.json");

    public static bool Exists => File.Exists(FilePath);

    public DateOnly Date { get; set; }
    public DateTime? ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public bool ClockOutAuto { get; set; }
    public bool ClockOutUnknownIssue { get; set; }
    public bool Prompted { get; set; }
    public DateTime? LastSeen { get; set; }

    public static WorkDayState Fresh() => new() { Date = DateOnly.FromDateTime(DateTime.Now) };

    // Returns the saved day, which may be a previous day that still needs closing.
    public static WorkDayState Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<WorkDayState>(File.ReadAllText(FilePath)) is { } state)
            {
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable state: start the day fresh.
        }

        return Fresh();
    }

    public WorkDayState Clone() => (WorkDayState)MemberwiseClone();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

