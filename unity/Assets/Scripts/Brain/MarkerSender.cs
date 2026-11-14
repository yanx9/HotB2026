using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Ogrod.Brain
{
    // Znaczniki zdarzeń gry (początek bodźca, odpowiedź gracza) wysyłane do bci/ przez UDP
    // i zapisywane do CSV. Bez nich nie da się pociąć EEG na epoki do kalibracji P300.
    public static class MarkerSender
    {
        public const int Port = 5006;
        // adres ustawia UdpBrainReceiver: znaczniki wracają tam, skąd przychodzi sygnał BCI,
        // więc most może działać na innym komputerze
        public static volatile string Host = "127.0.0.1";
        static UdpClient client;
        static StreamWriter log;

        public static string LogPath { get; private set; }

        public static void Send(string name, float value = 0f)
        {
            double t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            string ts = t.ToString("F3", CultureInfo.InvariantCulture);
            string val = value.ToString("0.###", CultureInfo.InvariantCulture);
            try
            {
                client ??= new UdpClient();
                byte[] data = Encoding.UTF8.GetBytes(
                    $"{{\"type\": \"marker\", \"name\": \"{name}\", \"value\": {val}, \"t\": {ts}}}");
                client.Send(data, data.Length, Host, Port);

                if (log == null)
                {
                    LogPath = Path.Combine(Application.persistentDataPath,
                        $"markers_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    log = new StreamWriter(LogPath) { AutoFlush = true };
                    log.WriteLine("t,name,value");
                }
                log.WriteLine($"{ts},{name},{val}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MarkerSender] {e.Message}");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            log?.Dispose(); log = null;
            client?.Close(); client = null;
            Host = "127.0.0.1";
        }
    }
}
