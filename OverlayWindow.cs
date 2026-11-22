using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BatteryTaskbar;

public class OverlayWindow : Window
{
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    readonly StackPanel panel = new() { Orientation = Orientation.Horizontal };
    readonly AppSettings settings;
    List<DeviceBattery> last = new();

    public Action? OpenMenu { get; set; }

    public OverlayWindow(AppSettings s)
    {
        settings = s;
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

        // Панель задач тоже "поверх всех" — периодически возвращаем себя наверх
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { Topmost = false; Topmost = true; Reposition(); };
        timer.Start();
    }

    public void Update(List<DeviceBattery> all)
    {
        last = all.ToList();
        Dispatcher.BeginInvoke(new Action(Render));
    }

    public void Refresh() => Dispatcher.BeginInvoke(new Action(Render));

    void Render()
    {
        panel.Children.Clear();
        Brush fg = IsLightTheme() ? Brushes.Black : Brushes.White;
        Brush low = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

        foreach (var d in last.Where(d => !settings.HiddenDevices.Contains(d.Name)))
        {
            Brush b = d.Level <= settings.LowThreshold ? low : fg;
            panel.Children.Add(BuildItem(d, b));
        }
        Reposition();
    }

    // Выбор отрисовки по теме. Новые темы добавлять сюда (и в Themes.All).
    UIElement BuildItem(DeviceBattery d, Brush b)
    {
        switch (settings.Theme)
        {
            case 1:
            default:
                return Compact(d, b);
        }
    }

    UIElement Compact(DeviceBattery d, Brush b)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = d.Name
        };
        sp.Children.Add(new TextBlock { Text = Glyph(d.Name), FontFamily = IconFont, FontSize = 16, Foreground = b, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = d.Level + "%", FontSize = 13, Foreground = b, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    static string Glyph(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("мыш") || n.Contains("mx ") || n.Contains("g502") || n.Contains("g305") || n.Contains("superlight")) return "\uE962";
        if (n.Contains("key") || n.Contains("клав") || n.Contains("k380") || n.Contains("k70") || n.Contains("mx keys")) return "\uE765";
        if (n.Contains("head") || n.Contains("buds") || n.Contains("pods") || n.Contains("науш") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("g pro x")) return "\uE7F6";
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("геймпад")) return "\uE7FC";
        return "\uE702";
    }

    public static bool IsLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (k?.GetValue("SystemUsesLightTheme") as int?) == 1;
        }
        catch { return false; }
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
