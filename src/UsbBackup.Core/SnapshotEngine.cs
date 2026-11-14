using System.Globalization;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsbBackup.Core;

public sealed record BackupProgress(string Phase, long FilesDone, long FilesTotal, long BytesCopied, long BytesToCopy, string? Current);

public sealed class SnapshotManifest
{
    public string Name { get; set; } = "";
    public string Machine { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public List<string> Sources { get; set; } = new();
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public long FilesLinked { get; set; }
    public long CloudOnlySkipped { get; set; }
    public long Excluded { get; set; }
    public long LinksSkipped { get; set; }
    public int ErrorCount { get; set; }
    /// <summary>First errors only (see ErrorCount for the total); the full list is in errors.txt.</summary>
    public List<string> Errors { get; set; } = new();
}

public sealed record SnapshotInfo(string Name, string Path, DateTime When, SnapshotManifest? Manifest)
{
    public override string ToString()
    {
        var text = When.ToString("ddd d MMM yyyy  HH:mm", CultureInfo.CurrentCulture);
        if (Manifest is null) return text;
        text += $"   ·   {Manifest.Files:N0} files, {Sizes.Human(Manifest.Bytes)}   ·   {Manifest.FilesCopied:N0} new or changed";
        if (Manifest.ErrorCount > 0) text += $"   ·   {Manifest.ErrorCount:N0} not copied";
        return text;
    }
}

public sealed class BackupOptions
{
    public IReadOnlyList<string> Excludes { get; init; } = Array.Empty<string>();
    public bool SkipCloudOnlyFiles { get; init; } = true;
    public bool Verify { get; init; }
}

public sealed class InsufficientSpaceException(string message) : IOException(message);

/// <summary>
/// Dated snapshots. Each run creates a complete, browsable folder tree; a file that has not
/// changed since the previous snapshot is a hard link to the copy already on the drive, so it
/// takes no extra space. Deleting any snapshot never damages another.
/// </summary>
public static class SnapshotEngine
{
    public const string ManifestFile = "_snapshot.json";
    public const string ErrorsFile = "_errors.txt";
    private const string PartialSuffix = ".partial";
    private const string NameFormat = "yyyy-MM-dd_HHmmss";
    private static readonly Regex NamePattern = new(@"^\d{4}-\d{2}-\d{2}_\d{6}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static string JobRoot(string driveRoot, string jobName) =>
        Path.Combine(driveRoot, "UsbBackup", Environment.MachineName, jobName);

    // ---- path mapping -------------------------------------------------------------------

    /// <summary>C:\Users\me becomes C\Users\me; \\server\share\x becomes UNC\server\share\x.</summary>
    public static string ToRelative(string fullPath)
    {
        var full = Path.GetFullPath(fullPath).TrimEnd('\\');
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"UNC\" + full.TrimStart('\\');
        if (full.Length >= 2 && full[1] == ':') return full[0] + full[2..];
        throw new ArgumentException($"Not an absolute path: {fullPath}");
    }

    /// <summary>The reverse of <see cref="ToRelative"/>; null when the path is not one we produced.</summary>
    public static string? ToOriginal(string relative)
    {
        relative = relative.Trim('\\');
        if (relative.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + relative[4..];
        if (relative.Length == 1 && char.IsLetter(relative[0])) return relative + @":\";
        if (relative.Length >= 2 && char.IsLetter(relative[0]) && relative[1] == '\\') return relative[0] + ":" + relative[1..];
        return null;
    }

    // ---- listing ------------------------------------------------------------------------

    public static List<SnapshotInfo> List(string jobRoot)
    {
        var result = new List<SnapshotInfo>();
        if (!Directory.Exists(jobRoot)) return result;
        foreach (var dir in Directory.GetDirectories(jobRoot))
        {
            var name = Path.GetFileName(dir);
            if (!NamePattern.IsMatch(name)) continue;
            if (!DateTime.TryParseExact(name, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
            SnapshotManifest? manifest = null;
            try
            {
                var file = Path.Combine(dir, ManifestFile);
                if (File.Exists(file)) manifest = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(file));
            }
            catch (Exception) { /* a snapshot without a readable manifest is still browsable */ }
            result.Add(new SnapshotInfo(name, dir, when, manifest));
        }
        result.Sort((a, b) => b.When.CompareTo(a.When));
        return result;
    }

    // ---- backup -------------------------------------------------------------------------

    private sealed record Item(string Source, string Relative, long Length, DateTime WriteUtc, bool Link);

    public static SnapshotManifest Run(
        IEnumerable<string> sources, string jobRoot, BackupOptions options,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default, DateTime? now = null)
    {
        var started = now ?? DateTime.Now;
        Directory.CreateDirectory(jobRoot);
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(jobRoot))!;
        var drive = new DriveInfo(driveRoot);
        var canLink = string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);

        // Anything left over from an interrupted run is incomplete by definition.
        foreach (var stale in Directory.GetDirectories(jobRoot, "*" + PartialSuffix)) DeleteTree(stale);

        var previous = List(jobRoot).FirstOrDefault();
        var name = started.ToString(NameFormat, CultureInfo.InvariantCulture);
        var finalDir = Path.Combine(jobRoot, name);
        if (Directory.Exists(finalDir)) throw new IOException($"A version named {name} already exists. Wait a second and try again.");
        var workDir = finalDir + PartialSuffix;

        var manifest = new SnapshotManifest { Name = name, Machine = Environment.MachineName, StartedUtc = started.ToUniversalTime() };
        var errors = new List<string>();

        // 1. Scan: decide for every file whether it is new/changed (copy) or unchanged (link).
        var items = new List<Item>();
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Never back the backup store up into itself when a source is on the destination drive.
        var matcher = new ExcludeMatcher(options.Excludes.Append(Path.Combine(driveRoot, "UsbBackup")));
        long lastReport = 0;

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source).TrimEnd('\\');
            if (full.Length == 2 && full[1] == ':') full += "\\";
            manifest.Sources.Add(full);
            if (!Directory.Exists(full)) { errors.Add($"{full}: folder not found"); continue; }
            Scan(new DirectoryInfo(full), ToRelative(full));
        }

        void Scan(DirectoryInfo dir, string relative)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{dir.FullName}: {ex.Message}");
                return;
            }
            if (seen.Add(relative + "\\")) dirs.Add(relative);

            foreach (var entry in entries)
            {
                if (matcher.IsExcluded(entry.FullName, entry.Name)) { manifest.Excluded++; continue; }
                // Junctions and symbolic links point elsewhere; following them risks loops and duplicates.
                if (entry.LinkTarget is not null) { manifest.LinksSkipped++; continue; }
                var rel = relative + "\\" + entry.Name;

                if (entry is DirectoryInfo sub) { Scan(sub, rel); continue; }

                var file = (FileInfo)entry;
                if (options.SkipCloudOnlyFiles &&
                    (file.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
                {
                    manifest.CloudOnlySkipped++;
                    continue;
                }
                if (!seen.Add(rel)) continue; // the same file reached through two overlapping source folders

                var link = false;
                if (canLink && previous is not null)
                {
                    var old = new FileInfo(Path.Combine(previous.Path, rel));
                    link = old.Exists && old.Length == file.Length &&
                           Math.Abs((old.LastWriteTimeUtc - file.LastWriteTimeUtc).TotalSeconds) < 2;
                }
                items.Add(new Item(file.FullName, rel, file.Length, file.LastWriteTimeUtc, link));

                if (Environment.TickCount64 - lastReport > 150)
                {
                    lastReport = Environment.TickCount64;
                    progress?.Report(new BackupProgress("Looking for changes", items.Count, 0, 0, 0, file.FullName));
                }
            }
        }

        // 2. Refuse to start a run that cannot fit, rather than fill the drive and fail halfway.
        var bytesToCopy = items.Where(i => !i.Link).Sum(i => i.Length);
        var needed = bytesToCopy + Math.Max(64L * 1024 * 1024, bytesToCopy / 100);
        if (needed > drive.AvailableFreeSpace)
            throw new InsufficientSpaceException(
                $"Not enough room on {driveRoot} — this backup needs about {Sizes.Human(needed)} and {Sizes.Human(drive.AvailableFreeSpace)} is free.");

        // 3. Build the snapshot.
        try
        {
            Directory.CreateDirectory(workDir);
            foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(workDir, d));

            long done = 0, bytesCopied = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(workDir, item.Relative);
                if (Environment.TickCount64 - lastReport > 100)
                {
                    lastReport = Environment.TickCount64;
                    progress?.Report(new BackupProgress("Copying", done, items.Count, bytesCopied, bytesToCopy, item.Source));
                }

                try
                {
                    if (item.Link && Native.TryCreateHardLink(dest, Path.Combine(previous!.Path, item.Relative)))
                    {
                        manifest.FilesLinked++;
                    }
                    else
                    {
                        File.Copy(item.Source, dest, overwrite: false);
                        var attributes = File.GetAttributes(dest);
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(dest, attributes & ~FileAttributes.ReadOnly);
                        if (options.Verify && !SameContent(item.Source, dest))
                        {
                            File.Delete(dest);
                            throw new IOException("the copy did not match the original (the file may have changed while it was being copied)");
                        }
                        manifest.FilesCopied++;
                        var copied = new FileInfo(dest).Length;
                        manifest.BytesCopied += copied;
                        bytesCopied += item.Length;
                    }
                    manifest.Files++;
                    manifest.Bytes += item.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{item.Source}: {ex.Message}");
                }
                done++;
            }

            manifest.FinishedUtc = DateTime.UtcNow;
            manifest.ErrorCount = errors.Count;
            manifest.Errors = errors.Take(200).ToList();
            if (errors.Count > 0) File.WriteAllLines(Path.Combine(workDir, ErrorsFile), errors);
            File.WriteAllText(Path.Combine(workDir, ManifestFile), JsonSerializer.Serialize(manifest, JsonOptions));

            // The rename is what makes a snapshot count as complete.
            Directory.Move(workDir, finalDir);
            progress?.Report(new BackupProgress("Finished", items.Count, items.Count, bytesCopied, bytesToCopy, null));
            return manifest;
        }
        catch
        {
            try { if (Directory.Exists(workDir)) DeleteTree(workDir); } catch (Exception) { /* cleaned up on the next run */ }
            throw;
        }
    }

    private static bool SameContent(string a, string b)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (fa.Length != fb.Length) return false;
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    // ---- retention ----------------------------------------------------------------------

    /// <summary>Which snapshot names survive. Pure, so the rule can be tested without a disk.</summary>
    public static HashSet<string> SelectToKeep(IEnumerable<(string Name, DateTime When)> snapshots, RetentionPolicy policy)
    {
        var ordered = snapshots.OrderByDescending(s => s.When).ToList();
        var keep = new HashSet<string>();
        if (ordered.Count == 0) return keep;
        keep.Add(ordered[0].Name); // the newest version is never pruned, whatever the numbers say

        foreach (var s in ordered.Take(Math.Max(0, policy.KeepLast))) keep.Add(s.Name);

        void Buckets(int count, Func<DateTime, string> key)
        {
            var used = new HashSet<string>();
            foreach (var s in ordered)
            {
                if (used.Count >= count) break;
                if (used.Add(key(s.When))) keep.Add(s.Name);
            }
        }

        Buckets(policy.KeepDaily, d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Buckets(policy.KeepWeekly, d => $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d)}");
        Buckets(policy.KeepMonthly, d => d.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        return keep;
    }

    /// <summary>Deletes the versions the policy no longer wants and returns their names.</summary>
    public static List<string> Prune(string jobRoot, RetentionPolicy policy, CancellationToken ct = default)
    {
        var all = List(jobRoot);
        var keep = SelectToKeep(all.Select(s => (s.Name, s.When)), policy);
        var removed = new List<string>();
        foreach (var s in all.Where(s => !keep.Contains(s.Name)))
        {
            ct.ThrowIfCancellationRequested();
            // Rename first so a half-deleted version can never be mistaken for a complete one.
            var doomed = s.Path + PartialSuffix;
            Directory.Move(s.Path, doomed);
            DeleteTree(doomed);
            removed.Add(s.Name);
        }
        return removed;
    }

    public static void DeleteTree(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return;
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub && sub.LinkTarget is null) { DeleteTree(sub.FullName); continue; }
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
        if ((dir.Attributes & FileAttributes.ReadOnly) != 0) dir.Attributes &= ~FileAttributes.ReadOnly;
        dir.Delete();
    }

    // ---- restore ------------------------------------------------------------------------

    public sealed record RestoreResult(long Restored, long SkippedExisting, List<string> Errors);

    /// <summary>
    /// Copies a file or folder out of a snapshot. With <paramref name="targetFolder"/> the item is
    /// placed inside that folder under its own name; without it, back where it originally lived.
    /// </summary>
    public static RestoreResult Restore(
        string snapshotPath, string relative, string? targetFolder, bool overwrite,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        relative = relative.Trim('\\');
        var source = relative.Length == 0 ? snapshotPath : Path.Combine(snapshotPath, relative);
        string destination;
        if (targetFolder is not null)
            destination = relative.Length == 0 ? targetFolder : Path.Combine(targetFolder, Path.GetFileName(relative));
        else
            destination = ToOriginal(relative) ?? throw new ArgumentException("Choose a drive, folder or file inside the version to restore it to its original place.");

        long restored = 0, skipped = 0, lastReport = 0;
        var errors = new List<string>();

        void CopyFile(string from, string to)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(to))
                {
                    if (!overwrite) { skipped++; return; }
                    var attributes = File.GetAttributes(to);
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(to, attributes & ~FileAttributes.ReadOnly);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true); // a real copy: the restored file is independent of the backup
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{to}: {ex.Message}");
            }
            if (Environment.TickCount64 - lastReport > 100)
            {
                lastReport = Environment.TickCount64;
                progress?.Report(new BackupProgress("Restoring", restored + skipped, 0, 0, 0, to));
            }
        }

        void CopyDir(string from, string to, bool top)
        {
            Directory.CreateDirectory(to);
            foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
            {
                if (top && relative.Length == 0 && entry.Name is ManifestFile or ErrorsFile) continue;
                if (entry is DirectoryInfo) CopyDir(entry.FullName, Path.Combine(to, entry.Name), false);
                else CopyFile(entry.FullName, Path.Combine(to, entry.Name));
            }
        }

        if (File.Exists(source)) CopyFile(source, destination);
        else if (Directory.Exists(source))
        {
            if (targetFolder is null && relative.Length == 0)
                throw new ArgumentException("Choose a drive, folder or file inside the version to restore it to its original place.");
            CopyDir(source, destination, true);
        }
        else throw new FileNotFoundException("That item is not in this version.", source);

        return new RestoreResult(restored, skipped, errors);
    }
}

internal sealed class ExcludeMatcher
{
    private readonly List<string> _names = new();
    private readonly List<string> _paths = new();

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            if (p.Contains('\\') || p.Contains(':')) _paths.Add(Path.GetFullPath(p).TrimEnd('\\'));
            else _names.Add(p);
        }
    }

    public bool IsExcluded(string fullPath, string name)
    {
        foreach (var n in _names)
            if (FileSystemName.MatchesSimpleExpression(n, name, ignoreCase: true)) return true;
        foreach (var p in _paths)
            if (fullPath.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
