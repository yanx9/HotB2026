using UnityEngine;
using UnityEngine.InputSystem;

namespace Bci.Signals
{
    // Zastępuje sygnały z potoku BCI klawiaturą: publikuje w SignalHub te same nazwy sygnałów,
    // których słuchają powiązania. Dopóki klawisz jest wciśnięty, nadpisuje sygnał z potoku.
    public class KeyboardSignalSource : MonoBehaviour
    {
        public SignalBindings bindings;

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || bindings == null || bindings.config == null) return;

            foreach (var binding in bindings.config.bindings)
            {
                bool active = false, released = false;
                float value = 0f;
                if (binding.key == "arrows")
                {
                    bool left = keyboard.leftArrowKey.isPressed || keyboard.aKey.isPressed;
                    bool right = keyboard.rightArrowKey.isPressed || keyboard.dKey.isPressed;
                    active = left || right;
                    value = (right ? 1f : 0f) - (left ? 1f : 0f);
                    released = keyboard.leftArrowKey.wasReleasedThisFrame || keyboard.rightArrowKey.wasReleasedThisFrame
                               || keyboard.aKey.wasReleasedThisFrame || keyboard.dKey.wasReleasedThisFrame;
                }
                else if (binding.key == "space")
                {
                    active = keyboard.spaceKey.isPressed;
                    value = 1f;
                    released = keyboard.spaceKey.wasReleasedThisFrame;
                }
                if (!active && !released) continue;

                // odwracamy przeliczenie z powiązania, żeby pełne wciśnięcie dało w grze pełne wychylenie
                float raw = active ? binding.center + (binding.invert ? -value : value) / Mathf.Max(1e-4f, binding.gain) : binding.center;
                SignalHub.Publish(binding.signal, raw, "klawiatura");
                SignalHub.Publish(SignalBindings.ValidName(binding.signal), 1f, "klawiatura");   // klawisz przebija "nieważny" z potoku
            }
        }
    }
}
