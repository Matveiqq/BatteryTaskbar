using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace BatteryTaskbar;

// Окно настроек в стиле Windows 11. Тема (светлая/тёмная) выбирается автоматически по настройкам Windows.
public sealed class SettingsWindow : Window
{
    // ---------- Цвета ----------
    sealed class Palette
    {
        public bool Light;
        public Brush Window = Brushes.White;
        public Brush NavSelected = Brushes.White;
        public Brush Hover = Brushes.White;
        public Brush Card = Brushes.White;
        public Brush CardBorder = Brushes.White;
        public Brush Text = Brushes.Black;
        public Brush Subtext = Brushes.Gray;
        public Brush Accent = Brushes.DodgerBlue;
        public Brush AccentText = Brushes.White;
        public Brush Track = Brushes.Gray;
        public Brush Button = Brushes.White;
        public Brush ButtonHover = Brushes.White;
        public Brush ButtonBorder = Brushes.Gray;

        static SolidColorBrush Hex(string value) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

        public static Palette Create(bool light)
        {
            Color accent = Color.FromRgb(0x00, 0x67, 0xC0);
            try { accent = SystemParameters.WindowGlassColor; } catch { }
            double luminance = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;
            if (light && luminance > 190) accent = Color.FromRgb(0x00, 0x67, 0xC0);
            if (!light && luminance < 90) accent = Color.FromRgb(0x4C, 0xC2, 0xFF);
            double accentLuminance = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;

            var palette = new Palette { Light = light };
            palette.Accent = new SolidColorBrush(accent);
            palette.AccentText = accentLuminance > 150 ? Brushes.Black : Brushes.White;
            if (light)
            {
                palette.Window = Hex("#F3F3F3");
                palette.NavSelected = Hex("#E4E4E4");
                palette.Hover = Hex("#EBEBEB");
                palette.Card = Hex("#FFFFFF");
                palette.CardBorder = Hex("#E5E5E5");
                palette.Text = Hex("#1B1B1B");
                palette.Subtext = Hex("#616161");
                palette.Track = Hex("#6E6E6E");
                palette.Button = Hex("#FBFBFB");
                palette.ButtonHover = Hex("#F0F0F0");
                palette.ButtonBorder = Hex("#D1D1D1");
            }
            else
            {
                palette.Window = Hex("#202020");
                palette.NavSelected = Hex("#323232");
                palette.Hover = Hex("#2A2A2A");
                palette.Card = Hex("#2B2B2B");
                palette.CardBorder = Hex("#3A3A3A");
                palette.Text = Hex("#FFFFFF");
                palette.Subtext = Hex("#B5B5B5");
                palette.Track = Hex("#A0A0A0");
                palette.Button = Hex("#363636");
                palette.ButtonHover = Hex("#404040");
                palette.ButtonBorder = Hex("#4A4A4A");
            }
            return palette;
        }
    }

    // ---------- Свои элементы управления ----------
    sealed class ToggleSwitch : Border
    {
        readonly Palette palette;
        readonly Ellipse knob;
        bool isOn;

        public event Action<bool>? Toggled;

        public ToggleSwitch(Palette p, bool on)
        {
            palette = p;
            isOn = on;
            Width = 42;
            Height = 22;
            CornerRadius = new CornerRadius(11);
            BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand;
            knob = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            Child = knob;
            MouseLeftButtonUp += (_, _) =>
            {
                isOn = !isOn;
                Update();
                Toggled?.Invoke(isOn);
            };
            Update();
        }

        void Update()
        {
            Background = isOn ? palette.Accent : Brushes.Transparent;
            BorderBrush = isOn ? palette.Accent : palette.Track;
            knob.Fill = isOn ? palette.AccentText : palette.Track;
            knob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    sealed class FlatButton : Border
    {
        public event Action? Clicked;

        public FlatButton(Palette p, string text, bool accent)
        {
            Brush normal = accent ? p.Accent : p.Button;
            Brush hover = accent ? p.Accent : p.ButtonHover;
            CornerRadius = new CornerRadius(4);
            Padding = new Thickness(16, 6, 16, 6);
            BorderThickness = new Thickness(1);
            BorderBrush = accent ? p.Accent : p.ButtonBorder;
            Background = normal;
            Cursor = Cursors.Hand;
            HorizontalAlignment = HorizontalAlignment.Left;
            Child = new TextBlock { Text = text, FontSize = 13, Foreground = accent ? p.AccentText : p.Text };
            MouseEnter += (_, _) => { Background = hover; Opacity = accent ? 0.9 : 1.0; };
            MouseLeave += (_, _) => { Background = normal; Opacity = 1.0; };
            MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
        }
    }

    sealed class Segmented : StackPanel
    {
        readonly Palette palette;
        readonly List<Border> items = new();

        public event Action<int>? Changed;

        public Segmented(Palette p, IEnumerable<string> labels, int selected)
        {
            palette = p;
            Orientation = Orientation.Horizontal;
            int number = 0;
            foreach (string label in labels)
            {
                int index = number++;
                var item = new Border
                {
                    Padding = new Thickness(12, 5, 12, 5),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 0, 4, 0),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = label, FontSize = 13 }
                };
                item.MouseLeftButtonUp += (_, _) =>
                {
                    Select(index);
                    Changed?.Invoke(index);
                };
                items.Add(item);
                Children.Add(item);
            }
            Select(selected);
        }

        void Select(int selected)
        {
            for (int i = 0; i < items.Count; i++)
            {
                bool on = i == selected;
                items[i].Background = on ? palette.Accent : palette.Button;
                items[i].BorderBrush = on ? palette.Accent : palette.ButtonBorder;
                ((TextBlock)items[i].Child).Foreground = on ? palette.AccentText : palette.Text;
            }
        }
    }

    // ---------- Окно ----------
    static SettingsWindow? instance;

    static readonly (string Key, string Title)[] Pages =
    {
        ("devices", "Устройства"),
        ("look", "Внешний вид"),
        ("g435", "Наушники G435"),
    };
    const string DiagnosticsKey = "diag";

    readonly AppSettings settings;
    readonly DeviceMonitor monitor;
    readonly Dictionary<string, Border> navItems = new();
    Palette palette = Palette.Create(true);
    ScrollViewer pageHost = new();
    string current = "devices";

    public static void ShowOrActivate(AppSettings settings, DeviceMonitor monitor)
    {
        if (instance != null)
        {
            if (instance.WindowState == WindowState.Minimized) instance.WindowState = WindowState.Normal;
            instance.Activate();
            return;
        }
        instance = new SettingsWindow(settings, monitor);
        instance.Show();
    }

    SettingsWindow(AppSettings appSettings, DeviceMonitor deviceMonitor)
    {
        settings = appSettings;
        monitor = deviceMonitor;

        Title = "Battery Taskbar — настройки";
        Width = 800;
        Height = 600;
        MinWidth = 660;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        monitor.Updated += OnMonitorUpdated;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Closed += (_, _) =>
        {
            monitor.Updated -= OnMonitorUpdated;
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            instance = null;
        };

        BuildUi();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    void ApplyTitleBarTheme()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            int dark = palette.Light ? 0 : 1;
            Native.DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // тёмный заголовок окна
        }
        catch { }
    }

    void OnMonitorUpdated()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (current == "devices") ShowPage(current);
        }));
    }

    void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General &&
            e.Category != UserPreferenceCategory.Color &&
            e.Category != UserPreferenceCategory.VisualStyle) return;
        Dispatcher.BeginInvoke(new Action(BuildUi));
    }

    void BuildUi()
    {
        palette = Palette.Create(SystemInfo.IsAppLightTheme());
        Background = palette.Window;
        navItems.Clear();

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new Grid { Margin = new Thickness(12, 16, 8, 12) };
        nav.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Text = "Battery Taskbar",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = palette.Text,
            Margin = new Thickness(8, 0, 0, 16)
        });
        foreach (var page in Pages)
            top.Children.Add(MakeNavItem(page.Key, page.Title, false));
        Grid.SetRow(top, 0);
        nav.Children.Add(top);

        var diagnostics = MakeNavItem(DiagnosticsKey, "Диагностика и журналы", true);
        Grid.SetRow(diagnostics, 1);
        nav.Children.Add(diagnostics);

        Grid.SetColumn(nav, 0);
        root.Children.Add(nav);

        pageHost = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(pageHost, 1);
        root.Children.Add(pageHost);

        Content = root;
        ShowPage(current);
        ApplyTitleBarTheme();
    }

    Border MakeNavItem(string key, string title, bool muted)
    {
        var item = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = title,
                FontSize = 14,
                Foreground = muted ? palette.Subtext : palette.Text
            }
        };
        item.MouseEnter += (_, _) => { if (current != key) item.Background = palette.Hover; };
        item.MouseLeave += (_, _) => { if (current != key) item.Background = Brushes.Transparent; };
        item.MouseLeftButtonUp += (_, _) => ShowPage(key);
        navItems[key] = item;
        return item;
    }

    void ShowPage(string key)
    {
        current = key;
        foreach (var pair in navItems)
            pair.Value.Background = pair.Key == key ? palette.NavSelected : Brushes.Transparent;

        UIElement page = key switch
        {
            "look" => LookPage(),
            "g435" => G435Page(),
            DiagnosticsKey => DiagnosticsPage(),
            _ => DevicesPage()
        };
        pageHost.Content = page;
    }

    // ---------- Помощники построения страниц ----------
    StackPanel NewPage(string title, string? subtitle)
    {
        var page = new StackPanel { Margin = new Thickness(24, 18, 20, 24) };
        page.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = palette.Text,
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (!string.IsNullOrEmpty(subtitle))
            page.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = palette.Subtext,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });
        return page;
    }

    Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = palette.CardBorder });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = palette.Card,
            BorderBrush = palette.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }

    Grid Row(string title, string? description, UIElement? control)
    {
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = palette.Text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = palette.Subtext,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        grid.Children.Add(text);

        if (control != null)
        {
            if (control is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return grid;
    }

    ToggleSwitch Toggle(bool on, Action<bool> changed)
    {
        var toggle = new ToggleSwitch(palette, on);
        toggle.Toggled += changed;
        return toggle;
    }

    void RefreshOverlay()
    {
        foreach (Window window in Application.Current.Windows)
            if (window is OverlayWindow overlay) overlay.Refresh();
    }

    // ---------- Страницы ----------
    UIElement DevicesPage()
    {
        var page = NewPage("Устройства",
            "Беспроводные устройства, найденные в системе. Переключатель управляет показом на панели задач.");

        var rows = new List<UIElement>();
        foreach (var device in monitor.Devices)
        {
            string name = device.Name;
            string details = device.Level + "%" + (device.Stale ? " · нет свежих данных" : "") + " · " + device.Source;
            rows.Add(Row(name, details, Toggle(!settings.HiddenDevices.Contains(name), on =>
            {
                if (on) settings.HiddenDevices.Remove(name);
                else settings.HiddenDevices.Add(name);
                settings.Save();
                RefreshOverlay();
            })));
        }

        foreach (string hidden in settings.HiddenDevices.Where(h => monitor.Devices.All(d => d.Name != h)).ToList())
        {
            string name = hidden;
            rows.Add(Row(name, "сейчас не найдено", Toggle(false, on =>
            {
                if (on) settings.HiddenDevices.Remove(name);
                else settings.HiddenDevices.Add(name);
                settings.Save();
                RefreshOverlay();
            })));
        }

        if (rows.Count == 0)
            rows.Add(Row(monitor.IsPolling ? "Идёт поиск устройств…" : "Устройства не найдены",
                "Включите устройство, подождите минуту или нажмите «Обновить».", null));

        page.Children.Add(Card(rows.ToArray()));

        var refresh = new FlatButton(palette, "Обновить сейчас", true);
        refresh.Clicked += async () => await monitor.PollAsync();
        page.Children.Add(refresh);
        return page;
    }

    UIElement LookPage()
    {
        var page = NewPage("Внешний вид", "Оформление и поведение виджета на панели задач.");

        int themeIndex = Math.Max(0, Array.FindIndex(Themes.All, t => t.Id == settings.Theme));
        var themes = new Segmented(palette, Themes.All.Select(t => t.Id + " — " + t.Name), themeIndex);
        themes.Changed += index =>
        {
            settings.Theme = Themes.All[index].Id;
            settings.Save();
            RefreshOverlay();
        };

        int[] thresholds = { 10, 15, 20, 30, 50 };
        int thresholdIndex = Math.Max(0, Array.IndexOf(thresholds, settings.LowThreshold));
        var low = new Segmented(palette, thresholds.Select(v => v + "%"), thresholdIndex);
        low.Changed += index =>
        {
            settings.LowThreshold = thresholds[index];
            settings.Save();
            RefreshOverlay();
        };

        var move = new FlatButton(palette, "Переместить", false);
        move.Clicked += () =>
        {
            foreach (Window window in Application.Current.Windows)
                if (window is OverlayWindow overlay) overlay.BeginMoveMode();
        };

        var reset = new FlatButton(palette, "Сбросить", false);
        reset.Clicked += () =>
        {
            foreach (Window window in Application.Current.Windows)
                if (window is OverlayWindow overlay) overlay.ResetPosition();
        };

        page.Children.Add(Card(
            Row("Тема виджета", null, themes),
            Row("Красный цвет при заряде ниже", null, low)));

        page.Children.Add(Card(
            Row("Переместить виджет",
                "Нажмите кнопку и перетащите виджет, удерживая левую кнопку мыши (30 секунд). В любое другое время: удерживайте Ctrl и перетаскивайте. Обычные клики проходят сквозь виджет.",
                move),
            Row("Сбросить положение", "Вернуть виджет к левому краю панели задач.", reset)));

        page.Children.Add(Card(
            Row("Скрывать в полноэкранных приложениях", "Виджет не показывается поверх игр и видео на весь экран.",
                Toggle(settings.HideInFullscreen, on => { settings.HideInFullscreen = on; settings.Save(); })),
            Row("Запускать вместе с Windows", null,
                Toggle(Autostart.IsEnabled(), on => Autostart.Set(on)))));
        return page;
    }

    UIElement G435Page()
    {
        var page = NewPage("Наушники G435",
            "Logitech G435 LIGHTSPEED на USB-донгле 046D:0ACB. У донгла нет обычного канала заряда, поэтому используется служебный режим.");

        var status = new TextBlock
        {
            Text = LogitechG435Provider.LastStatus,
            Foreground = palette.Subtext,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var toggle = Toggle(settings.AllowG435AcbPolling, on =>
        {
            settings.AllowG435AcbPolling = on;
            settings.Save();
            if (on) LogitechG435Provider.RequestImmediateRead();
            _ = monitor.PollAsync();
        });

        var now = new FlatButton(palette, "Опросить сейчас", false);
        now.Clicked += async () =>
        {
            LogitechG435Provider.RequestImmediateRead();
            await monitor.PollAsync();
            status.Text = LogitechG435Provider.LastStatus;
        };

        page.Children.Add(Card(
            Row("Опрашивать G435",
                "Каждое чтение на секунду приглушает звук и микрофон, поэтому опрос идёт не чаще раза в 15 минут. " +
                "Процент приблизительный: он считается по напряжению аккумулятора. Если звук после чтения не вернулся, вытащите и снова вставьте донгл.",
                toggle),
            Row("Опросить сейчас", null, now)));

        var statusBox = new Border { Margin = new Thickness(16, 12, 16, 12), Child = status };
        page.Children.Add(Card(statusBox));
        return page;
    }

    UIElement DiagnosticsPage()
    {
        var page = NewPage("Диагностика и журналы",
            "Раздел для поиска неисправностей. В обычной работе он не нужен.");

        var result = new TextBlock { Foreground = palette.Subtext, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        var openLog = new FlatButton(palette, "Открыть журнал", false);
        openLog.Clicked += () =>
        {
            try
            {
                Log.Write("(журнал открыт пользователем)");
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Log.FilePath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { result.Text = "Не удалось открыть журнал: " + ex.Message; }
        };

        var openFolder = new FlatButton(palette, "Открыть папку", false);
        openFolder.Clicked += () =>
        {
            try
            {
                string folder = System.IO.Path.GetDirectoryName(Log.FilePath)!;
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { result.Text = "Не удалось открыть папку: " + ex.Message; }
        };

        var scan = new FlatButton(palette, "Запустить", false);
        scan.Clicked += async () =>
        {
            result.Text = "Идёт сканирование 30 секунд: выключите и включите беспроводные устройства…";
            try { await HidScanner.RunAsync(30); }
            catch (Exception ex) { Log.Write("Сканер: ошибка " + ex.Message); }
            result.Text = "Сканирование завершено. Результат записан в журнал.";
        };

        page.Children.Add(Card(
            Row("Подробный журнал", "Записывать в журнал все подробности каждого опроса. Включайте только для поиска проблем.",
                Toggle(settings.VerboseLog, on =>
                {
                    settings.VerboseLog = on;
                    Log.Verbose = on;
                    settings.Save();
                })),
            Row("Журнал", "Файл log.txt (хранятся два файла: log.txt и log.old.txt).", openLog),
            Row("Папка с данными", "Настройки, журнал и последние значения заряда.", openFolder),
            Row("Сканер служебных каналов", "Слушает служебные каналы беспроводных устройств 30 секунд (только чтение).", scan)));

        page.Children.Add(Card(Row("Версия", Program.Version, null)));
        page.Children.Add(result);
        return page;
    }
}
