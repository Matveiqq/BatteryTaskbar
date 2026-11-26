using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BatteryTaskbar;

// Иконка в трее и короткое меню. Все настройки находятся в окне «Настройки».
public sealed class TrayController : IDisposable
{
    readonly Forms.NotifyIcon tray;
    readonly Forms.ContextMenuStrip menu = new();
    readonly AppSettings settings;
    readonly DeviceMonitor monitor;

    public TrayController(AppSettings s, DeviceMonitor m)
    {
        settings = s;
        monitor = m;
        tray = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Visible = true,
            Text = "Battery Taskbar",
            ContextMenuStrip = menu
        };
        menu.Opening += (_, _) => Rebuild();
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMenu(); };
        monitor.Updated += () => { if (menu.Visible) Rebuild(); };
        Rebuild();
    }

    public void ShowMenu()
    {
        try
        {
            typeof(Forms.NotifyIcon)
                .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(tray, null);
        }
        catch { }
    }

    void Rebuild()
    {
        menu.Items.Clear();
        menu.Items.Add(new Forms.ToolStripMenuItem("Battery Taskbar") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        if (monitor.IsPolling && monitor.Devices.Count == 0)
            menu.Items.Add(new Forms.ToolStripMenuItem("Идёт поиск устройств…") { Enabled = false });
        else if (monitor.Devices.Count == 0)
            menu.Items.Add(new Forms.ToolStripMenuItem(
                monitor.HasPolled ? "Устройства не найдены" : "Идёт поиск устройств…") { Enabled = false });

        foreach (var device in monitor.Devices)
            menu.Items.Add(new Forms.ToolStripMenuItem(
                device.Name + " — " + device.Level + "%" + (device.Stale ? " (нет свежих данных)" : "")));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var refresh = new Forms.ToolStripMenuItem("Обновить");
        refresh.Click += async (_, _) => await monitor.PollAsync();
        menu.Items.Add(refresh);

        var move = new Forms.ToolStripMenuItem("Переместить виджет");
        move.Click += (_, _) =>
        {
            foreach (Window window in Application.Current.Windows)
                if (window is OverlayWindow overlay) overlay.BeginMoveMode();
        };
        menu.Items.Add(move);

        var open = new Forms.ToolStripMenuItem("Настройки…");
        open.Click += (_, _) => SettingsWindow.ShowOrActivate(settings, monitor);
        menu.Items.Add(open);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exit = new Forms.ToolStripMenuItem("Выход");
        exit.Click += (_, _) => { tray.Visible = false; Application.Current.Shutdown(); };
        menu.Items.Add(exit);
    }

    static Icon MakeIcon()
    {
        bool light = SystemInfo.IsLightTheme();
        var outline = light ? Color.FromArgb(40, 40, 40) : Color.White;
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(outline, 2.5f);
            graphics.DrawRectangle(pen, 3, 8, 22, 16);
            using var cap = new SolidBrush(outline);
            graphics.FillRectangle(cap, 26, 13, 3, 6);
            using var fill = new SolidBrush(Color.FromArgb(61, 220, 132));
            graphics.FillRectangle(fill, 6, 11, 14, 10);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        tray.Visible = false;
        tray.Dispose();
    }
}
