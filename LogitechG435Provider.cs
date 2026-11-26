using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BatteryTaskbar;

// Logitech G435 LIGHTSPEED на донгле 046D:0ACB (Realtek).
// У донгла нет обычного канала состояния: заряд читается через его служебный мост команд PA в режиме bootloader.
// Последовательность описана в открытых проектах (MIT): deddroid/logitech-g435-battery, fr0styJS/g435-lightspeed-battery,
// HaloBattery: вход в bridge, команды PA через feature-отчёт 6, выход из bridge.
// ВАЖНО: на время чтения (около секунды) донгл приглушает звук и микрофон, поэтому чтение выключено по умолчанию,
// а включённое работает не чаще раза в 15 минут. Если звук не вернулся, достаточно вынуть и снова вставить донгл.
// Код написан без доступа к реальному устройству автором и сверен только по открытым описаниям.
public sealed class LogitechG435Provider : IBatteryProvider
{
    const int VendorId = 0x046D;
    const int ProductId = 0x0ACB;
    const int ProductIdNewRevision = 0x0ADF;

    const int ReportLength = 64;     // номер отчёта + 63 байта
    const int OutputLength = 515;    // по этому размеру находим нужный служебный интерфейс
    const byte ReportId = 0x06;

    const byte CatOta = 0x06;
    const byte CatLink = 0x0E;
    const byte CmdGetMode = 0xE0;
    const byte CmdSetMode = 0xE1;
    const byte CmdGetConnect = 0xE3;

    const int MinMillivolts = 3000;
    const int MaxMillivolts = 4400;

    static readonly TimeSpan OkInterval = TimeSpan.FromMinutes(15);
    static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(20);
    static readonly TimeSpan SessionLimit = TimeSpan.FromSeconds(4);

    static readonly (int Millivolts, int Percent)[] VoltageCurve =
    {
        (4200, 100), (4150, 95), (4100, 90), (4050, 85),
        (4000, 80), (3950, 75), (3900, 70), (3850, 62),
        (3800, 55), (3750, 45), (3700, 35), (3650, 25),
        (3600, 15), (3550, 10), (3500, 5), (3400, 0)
    };

    public static string LastStatus { get; private set; } = "Опрос выключен";
    static volatile bool forceNext;
    public static void RequestImmediateRead() => forceNext = true;

    readonly AppSettings settings;
    readonly object gate = new();
    DateTime nextPollUtc = DateTime.MinValue;
    BatteryDevice? cached;
    DateTime cachedAtUtc = DateTime.MinValue;
    int frameLayout; // 0: [06 04 03 00 len ...], 1: [06 04 03 len 00 ...]; подбирается по ответу донгла

    public LogitechG435Provider(AppSettings appSettings)
    {
        settings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    }

    public string Name => "Logitech G435 LIGHTSPEED";

    public Task<List<BatteryDevice>> ReadAsync() => Task.Run(ReadAll);

    List<BatteryDevice> ReadAll()
    {
        var result = new List<BatteryDevice>();
        try
        {
            if (!settings.AllowG435AcbPolling)
            {
                LastStatus = "Опрос выключен в настройках";
                Log.Debug("  G435: опрос выключен в настройках");
                return result;
            }

            HidDevice? control = FindControlInterface();
            if (control == null)
            {
                bool newRevision = DeviceList.Local.GetHidDevices(VendorId, ProductIdNewRevision).Any();
                LastStatus = newRevision
                    ? "Найден донгл 046D:0ADF: эта ревизия пока не поддерживается"
                    : "Донгл G435 (046D:0ACB) не найден";
                Log.Debug("  G435: " + LastStatus);
                return result;
            }

            if (!ReservePoll())
            {
                AddCached(result);
                return result;
            }

            try
            {
                int millivolts = ReadMillivolts(control);
                int percent = VoltageToPercent(millivolts);
                cached = new BatteryDevice("Наушники Logitech G435", percent, "LIGHTSPEED, оценка по напряжению");
                cachedAtUtc = DateTime.UtcNow;
                LastStatus = "Последнее чтение " + DateTime.Now.ToString("HH:mm") + ": " + millivolts + " мВ, около " + percent + "%";
                Log.Debug("  G435: " + millivolts + " мВ, около " + percent + "%");
                lock (gate) nextPollUtc = DateTime.UtcNow + OkInterval;
            }
            catch (Exception ex)
            {
                LastStatus = "Ошибка чтения: " + ex.Message;
                Log.Write("  G435: ошибка чтения: " + ex.Message);
                lock (gate) nextPollUtc = DateTime.UtcNow + RetryInterval;
            }

            AddCached(result);
        }
        catch (Exception ex)
        {
            Log.Write("  G435: ошибка: " + ex.Message);
        }
        return result;
    }

    bool ReservePoll()
    {
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;
            if (forceNext)
            {
                forceNext = false;
                nextPollUtc = now + RetryInterval;
                return true;
            }
            if (now < nextPollUtc) return false;
            // Пока идёт чтение, следующий опрос не начинаем; после результата срок уточняется
            nextPollUtc = now + RetryInterval;
            return true;
        }
    }

    void AddCached(List<BatteryDevice> result)
    {
        if (cached != null && DateTime.UtcNow - cachedAtUtc <= CacheLifetime)
            result.Add(cached);
    }

    static HidDevice? FindControlInterface()
    {
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId, ProductId))
        {
            try
            {
                if (dev.GetMaxFeatureReportLength() >= ReportLength &&
                    dev.GetMaxOutputReportLength() >= OutputLength)
                    return dev;
            }
            catch { }
        }
        return null;
    }

    static bool IsPlausible(int millivolts) => millivolts >= MinMillivolts && millivolts <= MaxMillivolts;

    static int VoltageToPercent(int millivolts)
    {
        if (millivolts >= VoltageCurve[0].Millivolts) return 100;
        for (int i = 0; i < VoltageCurve.Length - 1; i++)
        {
            var high = VoltageCurve[i];
            var low = VoltageCurve[i + 1];
            if (millivolts < low.Millivolts) continue;
            double fraction = (millivolts - low.Millivolts) / (double)(high.Millivolts - low.Millivolts);
            return (int)Math.Round(low.Percent + fraction * (high.Percent - low.Percent));
        }
        return 0;
    }

    int ReadMillivolts(HidDevice device)
    {
        if (!device.TryOpen(out HidStream stream))
            throw new IOException("не удалось открыть служебный интерфейс донгла");

        using (stream)
        {
            stream.ReadTimeout = 100;
            var session = new Session(stream, device.GetMaxInputReportLength(), frameLayout,
                DateTime.UtcNow + SessionLimit);

            bool entered = false;
            bool modeChanged = false;
            int originalMode = 0;
            try
            {
                entered = true; // выход из bridge нужен даже если вход закончился ошибкой
                session.Control(0x01); // вход в bootloader/bridge
                session.Drain();

                // Первая команда заодно подбирает формат кадра
                int? mode = session.QueryProbe(CatLink, 1, new byte[] { CmdGetMode },
                    buffer => FindRemoteValue(buffer, CmdGetMode), 600);
                frameLayout = session.Layout;
                if (mode == null)
                    throw new IOException("донгл не ответил на служебный запрос (гарнитура выключена или не подошёл формат кадра): "
                                          + session.LastRawHex());

                originalMode = mode.Value;
                if (originalMode != 1)
                {
                    session.Send(CatLink, 2, new byte[] { CmdSetMode, 0x01 }); // команда доходит до гарнитуры только в режиме remote
                    modeChanged = true;
                    Thread.Sleep(120);
                    session.Drain();
                }

                int? connected = session.Query(CatLink, 1, new byte[] { CmdGetConnect },
                    buffer => FindRemoteValue(buffer, CmdGetConnect), 500);
                if (connected == 0)
                    throw new IOException("гарнитура G435 выключена или не на связи");

                int? raw = session.Query(CatOta, 1, new byte[] { 0x00, 0x31 }, FindBatteryMillivolts, 1000);
                if (raw is int millivolts && IsPlausible(millivolts))
                    return millivolts;

                throw new IOException("донгл не вернул напряжение аккумулятора (получено: "
                                      + (raw.HasValue ? raw.Value + " мВ" : "ничего") + ") " + session.LastRawHex());
            }
            finally
            {
                if (modeChanged)
                {
                    try
                    {
                        session.Send(CatLink, 2, new byte[] { CmdSetMode, (byte)originalMode });
                        Thread.Sleep(60);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("  G435: не удалось вернуть режим донгла: " + ex.Message);
                    }
                }

                if (entered)
                {
                    try { session.Control(0x08); }
                    catch
                    {
                        try
                        {
                            Thread.Sleep(100);
                            session.Control(0x08);
                        }
                        catch (Exception ex)
                        {
                            Log.Write("  G435: не удалось выйти из bridge, вытащите и вставьте донгл (" + ex.Message + ")");
                        }
                    }
                }
            }
        }
    }

    // Ответ на GET_BATTERY: 07 00 06 <seq|80> 00 <мВ младший> <мВ старший> ..
    static int? FindBatteryMillivolts(byte[] buffer)
    {
        for (int i = 0; i <= buffer.Length - 7; i++)
        {
            if (buffer[i] == 0x07 && buffer[i + 1] == 0x00 &&
                buffer[i + 2] == CatOta && (buffer[i + 3] & 0x80) != 0 &&
                buffer[i + 4] == 0x00)
                return buffer[i + 5] | (buffer[i + 6] << 8);
        }
        return null;
    }

    // Ответ на REMOTE_GET_*: 02 00 <команда> <значение>
    static int? FindRemoteValue(byte[] buffer, byte command)
    {
        for (int i = 0; i <= buffer.Length - 4; i++)
        {
            if (buffer[i] == 0x02 && buffer[i + 1] == 0x00 && buffer[i + 2] == command)
                return buffer[i + 3];
        }
        return null;
    }

    sealed class Session
    {
        readonly HidStream stream;
        readonly int inputLength;
        readonly DateTime deadline;
        byte sequence = 1;
        byte[]? lastRaw;

        public int Layout { get; private set; }

        public Session(HidStream hidStream, int inputReportLength, int layout, DateTime deadlineUtc)
        {
            stream = hidStream;
            inputLength = Math.Max(8, inputReportLength);
            Layout = layout;
            deadline = deadlineUtc;
        }

        public string LastRawHex()
        {
            if (lastRaw == null) return "(ответов не было)";
            return "ответ донгла: " + BitConverter.ToString(lastRaw, 0, Math.Min(24, lastRaw.Length));
        }

        // Вход (0x01) или выход (0x08) из bridge: feature-отчёт 06 <операция> 03
        public void Control(byte operation)
        {
            var report = new byte[ReportLength];
            report[0] = ReportId;
            report[1] = operation;
            report[2] = 0x03;
            stream.SetFeature(report);
            WaitAck(150);
        }

        // Подтверждение 06 5A приходит во входном отчёте; его отсутствие не считаем ошибкой
        void WaitAck(int milliseconds)
        {
            var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            var input = new byte[inputLength];
            while (DateTime.UtcNow < end)
            {
                try
                {
                    int count = stream.Read(input);
                    if (count >= 2 && input[0] == ReportId && input[1] == 0x5A) return;
                }
                catch (TimeoutException) { }
            }
        }

        public void Drain()
        {
            for (int i = 0; i < 16; i++)
            {
                var report = new byte[ReportLength];
                report[0] = ReportId;
                stream.GetFeature(report);
                if (report[1] == 0xFF) break;
            }
        }

        byte NextSequence()
        {
            byte current = sequence;
            sequence = (byte)(sequence % 127 + 1);
            return current;
        }

        // Команда PA: 50 41 <категория> <номер> <длина> <команда...>, внутри кадра отчёта 06 04 03 ..
        public void Send(byte category, byte length, byte[] payload) => Send(category, length, payload, Layout);

        void Send(byte category, byte length, byte[] payload, int layout)
        {
            var pa = new byte[5 + payload.Length];
            pa[0] = 0x50;
            pa[1] = 0x41;
            pa[2] = category;
            pa[3] = NextSequence();
            pa[4] = length;
            Buffer.BlockCopy(payload, 0, pa, 5, payload.Length);

            var report = new byte[ReportLength];
            report[0] = ReportId;
            report[1] = 0x04;
            report[2] = 0x03;
            if (layout == 0) { report[3] = 0x00; report[4] = (byte)pa.Length; }
            else { report[3] = (byte)pa.Length; report[4] = 0x00; }
            Buffer.BlockCopy(pa, 0, report, 5, pa.Length);
            stream.SetFeature(report);
        }

        public int? Query(byte category, byte length, byte[] payload, Func<byte[], int?> parse, int waitMilliseconds)
        {
            Send(category, length, payload);
            return ReadReply(parse, waitMilliseconds);
        }

        // Пробует сначала запомненный формат кадра, затем второй
        public int? QueryProbe(byte category, byte length, byte[] payload, Func<byte[], int?> parse, int waitMilliseconds)
        {
            int first = Layout;
            Send(category, length, payload, first);
            int? value = ReadReply(parse, waitMilliseconds);
            if (value.HasValue) return value;

            int second = first == 0 ? 1 : 0;
            Send(category, length, payload, second);
            value = ReadReply(parse, waitMilliseconds);
            if (value.HasValue) Layout = second;
            return value;
        }

        int? ReadReply(Func<byte[], int?> parse, int waitMilliseconds)
        {
            var end = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            if (end > deadline) end = deadline;
            while (DateTime.UtcNow < end)
            {
                var report = new byte[ReportLength];
                report[0] = ReportId;
                stream.GetFeature(report);
                if (report[1] != 0xFF)
                {
                    lastRaw = report;
                    int? value = parse(report);
                    if (value.HasValue) return value;
                }
                Thread.Sleep(30);
            }
            return null;
        }
    }
}
