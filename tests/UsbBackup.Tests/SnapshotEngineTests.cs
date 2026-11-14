using System.Runtime.InteropServices;
using UsbBackup.Core;

namespace UsbBackup.Tests;

public sealed class SnapshotEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UsbBackupTests-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _job;
    private readonly DateTime _t0 = new(2026, 11, 14, 9, 0, 0);

    public SnapshotEngineTests()
    {
        _source = Path.Combine(_root, "source");
        _job = Path.Combine(_root, "dest", "My folders");
        Directory.CreateDirectory(Path.Combine(_source, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(_source, "empty"));
        Directory.CreateDirectory(Path.Combine(_source, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_source, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_source, "sub", "b.txt"), "bravo");
        File.WriteAllText(Path.Combine(_source, "sub", "deep", "c.txt"), "charlie");
        File.WriteAllText(Path.Combine(_source, "node_modules", "pkg", "index.js"), "x");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) SnapshotEngine.DeleteTree(_root);
    }

    private SnapshotManifest Backup(DateTime when, params string[] excludes) =>
        SnapshotEngine.Run(new[] { _source }, _job, new BackupOptions { Excludes = excludes, Verify = true }, now: when);

    private string InSnapshot(string snapshotName, params string[] parts) =>
        Path.Combine(new[] { _job, snapshotName, SnapshotEngine.ToRelative(_source) }.Concat(parts).ToArray());

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>How many names on the disk share this file's data.</summary>
    private static int LinkCount(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(GetFileInformationByHandle(stream.SafeFileHandle, out var info));
        return (int)info.NumberOfLinks;
    }

    [Fact]
    public void FirstRunCopiesEverythingIncludingEmptyFolders()
    {
        var m = Backup(_t0, "node_modules");

        Assert.Equal(3, m.Files);
        Assert.Equal(3, m.FilesCopied);
        Assert.Equal(0, m.FilesLinked);
        Assert.Equal(0, m.ErrorCount);
        Assert.Equal(1, m.Excluded);
        Assert.Equal("charlie", File.ReadAllText(InSnapshot(m.Name, "sub", "deep", "c.txt")));
        Assert.True(Directory.Exists(InSnapshot(m.Name, "empty")));
        Assert.False(Directory.Exists(InSnapshot(m.Name, "node_modules")));
        Assert.Empty(Directory.GetDirectories(_job, "*.partial"));
    }

    [Fact]
    public void SecondRunLinksUnchangedCopiesChangedAndDropsDeleted()
    {
        var first = Backup(_t0, "node_modules");

        File.WriteAllText(Path.Combine(_source, "a.txt"), "alpha, edited");
        File.Delete(Path.Combine(_source, "sub", "b.txt"));
        File.WriteAllText(Path.Combine(_source, "new.txt"), "new");

        var second = Backup(_t0.AddHours(1), "node_modules");

        Assert.Equal(3, second.Files);
        Assert.Equal(2, second.FilesCopied);   // a.txt (changed) and new.txt
        Assert.Equal(1, second.FilesLinked);   // c.txt

        // Each version shows the files as they were at that moment.
        Assert.Equal("alpha", File.ReadAllText(InSnapshot(first.Name, "a.txt")));
        Assert.Equal("alpha, edited", File.ReadAllText(InSnapshot(second.Name, "a.txt")));
        Assert.True(File.Exists(InSnapshot(first.Name, "sub", "b.txt")));
        Assert.False(File.Exists(InSnapshot(second.Name, "sub", "b.txt")));

        // The unchanged file is stored once and appears in both versions.
        Assert.Equal(2, LinkCount(InSnapshot(second.Name, "sub", "deep", "c.txt")));
        Assert.Equal(1, LinkCount(InSnapshot(second.Name, "a.txt")));
    }

    [Fact]
    public void DeletingAnOldVersionLeavesTheNewerOneIntact()
    {
        Backup(_t0);
        var second = Backup(_t0.AddDays(1));

        var removed = SnapshotEngine.Prune(_job, new RetentionPolicy { KeepLast = 1, KeepDaily = 0, KeepWeekly = 0, KeepMonthly = 0 });

        Assert.Single(removed);
        var left = SnapshotEngine.List(_job);
        Assert.Single(left);
        Assert.Equal(second.Name, left[0].Name);
        Assert.Equal("charlie", File.ReadAllText(InSnapshot(second.Name, "sub", "deep", "c.txt")));
        Assert.Equal(1, LinkCount(InSnapshot(second.Name, "sub", "deep", "c.txt")));
    }

    [Fact]
    public void RestoreToAFolderAndBackToTheOriginalPlace()
    {
        var m = Backup(_t0);
        var snapshot = Path.Combine(_job, m.Name);
        var rel = SnapshotEngine.ToRelative(_source);

        var target = Path.Combine(_root, "restored");
        var r1 = SnapshotEngine.Restore(snapshot, rel + @"\sub", target, overwrite: false);
        Assert.Equal(2, r1.Restored);
        Assert.Equal("charlie", File.ReadAllText(Path.Combine(target, "sub", "deep", "c.txt")));

        // Lose a file and damage another, then put the folder back where it was.
        File.Delete(Path.Combine(_source, "sub", "b.txt"));
        File.WriteAllText(Path.Combine(_source, "a.txt"), "damaged");

        var keep = SnapshotEngine.Restore(snapshot, rel, null, overwrite: false);
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(_source, "sub", "b.txt")));
        Assert.Equal("damaged", File.ReadAllText(Path.Combine(_source, "a.txt")));   // existing files untouched
        Assert.True(keep.SkippedExisting > 0);

        SnapshotEngine.Restore(snapshot, rel + @"\a.txt", null, overwrite: true);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_source, "a.txt")));

        // A restored file is independent: editing it must not alter the backup.
        File.WriteAllText(Path.Combine(_source, "a.txt"), "edited after restore");
        Assert.Equal("alpha", File.ReadAllText(InSnapshot(m.Name, "a.txt")));
    }

    [Fact]
    public void ALockedFileIsReportedAndTheRestStillBacksUp()
    {
        using (new FileStream(Path.Combine(_source, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var m = Backup(_t0);
            Assert.Equal(1, m.ErrorCount);
            Assert.Contains("a.txt", m.Errors[0]);
            Assert.Equal(3, m.Files); // b, c and node_modules\pkg\index.js
            Assert.True(File.Exists(Path.Combine(_job, m.Name, SnapshotEngine.ErrorsFile)));
        }
    }

    [Fact]
    public void CancellingLeavesNoHalfFinishedVersion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SnapshotEngine.Run(new[] { _source }, _job, new BackupOptions(), ct: cts.Token, now: _t0));
        Assert.Empty(SnapshotEngine.List(_job));
        Assert.Empty(Directory.GetDirectories(_job));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents", @"C\Users\me\Documents")]
    [InlineData(@"D:\", "D")]
    [InlineData(@"\\nas\share\x", @"UNC\nas\share\x")]
    public void PathMappingRoundTrips(string original, string relative)
    {
        Assert.Equal(relative, SnapshotEngine.ToRelative(original));
        Assert.Equal(original, SnapshotEngine.ToOriginal(relative));
    }

    [Fact]
    public void RetentionKeepsRecentDailyWeeklyAndMonthly()
    {
        // One version a day for 120 days, newest on 14 Nov 2026.
        var newest = new DateTime(2026, 11, 14, 18, 0, 0);
        var all = Enumerable.Range(0, 120).Select(i => newest.AddDays(-i)).Select(d => (Name: d.ToString("yyyy-MM-dd"), When: d)).ToList();

        var keep = SnapshotEngine.SelectToKeep(all, new RetentionPolicy { KeepLast = 3, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 3 });

        Assert.Contains("2026-11-14", keep);                 // newest
        Assert.Contains("2026-11-08", keep);                 // 7th day back
        Assert.DoesNotContain("2026-11-06", keep);           // older than the dailies, not a week or month end
        Assert.Contains("2026-10-31", keep);                 // newest of October
        Assert.Contains("2026-09-30", keep);                 // newest of September
        Assert.DoesNotContain("2026-08-31", keep);           // a fourth month is not kept
        Assert.Equal(11, keep.Count);                        // 7 dailies + 3 older weeks + 2 older months, minus one overlap
    }

    [Fact]
    public void TheNewestVersionSurvivesEvenWithZeroRetention()
    {
        var keep = SnapshotEngine.SelectToKeep(
            new[] { ("a", new DateTime(2026, 1, 1)), ("b", new DateTime(2026, 1, 2)) },
            new RetentionPolicy { KeepLast = 0, KeepDaily = 0, KeepWeekly = 0, KeepMonthly = 0 });
        Assert.Equal(new[] { "b" }, keep.ToArray());
    }
}
