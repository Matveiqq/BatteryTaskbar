using System;
using System.Windows;
using System.Windows.Threading;

namespace BatteryTaskbar;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Log.Write("=== запуск Battery Taskbar 1.3.0, " + Environment.OSVersion.VersionString + " ===");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = AppSettings.Load();

        // Источники заряда. Чтобы добавить поддержку нового бренда, напишите класс
        // IBatteryProvider и добавьте его сюда. Порядок задаёт приоритет при дублях.
        var monitor = new DeviceMonitor(
            new BleGattProvider(),
            new PnpBatteryProvider(),
            new WindowsBatteryApiProvider(),
            new LogitechProvider(),
            new FeatureReportProvider());

        var overlay = new OverlayWindow(settings, monitor);
        var tray = new TrayController(settings, monitor);
        overlay.OpenMenu = tray.ShowMenu;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += async (_, _) => await monitor.PollAsync();
        timer.Start();

        app.Startup += (_, _) => { overlay.Show(); _ = monitor.PollAsync(); };
        app.Run();
        tray.Dispose();
    }
}
