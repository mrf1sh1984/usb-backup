namespace UsbBackup.Core;

public enum BitLockerState { Unknown, On, Off, Encrypting, Decrypting, Suspended, Locked }

public sealed record DriveItem(string Root, string Label, string Format, long FreeBytes, long TotalBytes, string? Serial, bool Removable)
{
    public string Letter => Root.TrimEnd('\\');
    public bool IsNtfs => string.Equals(Format, "NTFS", StringComparison.OrdinalIgnoreCase);
    public override string ToString() =>
        $"{Letter}  {(string.IsNullOrWhiteSpace(Label) ? "(no label)" : Label)}  ·  {Format}  ·  {Sizes.Human(FreeBytes)} free of {Sizes.Human(TotalBytes)}";
}

public static class Drives
{
    /// <summary>Local drives that could hold a backup: everything ready except the Windows drive.</summary>
    public static List<DriveItem> ListDestinations()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var list = new List<DriveItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                if (!d.IsReady) continue;
                if (string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new DriveItem(d.Name, d.VolumeLabel, d.DriveFormat, d.AvailableFreeSpace, d.TotalSize,
                    Native.VolumeSerial(d.Name), d.DriveType == DriveType.Removable));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>
    /// Reads the same BitLocker status Explorer shows, which works without administrator rights.
    /// Call from an STA thread (the UI thread).
    /// </summary>
    public static BitLockerState GetBitLockerState(string root)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return BitLockerState.Unknown;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic? folder = shell.NameSpace(root.TrimEnd('\\'));
            if (folder is null) return BitLockerState.Unknown;
            object? value = folder.Self.ExtendedProperty("System.Volume.BitLockerProtection");
            return value is null ? BitLockerState.Unknown : Convert.ToInt32(value) switch
            {
                1 => BitLockerState.On,
                2 => BitLockerState.Off,
                3 => BitLockerState.Encrypting,
                4 => BitLockerState.Decrypting,
                5 => BitLockerState.Suspended,
                6 => BitLockerState.Locked,
                _ => BitLockerState.Unknown,
            };
        }
        catch (Exception)
        {
            return BitLockerState.Unknown;
        }
    }
}

public static class Sizes
{
    public static string Human(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} bytes" : $"{v:N1} {units[i]}";
    }
}
