
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatteryTaskbar;

public class OverlayWindow : Window
{
    readonly StackPanel panel = new() { Orientation = Orientation.Horizontal };
    readonly AppSettings settings;
    readonly DeviceMonitor monitor;

    WinEventDelegate? winEventCallback;
    IntPtr foregroundHook;
    IntPtr shellHook;

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint EVENT_OBJECT_HIDE = 0x8003;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;

    static readonly IntPtr HWND_TOPMOST = new(-1);

    public Action? OpenMenu { get; set; }

    public OverlayWindow(AppSettings s, DeviceMonitor m)
    {
        settings = s;
        monitor = m;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = panel;

        MouseDown += (_, _) => OpenMenu?.Invoke();
        monitor.Updated += Render;

        Loaded += (_, _) =>
        {
            InstallWindowHooks();
            UpdateVisibility();
        };

        Closed += (_, _) => RemoveWindowHooks();
    }

    public void Refresh() => Render();

    void InstallWindowHooks()
    {
        if (winEventCallback != null) return;

        winEventCallback = OnWinEvent;

        foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            winEventCallback,
            0, 0,
            WINEVENT_SKIPOWNPROCESS);

        shellHook = SetWinEventHook(
            EVENT_OBJECT_SHOW,
            EVENT_OBJECT_HIDE,
            IntPtr.Zero,
            winEventCallback,
            0, 0,
            WINEVENT_SKIPOWNPROCESS);
    }

    void RemoveWindowHooks()
    {
        if (foregroundHook != IntPtr.Zero)
            UnhookWinEvent(foregroundHook);

        if (shellHook != IntPtr.Zero)
            UnhookWinEvent(shellHook);

        foregroundHook = IntPtr.Zero;
        shellHook = IntPtr.Zero;
        winEventCallback = null;
    }

    void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (eventType == EVENT_SYSTEM_FOREGROUND ||
            (idObject == 0 && IsTaskbarWindow(hwnd)))
        {
            Dispatcher.BeginInvoke(
                new Action(UpdateVisibility),
                DispatcherPriority.Normal);
        }
    }

    static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        return hwnd == FindWindow("Shell_TrayWnd", null) ||
               hwnd == FindWindow("Shell_SecondaryTrayWnd", null);
    }

    void Render()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Render));
            return;
        }

        panel.Children.Clear();

        Brush fg = SystemInfo.IsLightTheme()
            ? Brushes.Black
            : Brushes.White;

        Brush low = new SolidColorBrush(
            Color.FromRgb(0xFF, 0x6B, 0x6B));

        var theme = Themes.Get(settings.Theme);

        foreach (var d in monitor.Devices.Where(
            d => !settings.HiddenDevices.Contains(d.Name)))
        {
            panel.Children.Add(
                theme.Build(
                    d,
                    d.Level <= settings.LowThreshold ? low : fg));
        }

        Reposition();
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateVisibility));
            return;
        }

        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);

        if (!IsTaskbarActuallyVisible(taskbar) ||
            IsForegroundFullscreen())
        {
            if (IsVisible) Hide();
            return;
        }

        Reposition();

        if (!IsVisible)
            Show();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(
                hwnd,
                HWND_TOPMOST,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    static bool IsTaskbarActuallyVisible(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero ||
            !IsWindowVisible(taskbar))
            return false;

        if (!GetWindowRect(taskbar, out var r))
            return false;

        double sw = SystemParameters.PrimaryScreenWidth;
        double sh = SystemParameters.PrimaryScreenHeight;

        double visibleWidth =
            Math.Max(0, Math.Min(r.Right, sw) - Math.Max(r.Left, 0));

        double visibleHeight =
            Math.Max(0, Math.Min(r.Bottom, sh) - Math.Max(r.Top, 0));

        return visibleWidth > 3 && visibleHeight > 3;
    }

    static bool IsForegroundFullscreen()
    {
        IntPtr foreground = GetForegroundWindow();

        if (foreground == IntPtr.Zero ||
            !GetWindowRect(foreground, out var r))
            return false;

        try
        {
            var bounds =
                System.Windows.Forms.Screen.FromHandle(foreground).Bounds;

            const int tolerance = 2;

            return r.Left <= bounds.Left + tolerance &&
                   r.Top <= bounds.Top + tolerance &&
                   r.Right >= bounds.Right - tolerance &&
                   r.Bottom >= bounds.Bottom - tolerance;
        }
        catch
        {
            return false;
        }
    }

    void Reposition()
    {
        panel.Measure(
            new Size(double.PositiveInfinity, double.PositiveInfinity));

        double h = panel.DesiredSize.Height;
        var wa = SystemParameters.WorkArea;

        double screenH = SystemParameters.PrimaryScreenHeight;
        double taskbarH = screenH - wa.Height;
        double areaTop = wa.Bottom;

        if (taskbarH < 20)
        {
            taskbarH = 48;
            areaTop = screenH - 48;
        }

        Left = settings.LeftMargin;
        Top = areaTop + (taskbarH - h) / 2;
    }

    delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr FindWindow(
        string? lpClassName,
        string? lpWindowName);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(
        IntPtr hWnd,
        out RECT lpRect);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);
}
