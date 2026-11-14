using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Bci.Signals
{
    // Znaczniki zdarzeń gry wysyłane do mostu (UDP), który publikuje je w LSL obok EEG.
    // Trafiają na adres, z którego przychodzą sygnały; dopóki nic nie przyszło, na localhost.
    public static class MarkerSender
    {
        public static int Port = 5006;
        static UdpClient client;

        public static void Send(string name, float value = 0f)
        {
            try
            {
                client ??= new UdpClient();
                double t = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\"type\":\"marker\",\"name\":\"{0}\",\"value\":{1:0.###},\"t\":{2:0.000}}}", name, value, t);
                byte[] data = Encoding.UTF8.GetBytes(json);
                client.Send(data, data.Length, new IPEndPoint(UdpSignalSource.LastSender ?? IPAddress.Loopback, Port));
            }
            catch (SocketException) { }   // brak odbiorcy nie może zatrzymać gry
        }
    }
}
