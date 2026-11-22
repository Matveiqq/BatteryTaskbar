using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryTaskbar;

public sealed class BluetoothLeProvider : IBatteryProvider
{
    public string Name => "Bluetooth LE (GATT)";

    public async Task<List<DeviceBattery>> ReadAsync()
    {
        var result = new List<DeviceBattery>();
        var infos = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));

        Log.Write("  BLE спаренных устройств: " + infos.Count);

        foreach (var info in infos)
        {
            var task = ReadDeviceAsync(info);
            if (await Task.WhenAny(task, Task.Delay(8000)) != task)
            {
                Log.Write("  BLE таймаут: " + info.Name);
                continue;
            }

            var device = await task;
            if (device != null)
                result.Add(device);
        }

        return result;
    }

    private static async Task<DeviceBattery?> ReadDeviceAsync(
        DeviceInformation info)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (device == null)
            {
                Log.Write("  BLE " + info.Name + ": не открылось");
                return null;
            }

            Log.Write("  BLE " + device.Name + ": " + device.ConnectionStatus);

            if (string.IsNullOrWhiteSpace(device.Name) ||
                device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return null;

            var services = await device.GetGattServicesForUuidAsync(
                GattServiceUuids.Battery, BluetoothCacheMode.Uncached);

            Log.Write("  BLE " + device.Name + ": служба батареи " +
                      services.Status + ", найдено " + services.Services.Count);

            if (services.Status != GattCommunicationStatus.Success)
                return null;

            foreach (var service in services.Services)
            {
                var characteristics =
                    await service.GetCharacteristicsForUuidAsync(
                        GattCharacteristicUuids.BatteryLevel,
                        BluetoothCacheMode.Uncached);

                if (characteristics.Status != GattCommunicationStatus.Success)
                    continue;

                foreach (var characteristic in characteristics.Characteristics)
                {
                    var read = await characteristic.ReadValueAsync(
                        BluetoothCacheMode.Uncached);

                    if (read.Status != GattCommunicationStatus.Success)
                        continue;

                    var reader = DataReader.FromBuffer(read.Value);
                    int level = reader.ReadByte();

                    if (level >= 0 && level <= 100)
                    {
                        Log.Write("  BLE: " + device.Name + " = " + level + "%");
                        return new DeviceBattery(device.Name, level);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("  BLE " + info.Name + " ошибка: " + ex.Message);
        }

        return null;
    }
}
