using System.Windows;
using Microsoft.Win32;

namespace UsbBackup.App;

public partial class App : Application
{
    public const string BackgroundSwitch = "--background";

    private Mutex? _single;
    private EventWaitHandle? _showSignal;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        var background = e.Args.Contains(BackgroundSwitch, StringComparer.OrdinalIgnoreCase);

        // One copy only: two would both react to the drive and write at the same time.
        _single = new Mutex(true, @"Local\UsbBackup.SingleInstance", out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\UsbBackup.ShowWindow");
        if (!first)
        {
            if (!background) _showSignal.Set(); // ask the running copy to come to the front
            Shutdown();
            return;
        }

        var window = new MainWindow(background);
        MainWindow = window;
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, false);
        if (!background) window.Show();
    }
}

/// <summary>The per-user "run at sign-in" entry. No administrator rights involved.</summary>
internal static class StartupEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsbBackup";

    private static string Command => $"\"{Environment.ProcessPath}\" {App.BackgroundSwitch}";

    /// <summary>True only when the entry exists and points at this copy of the app.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
