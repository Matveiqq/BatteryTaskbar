using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatteryTaskbar;

// Тема = класс, который рисует одно устройство на панели задач.
// Чтобы добавить тему: реализуйте IBatteryTheme и добавьте её в Themes.All.
public interface IBatteryTheme
{
    int Id { get; }
    string Name { get; }
    UIElement Build(BatteryDevice device, Brush color);
}

public static class Themes
{
    public static readonly IBatteryTheme[] All =
    {
        new CompactTheme(),
    };

    public static IBatteryTheme Get(int id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}

// Тема 1: иконка и процент
public sealed class CompactTheme : IBatteryTheme
{
    static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

    public int Id => 1;
    public string Name => "Компактный";

    public UIElement Build(BatteryDevice d, Brush color)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = d.Name
        };
        sp.Children.Add(new TextBlock { Text = Glyph(d.Name), FontFamily = IconFont, FontSize = 16, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = d.Level + "%", FontSize = 13, Foreground = color, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    static string Glyph(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("мыш") || n.Contains("mx ") || n.Contains("superlight")) return "\uE962";
        if (n.Contains("key") || n.Contains("клав") || n.Contains("k380") || n.Contains("k70")) return "\uE765";
        if (n.Contains("head") || n.Contains("buds") || n.Contains("pods") || n.Contains("науш") || n.Contains("wh-") || n.Contains("wf-")) return "\uE7F6";
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("геймпад")) return "\uE7FC";
        return "\uE702";
    }
}
