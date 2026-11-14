using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Ogrod.Brain
{
    // Zaślepka na czas prac bez headsetu: klawiatura udaje zdarzenia z EEG.
    // Spacja = P300 (select), strzałki ←/→ = wyobrażenie ruchu ręki, ↑/↓ = skupienie.
    public class KeyboardBrainSimulator : MonoBehaviour
    {
        const string Src = "klawiatura (symulacja)";
        const float RepeatSeconds = 0.5f;
        float nextLeft, nextRight;

        void Update()
        {
            var k = Keyboard.current;
            if (k == null) return;

            if (k.spaceKey.wasPressedThisFrame) BrainInput.RaiseSelect(Src);
            if (Held(k.leftArrowKey, ref nextLeft)) BrainInput.RaiseLeft(Src);
            if (Held(k.rightArrowKey, ref nextRight)) BrainInput.RaiseRight(Src);

            float delta = (k.upArrowKey.isPressed ? 1f : 0f) - (k.downArrowKey.isPressed ? 1f : 0f);
            if (delta != 0f) BrainInput.SetFocus(BrainInput.Focus + delta * 0.4f * Time.unscaledDeltaTime, Src);
        }

        // przytrzymany klawisz ponawia zdarzenie, tak jak klasyfikator wysyłałby kolejne decyzje
        static bool Held(KeyControl key, ref float next)
        {
            if (!key.isPressed) { next = 0f; return false; }
            if (Time.unscaledTime < next) return false;
            next = Time.unscaledTime + RepeatSeconds;
            return true;
        }
    }
}
