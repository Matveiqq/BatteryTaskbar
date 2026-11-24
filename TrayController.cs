using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BatteryTaskbar;

// Иконка в трее (скрытые значки ^) и меню настроек.
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

        // после каждого опроса обновляем меню, если оно открыто
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

        // Текущий заряд всех найденных устройств
        if (monitor.IsPolling)
            menu.Items.Add(new Forms.ToolStripMenuItem("Идёт обновление…") { Enabled = false });
        else if (monitor.Devices.Count == 0)
            menu.Items.Add(new Forms.ToolStripMenuItem(monitor.HasPolled ? "Устройства не найдены" : "Идёт поиск устройств…") { Enabled = false });
        foreach (var d in monitor.Devices)
            menu.Items.Add(new Forms.ToolStripMenuItem(
                d.Name + " — " + d.Level + "%" + (d.Stale ? " (нет свежих данных)" : "") + "  [" + d.Source + "]"));
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Что показывать на панели
        var show = new Forms.ToolStripMenuItem("Показывать на панели");
        var names = monitor.Devices.Select(d => d.Name).Union(settings.HiddenDevices).OrderBy(n => n).ToList();
        if (names.Count == 0) show.DropDownItems.Add(new Forms.ToolStripMenuItem("Нет устройств") { Enabled = false });
        foreach (var n in names)
        {
            var name = n;
            var item = new Forms.ToolStripMenuItem(name) { Checked = !settings.HiddenDevices.Contains(name) };
            item.Click += (_, _) =>
            {
                if (!settings.HiddenDevices.Remove(name)) settings.HiddenDevices.Add(name);
                settings.Save();
                OverlayRefresh();
            };
            show.DropDownItems.Add(item);
        }
        menu.Items.Add(show);

        // Тема
        var themeMenu = new Forms.ToolStripMenuItem("Тема");
        foreach (var t in Themes.All)
        {
            var id = t.Id;
            var item = new Forms.ToolStripMenuItem(t.Id + " — " + t.Name) { Checked = settings.Theme == id };
            item.Click += (_, _) => { settings.Theme = id; settings.Save(); OverlayRefresh(); };
            themeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(themeMenu);

        // Порог низкого заряда
        var low = new Forms.ToolStripMenuItem("Красный цвет при заряде ниже");
        foreach (var p in new[] { 10, 15, 20, 30, 50 })
        {
            var value = p;
            var item = new Forms.ToolStripMenuItem(value + "%") { Checked = settings.LowThreshold == value };
            item.Click += (_, _) => { settings.LowThreshold = value; settings.Save(); OverlayRefresh(); };
            low.DropDownItems.Add(item);
        }
        menu.Items.Add(low);

        var fs = new Forms.ToolStripMenuItem("Скрывать в полноэкранных приложениях") { Checked = settings.HideInFullscreen };
        fs.Click += (_, _) => { settings.HideInFullscreen = !settings.HideInFullscreen; settings.Save(); };
        menu.Items.Add(fs);

        var halo = new Forms.ToolStripMenuItem("Брать данные из HaloBattery (если запущен)") { Checked = settings.UseHaloBattery };
        halo.Click += (_, _) => { settings.UseHaloBattery = !settings.UseHaloBattery; settings.Save(); };
        menu.Items.Add(halo);

        var verbose = new Forms.ToolStripMenuItem("Подробный журнал (для диагностики)") { Checked = settings.VerboseLog };
        verbose.Click += (_, _) => { settings.VerboseLog = !settings.VerboseLog; Log.Verbose = settings.VerboseLog; settings.Save(); };
        menu.Items.Add(verbose);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var demo = new Forms.ToolStripMenuItem("Демо: показать пример устройств") { Checked = monitor.DemoMode };
        demo.Click += (_, _) => monitor.SetDemo(!monitor.DemoMode);
        menu.Items.Add(demo);

        // Обновить: опрашиваем устройства заново и снова показываем меню уже с новым списком
        var refresh = new Forms.ToolStripMenuItem("Обновить список устройств");
        refresh.Click += async (_, _) =>
        {
            tray.ShowBalloonTip(1500, "Battery Taskbar", "Обновляю список устройств…", Forms.ToolTipIcon.Info);
            await monitor.PollAsync();
            if (menu.Visible) Rebuild();
            else ShowMenu();
        };
        menu.Items.Add(refresh);

        var auto = new Forms.ToolStripMenuItem("Запускать вместе с Windows") { Checked = Autostart.IsEnabled() };
        auto.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled());
        menu.Items.Add(auto);

        var scan = new Forms.ToolStripMenuItem("Сканер служебных каналов (30 сек)");
        scan.Click += async (_, _) =>
        {
            tray.ShowBalloonTip(3000, "Battery Taskbar", "Сканирую 30 секунд. Выключите и включите беспроводные устройства.", Forms.ToolTipIcon.Info);
            await HidScanner.RunAsync(30);
            tray.ShowBalloonTip(3000, "Battery Taskbar", "Готово. Откройте журнал.", Forms.ToolTipIcon.Info);
        };
        menu.Items.Add(scan);

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
        exit.Click += (_, _) => { tray.Visible = false; Application.Current.Shutdown(); };
        menu.Items.Add(exit);
    }

    // Окно рисует заново само при обновлении монитора, а при смене настроек его нужно подтолкнуть.
    void OverlayRefresh()
    {
        foreach (Window w in Application.Current.Windows)
            if (w is OverlayWindow o) o.Refresh();
    }

    static Icon MakeIcon()
    {
        bool light = SystemInfo.IsLightTheme();
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

    public void Dispose()
    {
        tray.Visible = false;
        tray.Dispose();
    }
}
