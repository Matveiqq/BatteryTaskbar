using System.Collections.Generic;
using System.Threading.Tasks;

namespace BatteryTaskbar;

public sealed class LogitechHidProvider : IBatteryProvider
{
    public string Name => "Logitech HID++";

    public Task<List<DeviceBattery>> ReadAsync()
    {
        return Task.Run(() => LogitechHidpp.ReadAll());
    }
}
