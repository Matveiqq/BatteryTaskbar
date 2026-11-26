using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BatteryTaskbar;

public sealed record BatteryDevice(string Name, int Level, string Source, bool Stale = false);

public interface IBatteryProvider
{
    string Name { get; }
    Task<List<BatteryDevice>> ReadAsync();
}

// Опрашивает все источники параллельно и объединяет результат.
public sealed class DeviceMonitor
{
    public sealed record SavedDevice(string Name, int Level, string Source, long SeenTicks);

    readonly IBatteryProvider[] providers;
    bool dumped;
    string lastSignature = "";

    // Последние значения остаются на панели тусклыми до 12 часов.
    readonly Dictionary<string, (BatteryDevice Device, DateTime Seen)> lastSeen =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan KeepFor = TimeSpan.FromHours(12);

    static string StatePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryTaskbar", "last_known.json");

    public IReadOnlyList<BatteryDevice> Devices { get; private set; } = new List<BatteryDevice>();
    public bool IsPolling { get; private set; }
    public bool HasPolled { get; private set; }
    public event Action? Updated;

    public DeviceMonitor(params IBatteryProvider[] providers)
    {
        this.providers = providers ?? throw new ArgumentNullException(nameof(providers));
        LoadLastKnown();
    }

    public async Task PollAsync()
    {
        if (IsPolling)
        {
            while (IsPolling) await Task.Delay(200);
            return;
        }

        IsPolling = true;
        Log.ForceVerbose = !HasPolled;
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
                foreach (var device in list)
                    merged.TryAdd(device.Name, device);

            DateTime now = DateTime.Now;
            foreach (var device in merged.Values)
                lastSeen[device.Name] = (device, now);

            var result = merged.Values.ToList();
            foreach (var item in lastSeen.ToList())
            {
                if (merged.ContainsKey(item.Key)) continue;
                if (now - item.Value.Seen <= KeepFor)
                    result.Add(item.Value.Device with { Stale = true });
                else
                    lastSeen.Remove(item.Key);
            }

            result = result.OrderBy(x => x.Name).ToList();
            Devices = result;
            SaveLastKnown();

            string signature = string.Join("; ", result.Select(d =>
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

            DateTime now = DateTime.Now;
            foreach (var item in saved)
            {
                var seen = new DateTime(item.SeenTicks);
                if (now - seen > KeepFor || item.Level < 0 || item.Level > 100) continue;
                lastSeen[item.Name] = (new BatteryDevice(item.Name, item.Level, item.Source), seen);
            }
            Devices = lastSeen.Values.Select(v => v.Device with { Stale = true })
                .OrderBy(d => d.Name).ToList();
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

    async Task<List<BatteryDevice>> RunProvider(IBatteryProvider provider)
    {
        try
        {
            var task = Task.Run(() => provider.ReadAsync());
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (done != task)
            {
                Log.Write(provider.Name + ": тайм-аут");
                return new List<BatteryDevice>();
            }
            var list = await task;
            Log.Debug(provider.Name + ": найдено " + list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Write(provider.Name + ": ОШИБКА " + ex.GetType().Name + ": " + ex.Message);
            return new List<BatteryDevice>();
        }
    }
}
