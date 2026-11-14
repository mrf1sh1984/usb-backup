using System.Runtime.InteropServices;
using System.Text;

namespace UsbBackup.Core;

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName, StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer, int nFileSystemNameSize);

    /// <summary>The Win32 call needs the \\?\ form once a path gets long; .NET's own file APIs do not.</summary>
    private static string Extended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    public static bool TryCreateHardLink(string newFile, string existingFile) =>
        CreateHardLinkW(Extended(newFile), Extended(existingFile), IntPtr.Zero);

    public static string? VolumeSerial(string root)
    {
        if (!root.EndsWith('\\')) root += "\\";
        return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0)
            ? serial.ToString("X8")
            : null;
    }
}
