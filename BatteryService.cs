using System.Collections.Generic;
using System.Threading.Tasks;

namespace BatteryTaskbar;

// Совместимый фасад: Program.cs и OverlayWindow.cs менять не требуется.
public static class BatteryService
{
    private static readonly BatteryProviderManager Manager = new(
        new IBatteryProvider[]
        {
            new WindowsPnpProvider(),
            new WindowsBatteryApiProvider(),
            new AjazzHidProvider(),
            new LogitechHidProvider(),
            new BluetoothLeProvider()
        });

    private static bool dumped;

    public static async Task<List<DeviceBattery>> ReadAllAsync()
    {
        Log.Write("--- опрос устройств ---");

        if (!dumped)
        {
            dumped = true;
            try
            {
                await Task.Run(LogitechHidpp.DumpAllHid);
            }
            catch (System.Exception ex)
            {
                Log.Write("Список HID-устройств: " + ex.Message);
            }
        }

        var result = await Manager.ReadAllAsync();
        Log.Write("Итого найдено: " + result.Count);
        return result;
    }
}
