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
    public event Action? Updated;

    public DeviceMonitor(params IBatteryProvider[] providers)
    {
        this.providers = providers;
    }

    public async Task PollAsync()
    {
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

            Devices = merged.Values.OrderBy(d => d.Name).ToList();
            Log.Write("Итого найдено: " + Devices.Count);
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
        Updated?.Invoke();
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
