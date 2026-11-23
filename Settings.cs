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
    public bool HideInFullscreen { get; set; } = true;
    public bool UseHaloBattery { get; set; } = true;
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
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
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (enabled && Environment.ProcessPath != null)
                k.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
            else
                k.DeleteValue(ValueName, false);
        }
        catch { }
    }
}

public static class SystemInfo
{
    public static bool IsLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (k?.GetValue("SystemUsesLightTheme") as int?) == 1;
        }
        catch { return false; }
    }
}
