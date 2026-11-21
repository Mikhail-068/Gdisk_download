using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows;

namespace DriveDownload;

public partial class App : Application
{
    private Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstance = new Mutex(true, @"Local\DriveDownload-Windows", out var first);
        if (!first)
        {
            MessageBox.Show("Загрузчик уже открыт.", "Диск Загрузки");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
internal static class DarkChrome
{
    internal static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}