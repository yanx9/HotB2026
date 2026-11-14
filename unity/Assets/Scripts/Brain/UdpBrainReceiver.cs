using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Ogrod.Brain
{
    // Odbiera wiadomości z części pythonowej (bci/) w formacie z docs/protocol.md
    // i zamienia je na zdarzenia BrainInput.
    public class UdpBrainReceiver : MonoBehaviour
    {
        const string Src = "EEG (UDP)";
        public int port = 5005;

        [Serializable]
        class Message
        {
            public string type;
            public string name;
            public float value;
            public double t;
        }

        readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        UdpClient client;
        Thread thread;
        volatile bool running;

        void OnEnable()
        {
            try
            {
                client = new UdpClient(port);
                running = true;
                thread = new Thread(ReceiveLoop) { IsBackground = true };
                thread.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UdpBrainReceiver] Nie można nasłuchiwać na porcie {port}: {e.Message}");
            }
        }

        void OnDisable()
        {
            running = false;
            client?.Close();
            client = null;
        }

        void ReceiveLoop()
        {
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    queue.Enqueue(Encoding.UTF8.GetString(client.Receive(ref endpoint)));
                }
                catch (Exception)
                {
                    if (!running) return;
                }
            }
        }

        void Update()
        {
            while (queue.TryDequeue(out var json)) Dispatch(json);
        }

        static void Dispatch(string json)
        {
            Message m;
            try { m = JsonUtility.FromJson<Message>(json); }
            catch (Exception) { return; }
            if (m == null || m.name == null) return;

            if (m.type == "command")
            {
                switch (m.name)
                {
                    case "select": BrainInput.RaiseSelect(Src); break;
                    case "left": BrainInput.RaiseLeft(Src); break;
                    case "right": BrainInput.RaiseRight(Src); break;
                }
            }
            else if (m.type == "metric" && m.name == "focus")
            {
                BrainInput.SetFocus(m.value, Src);
            }
        }
    }
}
