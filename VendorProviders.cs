using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BatteryTaskbar;

// Устройства на фирменных донглах, у которых заряд лежит в feature-отчёте служебного канала.
// Приёмник обновляет заряд только после служебного запроса статуса (его же шлёт фирменная программа
// примерно раз в секунду): без запроса он отдаёт старое значение. Запрос без данных, только чтение статуса.
// Чтобы добавить устройство, допишите строку в Specs.
public sealed class FeatureReportProvider : IBatteryProvider
{
    sealed record Spec(int Vid, int Pid, string Name, byte ReportId, int Length, int LevelIndex, byte PollCommand);

    static readonly Spec[] Specs =
    {
        // Мышь AJAZZ AJ159 APEX / Angry Miao AM Infinity 8K (донгл 3151:5007).
        // Запрос: feature-отчёт 0x00, затем байт 0xF7 и нули. Ответ: отчёт 0x05, байт 3 = заряд в процентах.
        new Spec(0x3151, 0x5007, "Мышь AJAZZ", 0x05, 65, 3, 0xF7),
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
                        int level = 0;
                        byte[]? last = null;

                        // Несколько попыток: приёмнику нужно время, чтобы получить заряд от мыши после запроса
                        for (int attempt = 1; attempt <= 6 && level == 0; attempt++)
                        {
                            bool sent = true;
                            try
                            {
                                var req = new byte[spec.Length]; // req[0] = 0: номер отчёта
                                req[1] = spec.PollCommand;
                                stream.SetFeature(req);
                            }
                            catch (Exception ex)
                            {
                                sent = false;
                                Log.Write("  " + spec.Name + ": запрос статуса не отправился (ошибка: " + ex.Message + ")");
                            }

                            Thread.Sleep(200);

                            var buf = new byte[spec.Length];
                            buf[0] = spec.ReportId;
                            stream.GetFeature(buf);
                            last = buf;

                            level = buf[spec.LevelIndex];
                            if (level > 100) level = 0;
                            if (!sent) break;
                        }

                        if (last != null)
                            Log.Debug("  " + spec.Name + " feature#" + spec.ReportId + ": " +
                                      BitConverter.ToString(last, 0, Math.Min(12, last.Length)));

                        if (level > 0)
                            list.Add(new BatteryDevice(spec.Name, level, "HID feature"));
                        else
                            Log.Debug("  " + spec.Name + ": заряд не передан, мышь спит или не на связи");
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
