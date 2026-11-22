using System.Collections.Generic;
using System.Threading.Tasks;

namespace BatteryTaskbar;

public interface IBatteryProvider
{
    string Name { get; }

    Task<List<DeviceBattery>> ReadAsync();
}
