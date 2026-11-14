using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Precel.Signals
{
    // Odbiera sygnały z mostu LSL (bci/lsl_bridge.py) przez UDP i wpisuje je do SignalHub.
    // Jeden datagram = {"type","name","value","t"}; format: docs/protocol.md.
    // "metric" to wartość ciągła, "command" to zdarzenie jednorazowe.
    public class UdpSignalSource : MonoBehaviour
    {
        [Serializable]
        class Message
        {
            public string type;
            public string name;
            public float value;
            public double t;
        }

        public int port = 5005;

        readonly ConcurrentQueue<(Message message, IPAddress from)> inbox = new ConcurrentQueue<(Message, IPAddress)>();
        UdpClient client;
        Thread thread;
        volatile bool running;

        public static IPAddress LastSender { get; private set; }
        public static float LastPacketAt { get; private set; } = -100f;

        void OnEnable()
        {
            try
            {
                client = new UdpClient(port);
            }
            catch (SocketException e)
            {
                Debug.LogWarning($"Port UDP {port} jest zajęty ({e.SocketErrorCode}). Sygnały z mostu nie dotrą; działa klawiatura.");
                return;
            }
            running = true;
            thread = new Thread(Listen) { IsBackground = true, Name = "Sygnały UDP" };
            thread.Start();
        }

        void OnDisable()
        {
            running = false;
            client?.Close();
            client = null;
        }

        void Listen()
        {
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    byte[] data = client.Receive(ref endpoint);
                    var message = JsonUtility.FromJson<Message>(Encoding.UTF8.GetString(data));
                    if (message != null && !string.IsNullOrEmpty(message.name)) inbox.Enqueue((message, endpoint.Address));
                }
                catch (SocketException) { }          // zamknięcie gniazda przy wyłączaniu
                catch (ObjectDisposedException) { }
                catch (ArgumentException) { }        // datagram, który nie jest JSON-em
            }
        }

        void Update()
        {
            while (inbox.TryDequeue(out var item))
            {
                SignalHub.Publish(item.message.name, item.message.value, "udp", item.message.type == "command");
                LastSender = item.from;
                LastPacketAt = Time.unscaledTime;
            }
        }
    }
}
