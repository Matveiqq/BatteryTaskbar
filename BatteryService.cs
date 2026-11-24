using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BatteryTaskbar;

public sealed record BatteryDevice(string Name, int Level, string Source, bool Stale = false);

// Один источник данных о заряде. Новый бренд = новый класс, реализующий этот интерфейс.
public interface IBatteryProvider
{
    string Name { get; }
    Task<List<BatteryDevice>> ReadAsync();
}

// Опрашивает все источники параллельно (у каждого свой тайм-аут) и объединяет результат.
public sealed class DeviceMonitor
{
    public sealed record SavedDevice(string Name, int Level, string Source, long SeenTicks);

    readonly IBatteryProvider[] providers;
    bool dumped;
    string lastSignature = "";

    // Последние известные значения: если устройство уснуло или временно не ответило (или программу перезапустили),
    // показываем его последнее значение тусклым до 12 часов, а не убираем с панели
    readonly Dictionary<string, (BatteryDevice Device, DateTime Seen)> lastSeen = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan KeepFor = TimeSpan.FromHours(12);

    static string StatePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryTaskbar", "last_known.json");

    public IReadOnlyList<BatteryDevice> Devices { get; private set; } = new List<BatteryDevice>();
    public bool IsPolling { get; private set; }
    public bool HasPolled { get; private set; }
    public bool DemoMode { get; private set; }
    public event Action? Updated;

    static readonly BatteryDevice[] DemoDevices =
    {
        new BatteryDevice("Клавиатура", 85, "Демо"),
        new BatteryDevice("Мышь", 42, "Демо"),
        new BatteryDevice("Наушники", 18, "Демо"),
    };

    public DeviceMonitor(params IBatteryProvider[] providers)
    {
        this.providers = providers;
        LoadLastKnown();
    }

    // Демо-режим: показывает примерные устройства, чтобы оценить вид и положение виджета без железа
    public void SetDemo(bool on)
    {
        DemoMode = on;
        if (on) Devices = DemoDevices;
        else Devices = new List<BatteryDevice>();
        RaiseUpdated();
        if (!on) _ = PollAsync();
    }

    public async Task PollAsync()
    {
        if (DemoMode) return;
        if (IsPolling)
        {
            while (IsPolling) await Task.Delay(200);
            return;
        }

        IsPolling = true;
        Log.ForceVerbose = !HasPolled; // первый опрос после запуска пишем подробно
        try
        {
            Log.Debug("--- опрос устройств ---");
            if (!dumped)
            {
                dumped = true;
                await Task.Run(() => HidScanner.DumpAll());
            }

            var results = await Task.WhenAll(providers.Select(RunProvider).ToList());

            var merged = new Dictionary<string, BatteryDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in results)
                foreach (var d in list)
                    merged.TryAdd(d.Name, d);

            var now = DateTime.Now;
            foreach (var d in merged.Values) lastSeen[d.Name] = (d, now);

            var result = merged.Values.ToList();
            foreach (var kv in lastSeen.ToList())
            {
                if (merged.ContainsKey(kv.Key)) continue;
                if (now - kv.Value.Seen <= KeepFor)
                    result.Add(kv.Value.Device with { Stale = true });
                else
                    lastSeen.Remove(kv.Key);
            }
            result = result.OrderBy(x => x.Name).ToList();

            if (!DemoMode) Devices = result;
            SaveLastKnown();

            // В журнал пишем только изменения (или всё подряд в подробном режиме)
            var signature = string.Join("; ", result.Select(d =>
                d.Name + " " + d.Level + "%" + (d.Stale ? " (нет свежих данных)" : "") + " [" + d.Source + "]"));
            if (signature != lastSignature)
            {
                lastSignature = signature;
                Log.Write(result.Count == 0 ? "Устройства: не найдены" : "Устройства: " + signature);
            }
            Log.Debug("Итого: свежих " + merged.Count + ", всего на панели " + result.Count);
        }
        catch (Exception ex)
        {
            Log.Write("Опрос: ошибка " + ex.Message);
        }
        finally
        {
            Log.ForceVerbose = false;
            IsPolling = false;
            HasPolled = true;
        }
        RaiseUpdated();
    }

    void LoadLastKnown()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var saved = JsonSerializer.Deserialize<List<SavedDevice>>(File.ReadAllText(StatePath));
            if (saved == null) return;

            var now = DateTime.Now;
            foreach (var s in saved)
            {
                var seen = new DateTime(s.SeenTicks);
                if (now - seen > KeepFor || s.Level < 0 || s.Level > 100) continue;
                lastSeen[s.Name] = (new BatteryDevice(s.Name, s.Level, s.Source), seen);
            }
            Devices = lastSeen.Values.Select(v => v.Device with { Stale = true }).OrderBy(d => d.Name).ToList();
        }
        catch (Exception ex)
        {
            Log.Write("Прошлые значения: ошибка " + ex.Message);
        }
    }

    void SaveLastKnown()
    {
        try
        {
            var saved = lastSeen.Values
                .Select(v => new SavedDevice(v.Device.Name, v.Device.Level, v.Device.Source, v.Seen.Ticks))
                .ToList();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(saved));
        }
        catch { }
    }

    void RaiseUpdated()
    {
        try { Updated?.Invoke(); }
        catch (Exception ex) { Log.Write("Обновление интерфейса: ошибка " + ex); }
    }

    async Task<List<BatteryDevice>> RunProvider(IBatteryProvider p)
    {
        try
        {
            var task = Task.Run(() => p.ReadAsync());
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (done != task)
            {
                Log.Write(p.Name + ": тайм-аут");
                return new List<BatteryDevice>();
            }
            var list = await task;
            Log.Debug(p.Name + ": найдено " + list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Write(p.Name + ": ОШИБКА " + ex.GetType().Name + ": " + ex.Message);
            return new List<BatteryDevice>();
        }
    }
}
