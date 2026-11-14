using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UsbBackup.Core;

namespace UsbBackup.App;

public partial class MainWindow : Window
{
    private const string FoldersJob = "My folders";

    private readonly AppConfig _config = AppConfig.Load();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _imageRunning;
    private bool _loadingDrives;

    // Automatic backup
    private System.Windows.Forms.NotifyIcon? _tray;
    private readonly DispatcherTimer _arrivalTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _destinationSeen;
    private DateTime? _slotHandled; // the daily slot already dealt with, so a failed run is not retried every minute
    private bool _exiting;
    private bool _toldAboutTray;
    private (string Message, bool Warn) _lastOutcome;

    public MainWindow(bool background = false)
    {
        InitializeComponent();
        foreach (var f in _config.Folders) FolderList.Items.Add(f);
        LoadSettingsIntoForm();
        RefreshDrives();
        Log("Ready. Settings are kept in " + AppConfig.DefaultPath);

        SetupTray();
        Closed += (_, _) => { _tray?.Dispose(); Application.Current.Shutdown(); };
        new WindowInteropHelper(this).EnsureHandle(); // so drive notifications arrive even while hidden

        // Opened by hand with the drive already in: that is not an arrival, so nothing runs by itself.
        // Started in the background (at sign-in): a drive that is already in counts as just arrived.
        _destinationSeen = !background && DestinationPresent();
        _arrivalTimer.Tick += (_, _) => { _arrivalTimer.Stop(); CheckDestination(); };
        _pollTimer.Tick += (_, _) => CheckDestination();
        _pollTimer.IsEnabled = _config.AnyAutomatic;
        if (background) _arrivalTimer.Start();
    }

    // ---- automatic backup -----------------------------------------------------------------

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open USB Backup", null, (_, _) => ShowFromTray());
        menu.Items.Add("Back up folders now", null, async (_, _) => await RunFoldersBackup(automatic: true));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray = new System.Windows.Forms.NotifyIcon { Icon = MakeIcon(), Text = "USB Backup", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
    }

    private static System.Drawing.Icon MakeIcon()
    {
        // The same app.ico the window and the .exe use, at the size the notification area wants.
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Notify(string title, string text, bool warn = false) =>
        _tray?.ShowBalloonTip(6000, title, text, warn ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        // Windows sends a burst of these when a drive is plugged in; wait for it to settle, then look once.
        if (message == WM_DEVICECHANGE) { _arrivalTimer.Stop(); _arrivalTimer.Start(); }
        return IntPtr.Zero;
    }

    private bool DestinationPresent() =>
        _config.DestinationSerial is string serial && Drives.ListDestinations().Any(d => d.Serial == serial);

    /// <summary>Notices the chosen drive appearing (plugged in, or unlocked) and backs up if that is due.</summary>
    private async void CheckDestination()
    {
        if (_busy) return; // look again on the next tick
        // A drive that goes away and comes back gets a fresh try at a daily backup that failed.
        if (!DestinationPresent()) { _destinationSeen = false; _slotHandled = null; return; }
        var arrived = !_destinationSeen;
        _destinationSeen = true;

        var now = DateTime.Now;
        // Each daily slot is looked at once: either it is already covered, or one run is attempted.
        var slot = _config.LastScheduledSlot(now);
        var checkSchedule = slot is not null && slot != _slotHandled;
        if (!arrived && !checkSchedule) return;

        if (arrived) RefreshDrives();
        if (!_config.AnyAutomatic || SelectedDrive is not DriveItem drive) return;

        DateTime? last = null;
        try { last = SnapshotEngine.List(SnapshotEngine.JobRoot(drive.Root, FoldersJob)).FirstOrDefault()?.When; }
        catch (Exception ex) { Log("Could not read versions: " + ex.Message); }

        if (checkSchedule)
        {
            _slotHandled = slot;
            if (_config.ScheduledBackupDue(last, now))
            {
                Log($"Daily backup ({_config.DailyBackupTime}) is due — starting.");
                await RunFoldersBackup(automatic: true);
                return;
            }
        }
        if (!arrived || !_config.AutoBackupOnPlugIn) return;

        if (!_config.AutoBackupDue(last, now))
        {
            Log($"Backup drive connected. Last backup was {last:d MMM HH:mm}, less than {_config.AutoMinHours} hour(s) ago, so no automatic backup.");
            return;
        }
        Log("Backup drive connected — starting automatic backup.");
        await RunFoldersBackup(automatic: true);
    }

    /// <summary>The same checks as <see cref="DriveReadyForWriting"/>, but reporting by notification instead of asking.</summary>
    private DriveItem? DriveReadyQuietly()
    {
        RefreshDrives();
        var drive = SelectedDrive;
        if (drive is null)
        {
            Notify("Backup not run", "The backup drive is not connected.", warn: true);
            return null;
        }
        if (!_config.RequireBitLocker) return drive;
        var state = Drives.GetBitLockerState(drive.Root);
        if (state is BitLockerState.On or BitLockerState.Encrypting) return drive;
        Log($"Not backed up: {drive.Letter} is not confirmed as protected by BitLocker ({state}).");
        Notify("Backup not run", $"{drive.Letter} is not protected by BitLocker, so nothing was written to it.", warn: true);
        return null;
    }

    // ---- destination ----------------------------------------------------------------------

    private DriveItem? SelectedDrive => DriveBox.SelectedItem as DriveItem;

    private void RefreshDrives()
    {
        _loadingDrives = true;
        var wanted = SelectedDrive?.Serial ?? _config.DestinationSerial;
        var drives = Drives.ListDestinations();
        // Rebuild the list only when something about it changed, so refreshing on window
        // activation (after every dialog) does not reset what is selected further down.
        if (DriveBox.ItemsSource is not List<DriveItem> current || !current.SequenceEqual(drives))
        {
            DriveBox.ItemsSource = drives;
            DriveBox.SelectedItem = drives.FirstOrDefault(d => d.Serial is not null && d.Serial == wanted);
        }
        _loadingDrives = false;
        DriveChanged();
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        if (!_busy) RefreshDrives(); // picks up a drive that was plugged in or unlocked meanwhile
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDrives) return;
        if (SelectedDrive?.Serial is string serial && serial != _config.DestinationSerial)
        {
            _config.DestinationSerial = serial;
            _config.Save();
        }
        DriveChanged();
    }

    private void DriveChanged()
    {
        var drive = SelectedDrive;
        if (drive is null)
        {
            SetBadge("No drive chosen", "#E5E7EB", "#374151");
            DriveNote.Text = DriveBox.Items.Count == 0 ? "Plug in the USB drive, then press Refresh." : "Choose the drive to back up to.";
            LastBackupText.Text = "";
            LastImageText.Text = "";
        }
        else
        {
            var state = Drives.GetBitLockerState(drive.Root);
            switch (state)
            {
                case BitLockerState.On: SetBadge("BitLocker on", "#DCFCE7", "#166534"); break;
                case BitLockerState.Encrypting: SetBadge("BitLocker encrypting", "#FEF3C7", "#92400E"); break;
                case BitLockerState.Suspended: SetBadge("BitLocker suspended", "#FEF3C7", "#92400E"); break;
                case BitLockerState.Off: SetBadge("Not encrypted", "#FEE2E2", "#991B1B"); break;
                case BitLockerState.Decrypting: SetBadge("BitLocker being removed", "#FEE2E2", "#991B1B"); break;
                default: SetBadge("Encryption unknown", "#FEF3C7", "#92400E"); break;
            }
            DriveNote.Text = drive.IsNtfs
                ? ""
                : $"This drive is {drive.Format}, not NTFS: every version will take full space, and a system image cannot be written to it.";
        }
        RefreshSnapshots();
        RefreshLastImage();
    }

    private void SetBadge(string text, string background, string foreground)
    {
        LockText.Text = text;
        LockBadge.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        LockText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
    }

    /// <summary>The gate every write goes through. Returns the drive only if it is acceptable.</summary>
    private DriveItem? DriveReadyForWriting()
    {
        RefreshDrives();
        var drive = SelectedDrive;
        if (drive is null)
        {
            MessageBox.Show(this, "Choose the drive to back up to first.", "USB Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        if (!_config.RequireBitLocker) return drive;

        var state = Drives.GetBitLockerState(drive.Root);
        if (state is BitLockerState.On or BitLockerState.Encrypting) return drive;

        var what = state switch
        {
            BitLockerState.Off => "is not encrypted",
            BitLockerState.Suspended => "has BitLocker suspended",
            BitLockerState.Decrypting => "is having BitLocker removed",
            _ => "could not be confirmed as encrypted",
        };
        var answer = MessageBox.Show(this,
            $"{drive.Letter} {what}, so nothing has been written to it.\n\n" +
            "Turn on BitLocker for the drive and try again, or switch this check off under Settings.\n\n" +
            "Open the BitLocker settings now?",
            "Drive not protected", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.BitLockerDriveEncryption") { UseShellExecute = true });
        return null;
    }

    // ---- folders job ----------------------------------------------------------------------

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose folders to back up", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var folder in dialog.FolderNames)
        {
            if (_config.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
            _config.Folders.Add(folder);
            FolderList.Items.Add(folder);
        }
        _config.Save();
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var folder in FolderList.SelectedItems.Cast<string>().ToList())
        {
            _config.Folders.Remove(folder);
            FolderList.Items.Remove(folder);
        }
        _config.Save();
    }

    private async void BackupFolders_Click(object sender, RoutedEventArgs e) => await RunFoldersBackup(automatic: false);

    /// <summary>
    /// The folders job. When <paramref name="automatic"/> (drive arrival, or the notification-area
    /// menu) nothing is asked: problems and the result are reported by notification.
    /// </summary>
    private async Task RunFoldersBackup(bool automatic)
    {
        if (_busy) return;
        if (_config.Folders.Count == 0)
        {
            if (automatic) Notify("Backup not run", "No folders are set up to back up yet.", warn: true);
            else MessageBox.Show(this, "Add at least one folder to back up.", "USB Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var drive = automatic ? DriveReadyQuietly() : DriveReadyForWriting();
        if (drive is null) return;

        var jobRoot = SnapshotEngine.JobRoot(drive.Root, FoldersJob);
        var options = new BackupOptions { Excludes = _config.Excludes.ToList(), SkipCloudOnlyFiles = _config.SkipCloudOnlyFiles, Verify = _config.VerifyCopies };
        var sources = _config.Folders.ToList();
        var retention = _config.Retention;

        SetBusy(true, "Looking for changes…");
        Log($"Backing up {sources.Count} folder(s) to {jobRoot}");
        var progress = new Progress<BackupProgress>(ShowProgress);
        var token = _cts!.Token;
        try
        {
            var (manifest, removed) = await Task.Run(() =>
            {
                var m = SnapshotEngine.Run(sources, jobRoot, options, progress, token);
                var pruned = SnapshotEngine.Prune(jobRoot, retention, token);
                return (m, pruned);
            });

            Log($"Version {manifest.Name}: {manifest.Files:N0} files ({Sizes.Human(manifest.Bytes)}); " +
                $"{manifest.FilesCopied:N0} new or changed copied ({Sizes.Human(manifest.BytesCopied)}), {manifest.FilesLinked:N0} unchanged.");
            if (manifest.Excluded > 0) Log($"  Left out by your rules: {manifest.Excluded:N0} item(s).");
            if (manifest.CloudOnlySkipped > 0) Log($"  Cloud-only files skipped: {manifest.CloudOnlySkipped:N0}.");
            if (manifest.LinksSkipped > 0) Log($"  Shortcuts to other locations (junctions/symlinks) skipped: {manifest.LinksSkipped:N0}.");
            if (removed.Count > 0) Log($"  Old versions removed: {string.Join(", ", removed)}.");
            if (manifest.ErrorCount > 0)
            {
                Log($"  COULD NOT COPY {manifest.ErrorCount:N0} item(s):");
                foreach (var error in manifest.Errors.Take(20)) Log("    " + error);
                if (manifest.ErrorCount > 20) Log($"    … full list in {Path.Combine(jobRoot, manifest.Name, SnapshotEngine.ErrorsFile)}");
                Done($"Backup finished, but {manifest.ErrorCount:N0} item(s) could not be copied — see the list below.", warn: true);
            }
            else Done("Backup finished. Everything was copied.");
        }
        catch (OperationCanceledException)
        {
            Log("Stopped. The unfinished version was discarded; earlier versions are untouched.");
            Done("Backup stopped.", warn: true);
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            Done("Backup failed: " + ex.Message, warn: true);
        }
        finally
        {
            SetBusy(false);
            RefreshDrives();
        }
        if (automatic) Notify(_lastOutcome.Warn ? "Backup needs attention" : "Backup finished", _lastOutcome.Message, _lastOutcome.Warn);
    }

    private void ShowProgress(BackupProgress p)
    {
        if (p.FilesTotal > 0)
        {
            Progress.IsIndeterminate = false;
            // Weight by bytes when there is real copying to do, otherwise by file count.
            Progress.Value = p.BytesToCopy > 0
                ? 100.0 * p.BytesCopied / p.BytesToCopy
                : 100.0 * p.FilesDone / p.FilesTotal;
            StatusText.Text = $"{p.Phase}: {p.FilesDone:N0} of {p.FilesTotal:N0} files, {Sizes.Human(p.BytesCopied)} of {Sizes.Human(p.BytesToCopy)} copied   {p.Current}";
        }
        else
        {
            Progress.IsIndeterminate = true;
            StatusText.Text = $"{p.Phase}: {p.FilesDone:N0} files   {p.Current}";
        }
    }

    // ---- system image job -----------------------------------------------------------------

    private void RefreshLastImage()
    {
        var drive = SelectedDrive;
        if (drive is null) { LastImageText.Text = ""; return; }
        try
        {
            var store = new DirectoryInfo(Path.Combine(drive.Root, "WindowsImageBackup"));
            if (!store.Exists) { LastImageText.Text = "No system image on this drive yet."; return; }
            if (_config.LastImageWhen is DateTime when && _config.LastImageSerial is string serial && serial == drive.Serial)
            {
                LastImageText.Text = $"System image of this PC last written {when:d MMM yyyy HH:mm}.";
                return;
            }
            // Windows normally lets only administrators look inside the image folder.
            try
            {
                var mine = store.GetDirectories().FirstOrDefault(d => d.Name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
                LastImageText.Text = mine is not null
                    ? $"Image of this PC on this drive, last written {mine.LastWriteTime:d MMM yyyy HH:mm}."
                    : "This drive holds a system image of another PC, none of this one yet.";
            }
            catch (UnauthorizedAccessException)
            {
                LastImageText.Text = "This drive holds a system image. Windows shows its details only to administrators.";
            }
        }
        catch (Exception) { LastImageText.Text = ""; }
    }

    private async void SystemImage_Click(object sender, RoutedEventArgs e)
    {
        var drive = DriveReadyForWriting();
        if (drive is null) return;
        if (!drive.IsNtfs)
        {
            MessageBox.Show(this, $"A system image needs an NTFS drive. {drive.Letter} is {drive.Format}.", "System image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (drive.Removable)
        {
            MessageBox.Show(this, "Windows will not write a system image to a USB memory stick. Use a USB hard drive or SSD.", "System image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var system = new DriveInfo(SystemImage.SystemDrive + "\\");
        var used = system.TotalSize - system.TotalFreeSpace;
        if (used > drive.FreeBytes)
        {
            MessageBox.Show(this, $"{SystemImage.SystemDrive} holds {Sizes.Human(used)} and {drive.Letter} has {Sizes.Human(drive.FreeBytes)} free.", "Not enough room", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var go = MessageBox.Show(this,
            $"Create a full image of {SystemImage.SystemDrive} (about {Sizes.Human(used)}) on {drive.Letter}?\n\n" +
            "• Windows will ask for administrator approval.\n" +
            "• It can take from tens of minutes to a few hours. You can keep using the PC.\n" +
            "• Leave the drive plugged in until it finishes.",
            "System image", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (go != MessageBoxResult.OK) return;

        SetBusy(true, "Waiting for administrator approval…");
        _imageRunning = true;
        Progress.IsIndeterminate = true;
        Log($"System image: wbadmin {SystemImage.BuildArguments(drive.Letter)}");
        var output = new Progress<string>(line =>
        {
            Log("  " + line);
            StatusText.Text = line;
            if (SystemImage.ParsePercent(line) is int percent)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = percent;
            }
        });
        try
        {
            var result = await SystemImage.RunAsync(drive.Letter, output);
            switch (result.Outcome)
            {
                case SystemImageOutcome.Succeeded:
                    _config.LastImageWhen = DateTime.Now;
                    _config.LastImageSerial = drive.Serial;
                    _config.Save();
                    Done("System image finished.");
                    break;
                case SystemImageOutcome.ElevationDeclined:
                    Log("Administrator approval was not given; nothing was written.");
                    Done("System image not started.", warn: true);
                    break;
                default:
                    Log($"System image did not complete (exit code {result.ExitCode?.ToString() ?? "unknown"}). Full output: {result.LogFile}");
                    Done("System image failed — see the messages below.", warn: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            Done("System image failed: " + ex.Message, warn: true);
        }
        finally
        {
            _imageRunning = false;
            SetBusy(false);
            RefreshDrives();
        }
    }

    // ---- restore --------------------------------------------------------------------------

    private void RefreshSnapshots()
    {
        var drive = SelectedDrive;
        var selected = (SnapshotList.SelectedItem as SnapshotInfo)?.Name;
        var list = new List<SnapshotInfo>();
        try { if (drive is not null) list = SnapshotEngine.List(SnapshotEngine.JobRoot(drive.Root, FoldersJob)); }
        catch (Exception ex) { Log("Could not read versions: " + ex.Message); }

        var unchanged = SnapshotList.ItemsSource is List<SnapshotInfo> shown &&
                        shown.Select(s => s.Path).SequenceEqual(list.Select(s => s.Path));
        if (!unchanged)
        {
            SnapshotList.ItemsSource = list;
            SnapshotList.SelectedItem = list.FirstOrDefault(s => s.Name == selected);
        }
        if (drive is not null)
            LastBackupText.Text = list.Count == 0
                ? "No versions on this drive yet."
                : $"Last backup {list[0].When:d MMM yyyy HH:mm} · {list.Count} version(s) kept";
    }

    private sealed record Node(string Relative, bool IsFolder);

    private void SnapshotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SnapshotTree.Items.Clear();
        UpdateRestoreButtons();
        if (SnapshotList.SelectedItem is not SnapshotInfo snapshot) return;
        try { AddChildren(SnapshotTree.Items, snapshot.Path, "", topLevel: true); }
        catch (Exception ex) { Log("Could not open that version: " + ex.Message); }
    }

    private void AddChildren(ItemCollection target, string snapshotPath, string relative, bool topLevel)
    {
        var dir = new DirectoryInfo(relative.Length == 0 ? snapshotPath : Path.Combine(snapshotPath, relative));
        var entries = dir.GetFileSystemInfos()
            .OrderByDescending(x => x is DirectoryInfo).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var entry in entries)
        {
            if (topLevel && entry.Name is SnapshotEngine.ManifestFile or SnapshotEngine.ErrorsFile) continue;
            var rel = relative.Length == 0 ? entry.Name : relative + "\\" + entry.Name;
            var isFolder = entry is DirectoryInfo;
            // Top-level folders are the drives the files came from.
            var label = topLevel && entry.Name.Length == 1 ? entry.Name + ":" : entry.Name;
            if (entry is FileInfo file) label += $"   ({Sizes.Human(file.Length)}, {file.LastWriteTime:d MMM yyyy HH:mm})";
            var item = new TreeViewItem { Header = label, Tag = new Node(rel, isFolder), FontWeight = isFolder ? FontWeights.SemiBold : FontWeights.Normal };
            if (isFolder)
            {
                item.Items.Add(null); // placeholder so the expander shows; filled on first expand
                item.Expanded += (_, _) =>
                {
                    if (item.Items.Count != 1 || item.Items[0] is not null) return;
                    item.Items.Clear();
                    try { AddChildren(item.Items, snapshotPath, rel, topLevel: false); }
                    catch (Exception ex) { Log("Could not open folder: " + ex.Message); }
                };
            }
            target.Add(item);
        }
    }

    private Node? SelectedNode => (SnapshotTree.SelectedItem as TreeViewItem)?.Tag as Node;

    private void SnapshotTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => UpdateRestoreButtons();

    private void UpdateRestoreButtons()
    {
        var hasSnapshot = SnapshotList.SelectedItem is SnapshotInfo;
        var node = SelectedNode;
        RestoreToButton.IsEnabled = hasSnapshot && !_busy;
        OpenExplorerButton.IsEnabled = hasSnapshot;
        RestoreOriginalButton.IsEnabled = hasSnapshot && node is not null && !_busy;
        RestoreHint.Text = !hasSnapshot ? "Pick a version, then a folder or file."
            : node is null ? "Nothing picked: “Restore to a folder” restores the whole version."
            : "Picked: " + (SnapshotEngine.ToOriginal(node.Relative) ?? node.Relative);
    }

    private async void RestoreTo_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotInfo snapshot) return;
        var relative = SelectedNode?.Relative ?? "";
        var dialog = new OpenFolderDialog { Title = "Restore into which folder?" };
        if (dialog.ShowDialog(this) != true) return;
        await RunRestore(snapshot, relative, dialog.FolderName, overwrite: false);
    }

    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotInfo snapshot || SelectedNode is not Node node) return;
        var original = SnapshotEngine.ToOriginal(node.Relative);
        if (original is null) return;
        var answer = MessageBox.Show(this,
            $"Put this back as it was on {snapshot.When:d MMM yyyy HH:mm}?\n\n{original}\n\n" +
            "Yes — replace files that exist there now with the backed-up copies.\n" +
            "No — only bring back files that are missing; leave existing files alone.",
            "Put back where it was", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Cancel) return;
        await RunRestore(snapshot, node.Relative, null, overwrite: answer == MessageBoxResult.Yes);
    }

    private async Task RunRestore(SnapshotInfo snapshot, string relative, string? target, bool overwrite)
    {
        SetBusy(true, "Restoring…");
        Progress.IsIndeterminate = true;
        var shown = target ?? SnapshotEngine.ToOriginal(relative);
        Log($"Restoring from version {snapshot.Name} to {shown}");
        var progress = new Progress<BackupProgress>(p => StatusText.Text = $"Restoring: {p.FilesDone:N0} files   {p.Current}");
        var token = _cts!.Token;
        try
        {
            var result = await Task.Run(() => SnapshotEngine.Restore(snapshot.Path, relative, target, overwrite, progress, token));
            Log($"  Restored {result.Restored:N0} file(s)" + (result.SkippedExisting > 0 ? $"; left {result.SkippedExisting:N0} existing file(s) alone." : "."));
            foreach (var error in result.Errors.Take(20)) Log("    COULD NOT RESTORE " + error);
            if (result.Errors.Count > 0) Done($"Restore finished, but {result.Errors.Count:N0} file(s) could not be written.", warn: true);
            else Done($"Restore finished: {result.Restored:N0} file(s).");
        }
        catch (OperationCanceledException)
        {
            Log("  Restore stopped part-way; files already restored stay where they are.");
            Done("Restore stopped.", warn: true);
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            Done("Restore failed: " + ex.Message, warn: true);
        }
        finally { SetBusy(false); }
    }

    private void OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotInfo snapshot) return;
        var path = SelectedNode is Node node ? Path.Combine(snapshot.Path, node.Relative) : snapshot.Path;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    // ---- settings -------------------------------------------------------------------------

    private void LoadSettingsIntoForm()
    {
        KeepLastBox.Text = _config.Retention.KeepLast.ToString();
        KeepDailyBox.Text = _config.Retention.KeepDaily.ToString();
        KeepWeeklyBox.Text = _config.Retention.KeepWeekly.ToString();
        KeepMonthlyBox.Text = _config.Retention.KeepMonthly.ToString();
        ExcludesBox.Text = string.Join(Environment.NewLine, _config.Excludes);
        RequireBitLockerBox.IsChecked = _config.RequireBitLocker;
        SkipCloudBox.IsChecked = _config.SkipCloudOnlyFiles;
        VerifyBox.IsChecked = _config.VerifyCopies;
        AutoBox.IsChecked = _config.AutoBackupOnPlugIn;
        AutoHoursBox.Text = _config.AutoMinHours.ToString();
        DailyBox.IsChecked = _config.DailyBackup;
        DailyTimeBox.Text = _config.DailyBackupTime;
        try { StartupBox.IsChecked = StartupEntry.IsEnabled; } catch (Exception) { StartupBox.IsChecked = false; }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var boxes = new[] { KeepLastBox, KeepDailyBox, KeepWeeklyBox, KeepMonthlyBox, AutoHoursBox };
        var values = new int[5];
        for (var i = 0; i < boxes.Length; i++)
        {
            if (!int.TryParse(boxes[i].Text.Trim(), out values[i]) || values[i] < 0 || values[i] > 9999)
            {
                SettingsSaved.Text = "";
                MessageBox.Show(this, "The numbers of versions to keep, and the hours between automatic backups, must be whole numbers from 0 to 9999.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                boxes[i].Focus();
                return;
            }
        }
        if (!AppConfig.TryParseTime(DailyTimeBox.Text, out var dailyTime))
        {
            SettingsSaved.Text = "";
            MessageBox.Show(this, "The daily backup time must be a 24-hour time such as 13:00.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            DailyTimeBox.Focus();
            return;
        }
        _config.DailyBackup = DailyBox.IsChecked == true;
        _config.DailyBackupTime = dailyTime.ToString("HH:mm");
        DailyTimeBox.Text = _config.DailyBackupTime;
        _slotHandled = null; // a changed schedule is looked at afresh
        _config.Retention = new RetentionPolicy { KeepLast = values[0], KeepDaily = values[1], KeepWeekly = values[2], KeepMonthly = values[3] };
        _config.Excludes = ExcludesBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _config.RequireBitLocker = RequireBitLockerBox.IsChecked == true;
        _config.SkipCloudOnlyFiles = SkipCloudBox.IsChecked == true;
        _config.VerifyCopies = VerifyBox.IsChecked == true;
        _config.AutoBackupOnPlugIn = AutoBox.IsChecked == true;
        _config.AutoMinHours = values[4];
        _config.Save();
        _pollTimer.IsEnabled = _config.AnyAutomatic;

        var note = "";
        try
        {
            var wanted = StartupBox.IsChecked == true;
            if (wanted != StartupEntry.IsEnabled) StartupEntry.Set(wanted);
            if (_config.AnyAutomatic && !wanted)
                note = "  Automatic backup only works while the app is running — consider starting it at sign-in.";
        }
        catch (Exception ex) { note = "  Could not change the sign-in setting: " + ex.Message; }
        SettingsSaved.Text = $"Saved {DateTime.Now:HH:mm:ss}.{note}";
    }

    // ---- shared ---------------------------------------------------------------------------

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (busy) { _cts = new CancellationTokenSource(); Progress.Value = 0; }
        else { _cts?.Dispose(); _cts = null; Progress.IsIndeterminate = false; }
        BackupButton.IsEnabled = ImageButton.IsEnabled = AddFolderButton.IsEnabled = RemoveFolderButton.IsEnabled = !busy;
        DriveBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        if (status is not null) { StatusText.Text = status; StatusText.Foreground = Brushes.Black; }
        UpdateRestoreButtons();
    }

    private void Done(string message, bool warn = false)
    {
        _lastOutcome = (message, warn);
        StatusText.Text = message;
        StatusText.Foreground = warn ? new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B)) : new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
        Progress.IsIndeterminate = false;
        Progress.Value = warn ? 0 : 100;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_imageRunning)
        {
            Log("Asking Windows to stop the system image (administrator approval needed)…");
            SystemImage.RequestStop();
        }
        else
        {
            StatusText.Text = "Stopping…";
            _cts?.Cancel();
        }
    }

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // With automatic backup on, the close button only hides the window; Exit is on the icon's menu.
        if (!_exiting && _config.AnyAutomatic)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                Notify("USB Backup is still running", "It stays here so automatic backups can run. Right-click this icon to exit.");
            }
            return;
        }
        if (!_busy) return;
        var message = _imageRunning
            ? "A system image is being written. Closing this window will not stop it, but you will lose the progress display. Close anyway?"
            : "A backup or restore is running. Stop it and close?";
        if (MessageBox.Show(message, "USB Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            _exiting = false;
            return;
        }
        _cts?.Cancel();
    }
}
