using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace BatteryTaskbar;

public class AppSettings
{
    public int Theme { get; set; } = 1;
    public int LowThreshold { get; set; } = 20;
    public int LeftMargin { get; set; } = 12;
    // Положение виджета по горизонтали (null = по умолчанию, у левого края)
    public double? WidgetX { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool VerboseLog { get; set; }
    // Чтение G435 на донгле 046D:0ACB на ~1 секунду приглушает звук, поэтому выключено, пока пользователь не включит
    public bool AllowG435AcbPolling { get; set; }
    public HashSet<string> HiddenDevices { get; set; } = new();

    static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryTaskbar", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "BatteryTaskbar";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath != null)
                key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
            else
                key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}

public static class SystemInfo
{
    // Тема панели задач и системы (для виджета и значка в трее)
    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("SystemUsesLightTheme") as int?) == 1;
        }
        catch { return false; }
    }

    // Тема приложений (для окна настроек)
    public static bool IsAppLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int number ? number != 0 : true;
        }
        catch { return true; }
    }
}
