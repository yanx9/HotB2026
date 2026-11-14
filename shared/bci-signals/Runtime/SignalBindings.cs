using System;
using System.Collections.Generic;
using UnityEngine;

namespace Bci.Signals
{
    // Powiązanie: która czynność w grze słucha którego sygnału i jak go przeliczać.
    // Wczytywane z Resources/signals.json, więc zmiana sygnału sterującego nie wymaga zmian w kodzie.
    [Serializable]
    public class Binding
    {
        public string action;          // nazwa czynności w grze, np. "steer"
        public string signal;          // nazwa sygnału w SignalHub, np. "laterality"
        public string fallback;        // sygnał zastępczy, gdy pierwszy nie dociera (np. most bez profilu kalibracji)
        public float center;           // wartość sygnału uznawana za zero
        public float gain = 1f;
        public float deadzone;         // martwa strefa po przeliczeniu
        public bool invert;
        public float smoothing;        // stała czasowa wygładzania w sekundach
        public float threshold = 0.5f; // próg dla Held()
        public string key;             // klawisze symulujące sygnał: "arrows" albo "space"

        [NonSerialized] public float value;
        [NonSerialized] public bool held;
        [NonSerialized] public bool pressed;
        [NonSerialized] public bool live;
        [NonSerialized] public string source;   // sygnał, z którego czynność korzysta w tej chwili
    }

    [Serializable]
    public class BindingConfig
    {
        public int port = 5005;
        public int markerPort = 5006;
        public List<Binding> bindings = new List<Binding>();
    }

    public class SignalBindings : MonoBehaviour
    {
        public BindingConfig config;
        readonly Dictionary<string, Binding> byAction = new Dictionary<string, Binding>();

        public static BindingConfig Load()
        {
            var asset = Resources.Load<TextAsset>("signals");
            if (asset == null)
            {
                Debug.LogError("Brak Resources/signals.json: żadna czynność nie ma sygnału.");
                return new BindingConfig();
            }
            return JsonUtility.FromJson<BindingConfig>(asset.text);
        }

        public void Use(BindingConfig bindingConfig)
        {
            config = bindingConfig;
            byAction.Clear();
            foreach (var binding in config.bindings) byAction[binding.action] = binding;
        }

        public Binding Get(string action)
        {
            if (byAction.Count == 0 && config != null) Use(config);   // po przeładowaniu skryptów
            return byAction.TryGetValue(action, out var binding) ? binding : null;
        }

        // wartość czynności w zakresie -1..1 (0, gdy sygnał nie dociera)
        public float Axis(string action) => Get(action)?.value ?? 0f;

        // sygnał powyżej progu (z histerezą, żeby nie migotał na granicy)
        public bool Held(string action) => Get(action)?.held ?? false;

        // true przez jedną klatkę, gdy sygnał przekroczył próg
        public bool Pressed(string action) => Get(action)?.pressed ?? false;

        // Nazwa flagi ważności, którą most z profilem kalibracji nadaje obok sygnału:
        // "jaw" i "jaw_active" mają wspólną flagę "jaw_valid".
        public static string ValidName(string signal) =>
            (signal.EndsWith("_active") ? signal.Substring(0, signal.Length - "_active".Length) : signal) + "_valid";

        // Sygnał liczy się, gdy jest świeży i most nie oznaczył go jako nieważny
        // (brak kalibracji, artefakt, przerwa w danych). Bez flagi ważności wystarczy świeżość.
        static bool Read(string signal, out float value)
        {
            if (!SignalHub.TryGet(signal, out value)) return false;
            return !SignalHub.TryGet(ValidName(signal), out float valid) || valid >= 0.5f;
        }

        void Update()
        {
            if (config == null) return;
            foreach (var binding in config.bindings)
            {
                binding.source = binding.signal;
                binding.live = Read(binding.signal, out float raw);
                if (!binding.live && !string.IsNullOrEmpty(binding.fallback))
                {
                    binding.source = binding.fallback;
                    binding.live = Read(binding.fallback, out raw);
                }
                float target = 0f;
                if (binding.live)
                {
                    target = (raw - binding.center) * binding.gain;
                    if (binding.invert) target = -target;
                    float size = Mathf.Abs(target);
                    target = size <= binding.deadzone
                        ? 0f
                        : Mathf.Sign(target) * Mathf.Clamp01((size - binding.deadzone) / Mathf.Max(1e-4f, 1f - binding.deadzone));
                }
                float blend = binding.smoothing <= 0f ? 1f : 1f - Mathf.Exp(-Time.unscaledDeltaTime / binding.smoothing);
                binding.value = Mathf.Lerp(binding.value, target, blend);

                bool was = binding.held;
                binding.held = binding.value >= (was ? binding.threshold * 0.7f : binding.threshold);
                binding.pressed = binding.held && !was;
            }
        }
    }
}
