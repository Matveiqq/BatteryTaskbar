using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BatteryTaskbar;

public sealed class BatteryProviderManager
{
    private readonly IReadOnlyList<IBatteryProvider> providers;

    public BatteryProviderManager(IEnumerable<IBatteryProvider> providers)
    {
        this.providers = providers.ToList();
    }

    public async Task<List<DeviceBattery>> ReadAllAsync()
    {
        var devices = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            try
            {
                Log.Write("Провайдер " + provider.Name + ": запуск");
                var found = await provider.ReadAsync();

                foreach (var device in found)
                {
                    if (string.IsNullOrWhiteSpace(device.Name) ||
                        device.Level < 0 || device.Level > 100)
                        continue;

                    if (devices.TryAdd(device.Name.Trim(), device))
                        Log.Write("  Найдено: " + device.Name + " = " + device.Level + "%");
                }

                Log.Write("Провайдер " + provider.Name + ": найдено " + found.Count);
            }
            catch (Exception ex)
            {
                Log.Write("Провайдер " + provider.Name + ": ОШИБКА " +
                          ex.GetType().Name + ": " + ex.Message);
            }
        }

        return devices.Values.OrderBy(d => d.Name).ToList();
    }
}
