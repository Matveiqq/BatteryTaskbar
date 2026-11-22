using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BatteryTaskbar;

// Экспериментальный адаптер. Команды и расположение байта заряда
// пока не подтверждены для всех моделей Ajazz.
public sealed class AjazzHidProvider : IBatteryProvider
{
    public string Name => "AJAZZ HID (экспериментальный)";

    public Task<List<DeviceBattery>> ReadAsync()
    {
        return Task.Run(ReadDevices);
    }

    private static List<DeviceBattery> ReadDevices()
    {
        var result = new List<DeviceBattery>();
        int matching = 0;
        int success = 0;

        foreach (var device in DeviceList.Local.GetHidDevices(0x3151))
        {
            if (device.ProductID != 0x5007)
                continue;

            int featureLength;
            try { featureLength = device.GetMaxFeatureReportLength(); }
            catch { continue; }

            Log.Write("  AJAZZ HID-интерфейс: feature=" + featureLength);
            if (featureLength < 65)
                continue;

            matching++;

            try
            {
                if (!device.TryOpen(out HidStream stream))
                {
                    Log.Write("  AJAZZ: интерфейс не удалось открыть");
                    continue;
                }

                using (stream)
                {
                    stream.ReadTimeout = 500;
                    stream.WriteTimeout = 500;

                    // Сохранён прежний экспериментальный запрос.
                    var poll = new byte[featureLength];
                    poll[0] = 0x00;
                    poll[1] = 0xF7;
                    stream.SetFeature(poll);
                    Thread.Sleep(30);

                    var report = new byte[featureLength];
                    report[0] = 0x05;
                    stream.GetFeature(report);

                    Log.Write("  AJAZZ Feature 0x05: " +
                              BitConverter.ToString(report, 0,
                                  Math.Min(8, report.Length)));

                    int level = report[3];
                    bool emptyReport =
                        report[1] == 0 && report[2] == 0 && report[3] == 0;

                    if (!emptyReport && level <= 100)
                    {
                        const string name = "AJAZZ 2.4G";
                        result.Add(new DeviceBattery(name, level));
                        Log.Write("  AJAZZ: заряд " + level + "%");
                        success++;
                    }
                    else
                    {
                        Log.Write(emptyReport
                            ? "  AJAZZ: приёмник не передал данные о заряде"
                            : "  AJAZZ: некорректное значение " + level);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("  AJAZZ: ошибка чтения: " +
                          ex.GetType().Name + ": " + ex.Message);
            }
        }

        Log.Write("  AJAZZ: подходящих интерфейсов=" + matching +
                  ", успешно прочитано=" + success);

        return result;
    }
}
