using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

namespace BatteryTaskbar;

public sealed class TaskbarSnapshot
{
    // Занятые участки панели (значки приложений, кнопка «Пуск» и т.п.), в DIP
    public List<(double Left, double Right)> Occupied { get; set; } = new();
    // Где начинается область уведомлений (часы, трей), в DIP
    public double TrayStart { get; set; }
}

// Определяет, где на панели задач Windows 11 лежат значки, чтобы виджет не наезжал на них.
// Если определить не получилось, виджет работает как раньше (без защиты от наложения).
public static class TaskbarLayout
{
    // screenWidthPx и результат в физических пикселях; dpiScale переводит их в DIP
    public static TaskbarSnapshot? Read(double screenWidthPx, double dpiScale)
    {
        IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;
        if (!Native.GetWindowRect(taskbar, out Native.NativeRect bar)) return null;

        var root = AutomationElement.FromHandle(taskbar);
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton));
        AutomationElementCollection found = root.FindAll(TreeScope.Descendants, condition);

        var items = new List<(double L, double R)>();
        foreach (AutomationElement element in found)
        {
            System.Windows.Rect rect;
            try { rect = element.Current.BoundingRectangle; }
            catch { continue; }

            if (rect.IsEmpty || rect.Width < 16 || rect.Width > 700) continue;
            if (rect.Top < bar.Top - 6 || rect.Bottom > bar.Bottom + 6) continue;
            items.Add((rect.Left, rect.Right));
        }
        if (items.Count == 0) return null;

        items.Sort((a, b) => a.L.CompareTo(b.L));

        // Склеиваем близкие элементы в группы
        var groups = new List<(double L, double R)>();
        var current = items[0];
        for (int i = 1; i < items.Count; i++)
        {
            var next = items[i];
            if (next.L - current.R <= 40)
                current = (current.L, Math.Max(current.R, next.R));
            else
            {
                groups.Add(current);
                current = next;
            }
        }
        groups.Add(current);

        double trayBorder = screenWidthPx * 0.78;
        var occupied = new List<(double Left, double Right)>();
        double trayStart = screenWidthPx * 0.80;
        bool trayFound = false;
        foreach (var group in groups)
        {
            double center = (group.L + group.R) / 2;
            if (center >= trayBorder)
            {
                trayStart = trayFound ? Math.Min(trayStart, group.L) : group.L;
                trayFound = true;
            }
            else
            {
                occupied.Add((group.L / dpiScale, group.R / dpiScale));
            }
        }

        return new TaskbarSnapshot { Occupied = occupied, TrayStart = trayStart / dpiScale };
    }
}
