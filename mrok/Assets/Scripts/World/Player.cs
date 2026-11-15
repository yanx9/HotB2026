using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mrok.World
{
    // Chodzenie z pierwszej osoby: WASD, mysz albo Q / E do obracania, Shift do skradania.
    // Każdy krok zgłasza hałas o zasięgu zależnym od tempa.
    public class Player : MonoBehaviour
    {
        const float WalkSpeed = 3f, SneakSpeed = 1.3f, SprintSpeed = 5.2f;
        const float WalkNoise = 6f, SneakNoise = 3f, SprintNoise = 10f;   // z jak daleka słychać krok
        public const float Height = 1.7f;
        const float StepLength = 1.3f;

        public Action<float> Stepped;   // argument: zasięg hałasu
        public bool frozen;
        public bool sprint;             // ustawia gra: zaciśnięta szczęka; bieg jest szybki i głośny

        CharacterController body;
        Transform eyes;
        float yaw, pitch, sinceStep;

        public Vector3 EyePosition => transform.position + Vector3.up * (Height - 0.1f);

        public void Build(Camera cam, Vector3 start, float facing)
        {
            gameObject.layer = 2;   // Ignore Raycast: skan nie może trafiać w samego gracza
            body = gameObject.AddComponent<CharacterController>();
            body.height = 1.7f;
            body.radius = 0.3f;
            body.center = new Vector3(0f, 0.87f, 0f);
            body.enabled = false;
            transform.position = start;
            body.enabled = true;

            eyes = cam.transform;
            eyes.SetParent(transform, false);
            eyes.localPosition = Vector3.up * (Height - 0.1f);
            eyes.localRotation = Quaternion.identity;
            yaw = facing;
        }

        public void Teleport(Vector3 position, float facing)
        {
            body.enabled = false;
            transform.position = position;
            body.enabled = true;
            yaw = facing;
        }

        void Update()
        {
            if (body == null) return;
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            float dt = Time.deltaTime;

            if (mouse != null && mouse.leftButton.wasPressedThisFrame) Cursor.lockState = CursorLockMode.Locked;

            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 look = mouse.delta.ReadValue() * 0.1f;
                yaw += look.x;
                pitch = Mathf.Clamp(pitch - look.y, -80f, 80f);
            }
            if (keyboard != null)
            {
                if (keyboard.qKey.isPressed) yaw -= 110f * dt;
                if (keyboard.eKey.isPressed) yaw += 110f * dt;
            }
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            eyes.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            Vector3 move = Vector3.zero;
            bool sneak = false;
            if (keyboard != null && !frozen)
            {
                float x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                float z = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                move = Vector3.ClampMagnitude(transform.right * x + transform.forward * z, 1f);
                sneak = keyboard.leftShiftKey.isPressed;
            }

            Vector3 before = transform.position;
            bool run = sprint && !sneak;
            body.Move((move * (sneak ? SneakSpeed : run ? SprintSpeed : WalkSpeed) + Vector3.down * 5f) * dt);
            Vector3 moved = transform.position - before;
            moved.y = 0f;
            sinceStep += moved.magnitude;
            if (sinceStep >= StepLength)
            {
                sinceStep = 0f;
                Stepped?.Invoke(sneak ? SneakNoise : run ? SprintNoise : WalkNoise);
            }
        }
    }
}
