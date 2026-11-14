using System.Text.Json;

namespace UsbBackup.Core;

/// <summary>How many dated versions to keep. A version is kept if any rule wants it.</summary>
public sealed class RetentionPolicy
{
    /// <summary>Always keep this many most recent versions.</summary>
    public int KeepLast { get; set; } = 10;
    /// <summary>Keep the newest version of each of the last N days that have one.</summary>
    public int KeepDaily { get; set; } = 14;
    /// <summary>Keep the newest version of each of the last N weeks that have one.</summary>
    public int KeepWeekly { get; set; } = 8;
    /// <summary>Keep the newest version of each of the last N months that have one.</summary>
    public int KeepMonthly { get; set; } = 12;
}

public sealed class AppConfig
{
    public List<string> Folders { get; set; } = new();

    /// <summary>
    /// Names (wildcards allowed) or full paths to leave out. A name matches any file or
    /// folder with that name at any depth; a full path matches that item and all below it.
    /// </summary>
    public List<string> Excludes { get; set; } = new()
    {
        "node_modules",
        "$RECYCLE.BIN",
        "System Volume Information",
    };

    /// <summary>Volume serial of the chosen destination, so a changed drive letter does not matter.</summary>
    public string? DestinationSerial { get; set; }

    public bool RequireBitLocker { get; set; } = true;
    public bool SkipCloudOnlyFiles { get; set; } = true;
    public bool VerifyCopies { get; set; }
    public RetentionPolicy Retention { get; set; } = new();

    /// <summary>Run the folders job by itself when the destination drive appears (plugged in or unlocked).</summary>
    public bool AutoBackupOnPlugIn { get; set; }

    /// <summary>Automatic runs are skipped while the newest version is younger than this.</summary>
    public int AutoMinHours { get; set; } = 4;

    /// <summary>
    /// Whether an automatic run is due, given when the newest version was made (null = none yet).
    /// </summary>
    public bool AutoBackupDue(DateTime? lastVersion, DateTime now) =>
        AutoBackupOnPlugIn && (lastVersion is null || now - lastVersion.Value >= TimeSpan.FromHours(AutoMinHours));

    /// <summary>Also run the folders job once a day at <see cref="DailyBackupTime"/>, while the drive is connected.</summary>
    public bool DailyBackup { get; set; }

    /// <summary>Time of day for the daily backup, 24-hour "HH:mm".</summary>
    public string DailyBackupTime { get; set; } = "13:00";

    /// <summary>Reads a 24-hour time such as "13:00" or "9:05".</summary>
    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), new[] { "H:mm", "HH:mm" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);

    /// <summary>
    /// The most recent moment the daily backup was meant to run: today's time if it has passed,
    /// otherwise yesterday's. Null when the daily backup is off or the time is unreadable.
    /// </summary>
    public DateTime? LastScheduledSlot(DateTime now)
    {
        if (!DailyBackup || !TryParseTime(DailyBackupTime, out var time)) return null;
        var today = now.Date + time.ToTimeSpan();
        return today <= now ? today : today.AddDays(-1);
    }

    /// <summary>
    /// Whether the daily backup is owed: no version has been made since the last scheduled moment.
    /// A run missed because the drive was away is therefore still owed when the drive comes back,
    /// and any backup made after the scheduled moment (by hand or on plug-in) counts.
    /// </summary>
    public bool ScheduledBackupDue(DateTime? lastVersion, DateTime now) =>
        LastScheduledSlot(now) is DateTime slot && (lastVersion is null || lastVersion.Value < slot);

    /// <summary>
    /// When this app last finished a system image, and on which drive. Windows only lets
    /// administrators look inside WindowsImageBackup, so the app cannot read this off the drive.
    /// </summary>
    public DateTime? LastImageWhen { get; set; }
    public string? LastImageSerial { get; set; }

    /// <summary>Whether the app has any reason to keep running in the notification area.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AnyAutomatic => AutoBackupOnPlugIn || DailyBackup;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>USBBACKUP_CONFIG points the app at another settings file (used for testing).</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("USBBACKUP_CONFIG") is { Length: > 0 } custom ? custom :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsbBackup", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection rather than silently overwriting it.
            File.Copy(path, path + ".broken", overwrite: true);
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
