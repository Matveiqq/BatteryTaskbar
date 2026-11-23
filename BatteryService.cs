using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BatteryTaskbar;

public sealed record BatteryDevice(string Name, int Level, string Source);

// Один источник данных о заряде. Новый бренд = новый класс, реализующий этот интерфейс.
public interface IBatteryProvider
{
    string Name { get; }
    Task<List<BatteryDevice>> ReadAsync();
}

// Опрашивает все источники параллельно (у каждого свой тайм-аут) и объединяет результат.
public sealed class DeviceMonitor
{
    readonly IBatteryProvider[] providers;
    bool dumped;

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

    // Демо-режим: показывает примерные устройства, чтобы оценить вид и положение виджета без железа
    public void SetDemo(bool on)
    {
        DemoMode = on;
        if (on) Devices = DemoDevices;
        else Devices = new List<BatteryDevice>();
        RaiseUpdated();
        if (!on) _ = PollAsync();
    }

    public DeviceMonitor(params IBatteryProvider[] providers)
    {
        this.providers = providers;
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
        try
        {
            Log.Write("--- опрос устройств ---");
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

            if (!DemoMode) Devices = merged.Values.OrderBy(d => d.Name).ToList();
            Log.Write("Итого найдено: " + merged.Count);
        }
        catch (Exception ex)
        {
            Log.Write("Опрос: ошибка " + ex.Message);
        }
        finally
        {
            IsPolling = false;
            HasPolled = true;
        }
        RaiseUpdated();
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
            Log.Write(p.Name + ": найдено " + list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.Write(p.Name + ": ОШИБКА " + ex.GetType().Name + ": " + ex.Message);
            return new List<BatteryDevice>();
        }
    }
}
