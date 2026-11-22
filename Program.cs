using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BatteryTaskbar;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Log.Write("=== запуск Battery Taskbar 0.3.1, " + Environment.OSVersion.VersionString + " ===");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = AppSettings.Load();
        var overlay = new OverlayWindow(settings);
        var devices = new List<DeviceBattery>();
        bool polling = false;
        bool firstDone = false;

        var tray = new Forms.NotifyIcon { Icon = MakeIcon(), Visible = true, Text = "Battery Taskbar" };
        var menu = new Forms.ContextMenuStrip();
        tray.ContextMenuStrip = menu;

        async void Poll()
        {
            if (polling) return;
            polling = true;
            try
            {
                var fresh = await BatteryService.ReadAllAsync();
                devices.Clear();
                devices.AddRange(fresh);
                overlay.Update(fresh);
            }
            catch (Exception ex)
            {
                Log.Write("Poll ошибка: " + ex.Message);
            }
            finally
            {
                polling = false;
                firstDone = true;
            }
        }

        void Rebuild()
        {
            menu.Items.Clear();
            menu.Items.Add(new Forms.ToolStripMenuItem("Battery Taskbar") { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());

            if (devices.Count == 0)
                menu.Items.Add(new Forms.ToolStripMenuItem(firstDone ? "Устройства не найдены" : "Идёт поиск устройств…") { Enabled = false });
            foreach (var d in devices)
                menu.Items.Add(new Forms.ToolStripMenuItem(d.Name + " — " + d.Level + "%"));
            menu.Items.Add(new Forms.ToolStripSeparator());

            var show = new Forms.ToolStripMenuItem("Показывать на панели");
            var names = devices.Select(d => d.Name).Union(settings.HiddenDevices).OrderBy(n => n).ToList();
            if (names.Count == 0) show.DropDownItems.Add(new Forms.ToolStripMenuItem("Нет устройств") { Enabled = false });
            foreach (var n in names)
            {
                var name = n;
                var item = new Forms.ToolStripMenuItem(name) { Checked = !settings.HiddenDevices.Contains(name) };
                item.Click += (_, _) =>
                {
                    if (!settings.HiddenDevices.Remove(name)) settings.HiddenDevices.Add(name);
                    settings.Save();
                    overlay.Refresh();
                };
                show.DropDownItems.Add(item);
            }
            menu.Items.Add(show);

            var themeMenu = new Forms.ToolStripMenuItem("Тема");
            foreach (var t in Themes.All)
            {
                var id = t.Id;
                var item = new Forms.ToolStripMenuItem(t.Id + " — " + t.Name) { Checked = settings.Theme == id };
                item.Click += (_, _) => { settings.Theme = id; settings.Save(); overlay.Refresh(); };
                themeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(themeMenu);

            var low = new Forms.ToolStripMenuItem("Красный цвет при заряде ниже");
            foreach (var p in new[] { 10, 15, 20, 30, 50 })
            {
                var value = p;
                var item = new Forms.ToolStripMenuItem(value + "%") { Checked = settings.LowThreshold == value };
                item.Click += (_, _) => { settings.LowThreshold = value; settings.Save(); overlay.Refresh(); };
                low.DropDownItems.Add(item);
            }
            menu.Items.Add(low);

            menu.Items.Add(new Forms.ToolStripSeparator());
            var refresh = new Forms.ToolStripMenuItem("Обновить сейчас");
            refresh.Click += (_, _) => Poll();
            menu.Items.Add(refresh);

            var auto = new Forms.ToolStripMenuItem("Запускать вместе с Windows") { Checked = Autostart.IsEnabled() };
            auto.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled());
            menu.Items.Add(auto);

            var diag = new Forms.ToolStripMenuItem("Диагностика (открыть журнал)");
            diag.Click += (_, _) =>
            {
                try
                {
                    Log.Write("(журнал открыт пользователем)");
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Log.FilePath + "\"") { UseShellExecute = true });
                }
                catch { }
            };
            menu.Items.Add(diag);

            var exit = new Forms.ToolStripMenuItem("Выход");
            exit.Click += (_, _) => { tray.Visible = false; app.Shutdown(); };
            menu.Items.Add(exit);
        }

        menu.Opening += (_, _) => Rebuild();
        Rebuild();

        void ShowMenu()
        {
            try
            {
                typeof(Forms.NotifyIcon)
                    .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(tray, null);
            }
            catch { }
        }
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMenu(); };
        overlay.OpenMenu = ShowMenu;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => Poll();
        timer.Start();

        app.Startup += (_, _) => { overlay.Show(); Poll(); };
        app.Run();
        tray.Dispose();
    }

    static Icon MakeIcon()
    {
        bool light = OverlayWindow.IsLightTheme();
        var outline = light ? Color.FromArgb(40, 40, 40) : Color.White;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(outline, 2.5f);
            g.DrawRectangle(pen, 3, 8, 22, 16);
            using var cap = new SolidBrush(outline);
            g.FillRectangle(cap, 26, 13, 3, 6);
            using var fill = new SolidBrush(Color.FromArgb(61, 220, 132));
            g.FillRectangle(fill, 6, 11, 14, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
