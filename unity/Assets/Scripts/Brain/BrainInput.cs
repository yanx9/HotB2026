using System;
using UnityEngine;

namespace Ogrod.Brain
{
    // Jedyny punkt styku gry z BCI. Gra słucha tych zdarzeń i nie wie, skąd pochodzą:
    // z klawiatury (KeyboardBrainSimulator) czy z EEG przez UDP (UdpBrainReceiver).
    public static class BrainInput
    {
        public static event Action Select;   // P300: "rozpoznałem cel"
        public static event Action Left;     // motor imagery: lewa ręka
        public static event Action Right;    // motor imagery: prawa ręka

        public static float Focus { get; private set; } = 0.5f;   // metryka 0–1 z widma EEG
        public static string Source { get; private set; } = "brak";
        public static float LastEventTime { get; private set; } = -999f;

        public static void RaiseSelect(string source) { Mark(source); Select?.Invoke(); }
        public static void RaiseLeft(string source) { Mark(source); Left?.Invoke(); }
        public static void RaiseRight(string source) { Mark(source); Right?.Invoke(); }
        public static void SetFocus(float value, string source) { Mark(source); Focus = Mathf.Clamp01(value); }

        static void Mark(string source)
        {
            Source = source;
            LastEventTime = Time.unscaledTime;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            Select = null; Left = null; Right = null;
            Focus = 0.5f; Source = "brak"; LastEventTime = -999f;
        }
    }
}
