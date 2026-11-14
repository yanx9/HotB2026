using System.Collections.Generic;
using UnityEngine;

namespace Precel.Signals
{
    // Jeden nazwany sygnał: ostatnia wartość, kiedy przyszła i skąd.
    public class Signal
    {
        public string name;
        public float value;
        public string source;
        public float receivedAt;   // Time.unscaledTime
        public bool transient;     // zdarzenie jednorazowe: po chwili wraca do zera

        public float Age => Time.unscaledTime - receivedAt;
    }

    // Tablica wszystkich sygnałów, jakie dotarły do gry. Gra nie wie, co one znaczą
    // ani skąd pochodzą (most LSL, klawiatura, cokolwiek): zna tylko nazwy i liczby.
    // Powiązanie sygnału z czynnością w grze robi SignalBindings.
    public static class SignalHub
    {
        public const float StaleSeconds = 1f;       // ciągły sygnał bez nowych danych przestaje się liczyć
        public const float TransientSeconds = 0.3f; // tyle "trwa" zdarzenie jednorazowe

        static readonly Dictionary<string, Signal> signals = new Dictionary<string, Signal>();
        static readonly List<Signal> ordered = new List<Signal>();

        public static IReadOnlyList<Signal> All => ordered;

        public static void Publish(string name, float value, string source, bool transient = false)
        {
            if (!signals.TryGetValue(name, out var signal))
            {
                signal = new Signal { name = name };
                signals[name] = signal;
                ordered.Add(signal);
            }
            signal.value = value;
            signal.source = source;
            signal.transient = transient;
            signal.receivedAt = Time.unscaledTime;
        }

        // false, gdy sygnału nie ma albo jest nieświeży
        public static bool TryGet(string name, out float value)
        {
            value = 0f;
            if (!signals.TryGetValue(name, out var signal)) return false;
            if (signal.Age > (signal.transient ? TransientSeconds : StaleSeconds)) return false;
            value = signal.value;
            return true;
        }

        public static void Clear()
        {
            signals.Clear();
            ordered.Clear();
        }
    }
}
