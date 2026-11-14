# USB Backup

A small Windows desktop app that backs a PC up to a USB drive.

> **Status: a personal project, shared as-is.** The folder backup engine has automated tests and
> has been used for real backups. The system image job, restoring from the window, the
> plug-in trigger on a physical drive and the start-at-sign-in option have not been verified
> end to end. Test a restore yourself before relying on it, and keep another backup.

- **My folders** — backs up the folders you choose as dated versions under
  `<drive>:\UsbBackup\<PC name>\My folders\<yyyy-MM-dd_HHmmss>`. Each version is a complete,
  browsable folder tree. Files that have not changed since the previous version are NTFS hard
  links, so they take no extra room. Old versions are pruned by a keep rule (most recent,
  daily, weekly, monthly).
- **System image** — a full image of the Windows drive made with Windows' own tool (`wbadmin`),
  after an administrator prompt. Saved as `WindowsImageBackup` on the drive and restored from
  Windows recovery (Troubleshoot → Advanced options → System Image Recovery).
- **Restore** — browse any version and restore a file or folder to a folder you pick, or back
  where it was.
- **BitLocker check** — refuses to write unless the destination drive reports BitLocker on
  (can be switched off in Settings).
- **Automatic backup** — optionally runs the folders job when the backup drive is plugged in or
  unlocked, and/or once a day at a set time (a run missed because the drive was away happens
  when it is next connected). Runs from a notification-area icon and can start at sign-in.

## Layout

| Path | What |
| --- | --- |
| `src/UsbBackup.Core` | The engine: snapshots, pruning, restore, drive and BitLocker checks, system image |
| `src/UsbBackup.App` | The WPF window and notification-area icon |
| `tests/UsbBackup.Tests` | xunit tests for the engine |
| `tools/make-icon.ps1` | Draws `src/UsbBackup.App/app.ico` |

## Build

Needs the .NET 8 SDK on Windows.

```
dotnet test
dotnet publish src/UsbBackup.App -c Release -o publish
```

Run `publish\UsbBackup.App.exe`. Settings are kept in `%APPDATA%\UsbBackup\config.json`.

## Limits

- Files held open exclusively by another program are skipped and listed in the log and in
  `_errors.txt` inside the version.
- Cloud-only OneDrive / Google Drive files are skipped by default.
- Junctions and symbolic links are not followed.
- Hard-linked versions need an NTFS destination; on other file systems every version is a full copy.

## Licence

MIT — see [LICENSE](LICENSE).
