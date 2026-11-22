using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatteryTaskbar;

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

        // Панель задач тоже "поверх всех": периодически возвращаем себя наверх
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { Topmost = false; Topmost = true; Reposition(); };
        timer.Start();
    }

    public void Refresh() => Render();

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
