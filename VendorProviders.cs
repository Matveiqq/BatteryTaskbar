using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HidSharp;

namespace BatteryTaskbar;

// Устройства на фирменных донглах, у которых заряд лежит в feature-отчёте служебного канала.
// Только ЧТЕНИЕ отчёта (GetFeature), ничего не отправляется.
// Чтобы добавить устройство, допишите строку в Specs.
public sealed class FeatureReportProvider : IBatteryProvider
{
    sealed record Spec(int Vid, int Pid, string Name, byte ReportId, int Length, int LevelIndex);

    static readonly Spec[] Specs =
    {
        // Мышь AJAZZ (семейство "AM Infinity 8K", донгл 3151:5007).
        // Формат по данным HaloBattery: отчёт 0x05, байт 3 = заряд в процентах (0x5D = 93%).
        new Spec(0x3151, 0x5007, "Мышь AJAZZ", 0x05, 65, 3),
    };

    public string Name => "Фирменные донглы (feature-отчёт)";

    public Task<List<BatteryDevice>> ReadAsync() => Task.Run(ReadAll);

    static List<BatteryDevice> ReadAll()
    {
        var list = new List<BatteryDevice>();
        foreach (var spec in Specs)
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(spec.Vid, spec.Pid))
            {
                try
                {
                    // нужный служебный интерфейс: feature-отчёт длиной 65 байт
                    if (dev.GetMaxFeatureReportLength() < spec.Length) continue;
                    if (!dev.TryOpen(out HidStream stream)) continue;
                    using (stream)
                    {
                        var buf = new byte[spec.Length];
                        buf[0] = spec.ReportId;
                        stream.GetFeature(buf);

                        Log.Write("  " + spec.Name + " feature#" + spec.ReportId + ": " +
                                  BitConverter.ToString(buf, 0, Math.Min(12, buf.Length)));

                        int level = buf[spec.LevelIndex];
                        if (level > 0 && level <= 100)
                            list.Add(new BatteryDevice(spec.Name, level, "HID feature"));
                        else
                            Log.Write("  " + spec.Name + ": заряд не передан (значение " + level + "), мышь спит или нужен запрос");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("  " + spec.Name + " ошибка: " + ex.Message);
                }
            }
        }
        return list;
    }
}
