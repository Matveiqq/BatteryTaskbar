using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BatteryTaskbar;

// Необязательный источник: читает файл status.json, который HaloBattery пишет для внешних программ
// (в HaloBattery нужно включить параметр status_file). Так в нашем виджете появляются все устройства,
// которые умеет читать HaloBattery. Формат файла заранее неизвестен, поэтому разбор «терпимый».
public sealed class HaloBatteryBridgeProvider : IBatteryProvider
{
    static readonly string[] NameKeys = { "name", "device", "label", "title", "display_name", "device_name" };
    static readonly string[] LevelKeys = { "percent", "percentage", "level", "battery", "pct", "charge", "capacity", "battery_percent" };

    readonly AppSettings settings;

    public HaloBatteryBridgeProvider(AppSettings s)
    {
        settings = s;
    }

    public string Name => "HaloBattery (status.json)";

    public Task<List<BatteryDevice>> ReadAsync() => Task.Run(Read);

    List<BatteryDevice> Read()
    {
        var list = new List<BatteryDevice>();
        if (!settings.UseHaloBattery) return list;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HaloBattery", "status.json");
        if (!File.Exists(path))
        {
            Log.Write("  HaloBattery: status.json не найден (включите status_file в настройках HaloBattery)");
            return list;
        }

        bool running = Process.GetProcessesByName("HaloBattery").Length > 0;
        var age = DateTime.Now - File.GetLastWriteTime(path);
        if (!running && age > TimeSpan.FromMinutes(10))
        {
            Log.Write("  HaloBattery: файл устарел, а программа не запущена");
            return list;
        }

        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
            text = sr.ReadToEnd();

        var preview = text.Length > 800 ? text.Substring(0, 800) : text;
        Log.Write("  HaloBattery status.json: " + preview.Replace("\r", " ").Replace("\n", " "));

        using var doc = JsonDocument.Parse(text);
        Walk(doc.RootElement, null, list);
        return list;
    }

    static void Walk(JsonElement el, string? key, List<BatteryDevice> list)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            string? name = null;
            int? level = null;
            foreach (var p in el.EnumerateObject())
            {
                var k = p.Name.ToLowerInvariant();
                if (name == null && NameKeys.Contains(k) && p.Value.ValueKind == JsonValueKind.String)
                    name = p.Value.GetString();
                if (level == null && LevelKeys.Contains(k))
                    level = ToPercent(p.Value);
            }
            // формат вида { "Имя устройства": { "percent": 93 } }
            if (string.IsNullOrWhiteSpace(name) && level.HasValue && !string.IsNullOrWhiteSpace(key))
                name = key;

            if (!string.IsNullOrWhiteSpace(name) && level is int l && l >= 0 && l <= 100)
                list.Add(new BatteryDevice(name!, l, "HaloBattery"));

            foreach (var p in el.EnumerateObject())
                Walk(p.Value, p.Name, list);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                Walk(item, null, list);
        }
    }

    static int? ToPercent(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number)
            return (int)Math.Round(v.GetDouble());
        if (v.ValueKind == JsonValueKind.String)
        {
            var m = Regex.Match(v.GetString() ?? "", @"\d+");
            if (m.Success && int.TryParse(m.Value, out int n)) return n;
        }
        return null;
    }
}
