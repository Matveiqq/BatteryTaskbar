using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

// Безрамочное прозрачное окно, лежащее поверх левой части панели задач.
// По умолчанию клики проходят сквозь него (в играх, на панели задач и т.д.).
// Переместить: удерживая Ctrl, перетащите виджет левой кнопкой мыши, либо включите «Переместить виджет» в настройках.
public class OverlayWindow : Window
{
    readonly StackPanel panel = new() { Orientation = Orientation.Horizontal };
    readonly Border frame;
    readonly AppSettings settings;
    readonly DeviceMonitor monitor;

    IntPtr hwnd = IntPtr.Zero;
    bool interactive;
    bool dragging;
    bool moveMode;
    DateTime moveModeUntil = DateTime.MinValue;
    bool hiddenFullscreen;
    bool noSpace;

    TaskbarSnapshot? layout;
    bool layoutBusy;

    public OverlayWindow(AppSettings s, DeviceMonitor m)
    {
        settings = s;
        monitor = m;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // почти прозрачный, но ловит клики в режиме перемещения
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 2, 2),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Child = panel
        };
        Content = frame;

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) => EndDrag();
        monitor.Updated += Refresh;
        Loaded += (_, _) => RefreshLayout();

        // Быстрая проверка: порядок окон, Ctrl над виджетом, полноэкранные приложения
        var fast = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        fast.Tick += (_, _) => Tick();
        fast.Start();

        // Положение пересчитываем реже
        var place = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        place.Tick += (_, _) => Reposition();
        place.Start();

        // Расположение значков панели задач
        var layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        layoutTimer.Tick += (_, _) => RefreshLayout();
        layoutTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureHandle();
        if (hwnd == IntPtr.Zero) return;
        // Не забирает фокус, не показывается в Alt+Tab и пропускает клики сквозь себя
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
            ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
    }

    void EnsureHandle()
    {
        if (hwnd == IntPtr.Zero)
            hwnd = new WindowInteropHelper(this).Handle;
    }

    // ---------- Режим перемещения ----------

    public void BeginMoveMode()
    {
        moveMode = true;
        moveModeUntil = DateTime.UtcNow.AddSeconds(30);
        Render();
        SetInteractive(true);
    }

    public void ResetPosition()
    {
        settings.WidgetX = null;
        settings.Save();
        Reposition();
    }

    // Перетаскивание по горизонтали вручную (по захвату мыши): не зависит от системного DragMove
    int dragStartCursorX;
    double dragStartLeft;

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!interactive) return;
        if (!Native.GetCursorPos(out Native.NativePoint p)) return;
        dragging = true;
        dragStartCursorX = p.X;
        dragStartLeft = Left;
        CaptureMouse();
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        if (!Native.GetCursorPos(out Native.NativePoint p)) return;

        double scale = 1.0;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double x = dragStartLeft + (p.X - dragStartCursorX) / scale;
        Left = Math.Max(4, Math.Min(x, screenWidth - ActualWidth - 4));
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        try { ReleaseMouseCapture(); } catch { }
        FinishDrag();
    }

    void FinishDrag()
    {
        moveMode = false;
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = ResolveX(Left, frame.DesiredSize.Width);
        settings.WidgetX = double.IsNaN(x) ? Left : x;
        settings.Save();
        Render();
        if (!CtrlDown()) SetInteractive(false);
    }

    void SetInteractive(bool on)
    {
        EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        ex = on ? (ex & ~Native.WS_EX_TRANSPARENT) : (ex | Native.WS_EX_TRANSPARENT);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
        interactive = on;

        Cursor = on ? Cursors.SizeAll : Cursors.Arrow;
        if (on)
        {
            Color accent = Color.FromRgb(0x00, 0x78, 0xD4);
            try { accent = SystemParameters.WindowGlassColor; } catch { }
            frame.BorderBrush = new SolidColorBrush(accent);
            frame.Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
        }
        else
        {
            frame.BorderBrush = Brushes.Transparent;
            frame.Background = Brushes.Transparent;
        }
    }

    static bool CtrlDown() => (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;

    bool CursorOverWidget()
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!Native.GetCursorPos(out Native.NativePoint p)) return false;
        if (!Native.GetWindowRect(hwnd, out Native.NativeRect r)) return false;
        return p.X >= r.Left - 4 && p.X <= r.Right + 4 && p.Y >= r.Top - 4 && p.Y <= r.Bottom + 4;
    }

    void UpdateInteractivity()
    {
        if (dragging) return;

        bool want;
        if (moveMode)
        {
            if (DateTime.UtcNow > moveModeUntil)
            {
                moveMode = false;
                Render();
                want = false;
            }
            else want = true;
        }
        else want = CtrlDown() && CursorOverWidget();

        if (want != interactive) SetInteractive(want);
    }

    // ---------- Видимость и порядок окон ----------

    void Tick()
    {
        try
        {
            bool fullscreen = settings.HideInFullscreen && IsFullscreenAppRunning();
            if (fullscreen != hiddenFullscreen)
            {
                hiddenFullscreen = fullscreen;
                ApplyVisibility();
            }
            if (!IsVisible) return;

            EnsureHandle();
            if (hwnd == IntPtr.Zero) return;

            UpdateInteractivity();

            // Если над нами лежит другое окно, поднимаемся обратно (без активации)
            if (!dragging && Native.GetWindow(hwnd, Native.GW_HWNDPREV) != IntPtr.Zero)
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch { }
    }

    void ApplyVisibility()
    {
        bool hasItems = panel.Children.Count > 0;
        bool want = !hiddenFullscreen && !noSpace && (hasItems || moveMode);
        if (want && !IsVisible) Show();
        else if (!want && IsVisible) Hide();
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

    // ---------- Отрисовка ----------

    public void Refresh()
    {
        Render();
    }

    void Render()
    {
        try { RenderCore(); }
        catch (Exception ex) { Log.Write("Отрисовка: ошибка " + ex); }
    }

    void RenderCore()
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
        {
            var element = theme.Build(d, d.Level <= settings.LowThreshold ? low : fg);
            if (d.Stale) element.Opacity = 0.5; // нет свежих данных: показываем последнее значение тусклым
            panel.Children.Add(element);
        }

        if (panel.Children.Count == 0 && moveMode)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Battery Taskbar",
                Foreground = fg,
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        ApplyVisibility();
        Reposition();
    }

    // ---------- Положение и защита от наложения на значки ----------

    void RefreshLayout()
    {
        if (layoutBusy) return;
        layoutBusy = true;

        double scale = 1.0;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        double screenPx = SystemParameters.PrimaryScreenWidth * scale;

        Task.Run(() =>
        {
            TaskbarSnapshot? snapshot = null;
            try { snapshot = TaskbarLayout.Read(screenPx, scale); }
            catch (Exception ex) { Log.Debug("  панель задач: не удалось определить значки: " + ex.Message); }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                layoutBusy = false;
                layout = snapshot;
                Reposition();
            }));
        });
    }

    void Reposition()
    {
        if (dragging) return;

        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = frame.DesiredSize.Width;
        double height = frame.DesiredSize.Height;

        var workArea = SystemParameters.WorkArea;
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        double taskbarHeight = screenHeight - workArea.Height;
        double areaTop = workArea.Bottom;
        if (taskbarHeight < 20) { taskbarHeight = 48; areaTop = screenHeight - 48; } // автоскрытие или панель не снизу

        double desired = settings.WidgetX ?? settings.LeftMargin;
        double x = ResolveX(desired, width);

        bool none = double.IsNaN(x);
        if (none != noSpace)
        {
            noSpace = none;
            Log.Debug(none ? "  виджет скрыт: на панели задач нет свободного места" : "  виджет показан: место освободилось");
            ApplyVisibility();
        }
        if (none) return;

        Left = x;
        Top = areaTop + (taskbarHeight - height) / 2;
    }

    // Подбирает X так, чтобы виджет не лежал на значках панели задач. NaN = места нет.
    double ResolveX(double desired, double width)
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double minX = 4;
        double maxX = Math.Max(minX, screenWidth - width - 4);
        desired = Math.Max(minX, Math.Min(desired, maxX));

        var snapshot = layout;
        if (snapshot == null)
            return Math.Min(desired, Math.Max(minX, screenWidth * 0.80 - width)); // данных о значках нет: как раньше

        const double gap = 8;
        var free = new List<(double L, double R)>();
        double cursor = minX;
        foreach (var occupied in snapshot.Occupied.OrderBy(o => o.Left))
        {
            if (occupied.Left - gap > cursor) free.Add((cursor, occupied.Left - gap));
            cursor = Math.Max(cursor, occupied.Right + gap);
        }
        double end = snapshot.TrayStart - gap;
        if (end > cursor) free.Add((cursor, end));

        double bestX = double.NaN;
        double bestDistance = double.MaxValue;
        foreach (var zone in free)
        {
            if (zone.R - zone.L < width) continue;
            double x = Math.Max(zone.L, Math.Min(desired, zone.R - width));
            double distance = Math.Abs(x - desired);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestX = x;
            }
        }
        return bestX;
    }
}
