using UsbBackup.Core;

namespace UsbBackup.Tests;

public sealed class ScheduleTests
{
    private static AppConfig Daily(string time = "13:00") => new() { DailyBackup = true, DailyBackupTime = time };
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    [Fact]
    public void Off_is_never_due()
    {
        var config = new AppConfig { DailyBackupTime = "13:00" };
        Assert.Null(config.LastScheduledSlot(At(14, 15)));
        Assert.False(config.ScheduledBackupDue(null, At(14, 15)));
    }

    [Fact]
    public void Slot_is_today_once_the_time_has_passed_and_yesterday_before_it()
    {
        Assert.Equal(At(14, 13), Daily().LastScheduledSlot(At(14, 13)));
        Assert.Equal(At(14, 13), Daily().LastScheduledSlot(At(14, 23, 59)));
        Assert.Equal(At(13, 13), Daily().LastScheduledSlot(At(14, 12, 59)));
    }

    [Fact]
    public void Not_due_before_the_time_when_yesterday_was_covered()
    {
        // Backed up yesterday evening; it is this morning.
        Assert.False(Daily().ScheduledBackupDue(At(13, 18), At(14, 12, 59)));
    }

    [Fact]
    public void Due_at_the_time_and_not_again_once_a_version_exists()
    {
        Assert.True(Daily().ScheduledBackupDue(At(14, 10, 35), At(14, 13)));
        Assert.False(Daily().ScheduledBackupDue(At(14, 13, 1), At(14, 13, 2)));
        Assert.False(Daily().ScheduledBackupDue(At(14, 13, 1), At(15, 12, 59)));
        Assert.True(Daily().ScheduledBackupDue(At(14, 13, 1), At(15, 13)));
    }

    [Fact]
    public void A_missed_run_is_still_owed_later()
    {
        // Drive was away at 13:00 on the 14th; it comes back the next morning.
        Assert.True(Daily().ScheduledBackupDue(At(13, 13, 5), At(15, 9)));
    }

    [Fact]
    public void A_backup_by_hand_after_the_time_counts()
    {
        Assert.False(Daily().ScheduledBackupDue(At(14, 16), At(14, 17)));
    }

    [Fact]
    public void No_versions_yet_is_due()
    {
        Assert.True(Daily().ScheduledBackupDue(null, At(14, 8)));
    }

    [Theory]
    [InlineData("13:00", true)]
    [InlineData("9:05", true)]
    [InlineData(" 09:05 ", true)]
    [InlineData("24:00", false)]
    [InlineData("1pm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Time_parsing(string? text, bool ok)
    {
        Assert.Equal(ok, AppConfig.TryParseTime(text, out _));
    }

    [Fact]
    public void Unreadable_time_is_never_due()
    {
        Assert.False(Daily("soon").ScheduledBackupDue(null, At(14, 15)));
    }

    [Fact]
    public void Settings_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "UsbBackupTests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Daily("07:30").Save(path);
            var loaded = AppConfig.Load(path);
            Assert.True(loaded.DailyBackup);
            Assert.Equal("07:30", loaded.DailyBackupTime);
            Assert.DoesNotContain("AnyAutomatic", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
