using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatteryTaskbar;

static class Native
{
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint GW_HWNDPREV = 3;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
}

// Безрамочное прозрачное окно, лежащее поверх левой части панели задач.
public class OverlayWindow : Window
{
    readonly StackPanel panel = new() { Orientation = Orientation.Horizontal };
    readonly AppSettings settings;
    readonly DeviceMonitor monitor;

    public Action? OpenMenu { get; set; }

    public OverlayWindow(AppSettings s, DeviceMonitor m)
    {
        settings = s;
        monitor = m;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // почти прозрачный, но ловит клики
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = panel;
        MouseDown += (_, _) => OpenMenu?.Invoke();

        monitor.Updated += Render;

        // Быстро возвращаем виджет наверх, если панель задач, меню «Пуск» или Alt+Tab его перекрыли
        var keepTop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        keepTop.Tick += (_, _) => KeepOnTop();
        keepTop.Start();

        // Положение пересчитываем реже
        var place = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        place.Tick += (_, _) => Reposition();
        place.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Не забирает фокус при клике и не показывается в Alt+Tab
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
    }

    public void Refresh() => Render();

    void KeepOnTop()
    {
        try
        {
            if (settings.HideInFullscreen && IsFullscreenAppRunning())
            {
                if (IsVisible) Hide();
                return;
            }

            if (!IsVisible) Show();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Если над нами лежит другое окно, поднимаемся обратно (без активации)
            if (Native.GetWindow(hwnd, Native.GW_HWNDPREV) != IntPtr.Zero)
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch { }
    }

    // Полноэкранная игра, видео или презентация
    static bool IsFullscreenAppRunning()
    {
        try
        {
            if (Native.SHQueryUserNotificationState(out int state) != 0) return false;
            // 2 = полноэкранное приложение, 3 = полноэкранная игра (Direct3D), 4 = режим презентации
            return state == 2 || state == 3 || state == 4;
        }
        catch { return false; }
    }

    void Render()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Render));
            return;
        }

        panel.Children.Clear();
        Brush fg = SystemInfo.IsLightTheme() ? Brushes.Black : Brushes.White;
        Brush low = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        var theme = Themes.Get(settings.Theme);

        foreach (var d in monitor.Devices.Where(d => !settings.HiddenDevices.Contains(d.Name)))
            panel.Children.Add(theme.Build(d, d.Level <= settings.LowThreshold ? low : fg));

        Reposition();
    }

    void Reposition()
    {
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double h = panel.DesiredSize.Height;
        var wa = SystemParameters.WorkArea;
        double screenH = SystemParameters.PrimaryScreenHeight;
        double taskbarH = screenH - wa.Height;
        double areaTop = wa.Bottom;
        if (taskbarH < 20) { taskbarH = 48; areaTop = screenH - 48; } // автоскрытие или панель не снизу
        Left = settings.LeftMargin;
        Top = areaTop + (taskbarH - h) / 2;
    }
}
