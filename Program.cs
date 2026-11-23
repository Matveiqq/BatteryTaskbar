using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BatteryTaskbar;

public static class Program
{
    const string Version = "1.4.2";

    [STAThread]
    public static void Main()
    {
        // Любая необработанная ошибка попадает в журнал, а при падении на старте показывается окно с текстом
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash("Необработанная ошибка", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("Ошибка в фоновой задаче: " + e.Exception);
            e.SetObserved();
        };

        try
        {
            Run();
        }
        catch (Exception ex)
        {
            ReportCrash("Ошибка при запуске", ex);
        }
    }

    static void Run()
    {
        Log.Write("=== запуск Battery Taskbar " + Version + ", " + Environment.OSVersion.VersionString + " ===");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Ошибка в интерфейсе не должна убивать программу: пишем в журнал и продолжаем
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("Ошибка интерфейса: " + e.Exception);
            e.Handled = true;
        };
        Log.Write("Шаг 1: приложение создано");

        var settings = AppSettings.Load();
        Log.Write("Шаг 2: настройки загружены");

        // Источники заряда. Чтобы добавить поддержку нового бренда, напишите класс
        // IBatteryProvider и добавьте его сюда. Порядок задаёт приоритет при дублях.
        var monitor = new DeviceMonitor(
            new BleGattProvider(),
            new PnpBatteryProvider(),
            new WindowsBatteryApiProvider(),
            new LogitechProvider(),
            new FeatureReportProvider(),
            new HaloBatteryBridgeProvider(settings));
        Log.Write("Шаг 3: источники созданы");

        var overlay = new OverlayWindow(settings, monitor);
        Log.Write("Шаг 4: виджет создан");

        var tray = new TrayController(settings, monitor);
        overlay.OpenMenu = tray.ShowMenu;
        Log.Write("Шаг 5: значок в трее создан");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += async (_, _) => await monitor.PollAsync();
        timer.Start();

        app.Startup += (_, _) =>
        {
            Log.Write("Шаг 6: запуск окна и первого опроса");
            overlay.Show();
            _ = monitor.PollAsync();
        };
        app.Run();
        tray.Dispose();
    }

    static void ReportCrash(string title, Exception? ex)
    {
        Log.Write(title + ": " + (ex?.ToString() ?? "причина неизвестна"));
        try
        {
            MessageBox.Show(
                title + ":\n\n" + (ex?.Message ?? "причина неизвестна") +
                "\n\nПодробности в файле:\n" + Log.FilePath,
                "Battery Taskbar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
