using System.Collections.Generic;
using UnityEngine;

namespace Mrok.World
{
    // Sylwetka człowieka ze zderzaczy na stawach. Skan zapisuje ją w takiej pozie, w jakiej
    // akurat była: strażnika w pół kroku, posąg z uniesioną ręką.
    public class Figure
    {
        public readonly Transform root, chest, head;
        public readonly Transform hipL, hipR, kneeL, kneeR, shoulderL, shoulderR, elbowL, elbowR;

        readonly Dictionary<Collider, Surface> surfaces;
        readonly Surface surface;

        public Figure(Transform parent, Dictionary<Collider, Surface> surfaces, Surface surface, bool cap)
        {
            this.surfaces = surfaces;
            this.surface = surface;

            root = Joint(parent, "Postać", Vector3.zero);
            var pelvis = Joint(root, "Biodra", new Vector3(0f, 0.95f, 0f));
            Box(pelvis, new Vector3(0f, 0.02f, 0f), new Vector3(0.34f, 0.2f, 0.2f));
            chest = Joint(pelvis, "Tułów", new Vector3(0f, 0.1f, 0f));
            Box(chest, new Vector3(0f, 0.27f, 0f), new Vector3(0.4f, 0.5f, 0.22f));

            head = Joint(chest, "Głowa", new Vector3(0f, 0.54f, 0f));
            Limb(head, 0.03f, 0.1f, 0.05f);   // szyja
            var skull = head.gameObject.AddComponent<SphereCollider>();
            skull.center = new Vector3(0f, 0.14f, 0.01f);
            skull.radius = 0.115f;
            surfaces[skull] = surface;
            if (cap)
            {
                Box(head, new Vector3(0f, 0.25f, 0f), new Vector3(0.25f, 0.08f, 0.25f));
                Box(head, new Vector3(0f, 0.215f, 0.16f), new Vector3(0.2f, 0.02f, 0.12f));   // daszek
            }

            shoulderL = Joint(chest, "Ramię L", new Vector3(-0.25f, 0.47f, 0f));
            shoulderR = Joint(chest, "Ramię P", new Vector3(0.25f, 0.47f, 0f));
            elbowL = Arm(shoulderL);
            elbowR = Arm(shoulderR);
            hipL = Joint(pelvis, "Udo L", new Vector3(-0.1f, -0.05f, 0f));
            hipR = Joint(pelvis, "Udo P", new Vector3(0.1f, -0.05f, 0f));
            kneeL = Leg(hipL);
            kneeR = Leg(hipR);
        }

        static Transform Joint(Transform parent, string name, Vector3 position)
        {
            var joint = new GameObject(name).transform;
            joint.SetParent(parent, false);
            joint.localPosition = position;
            return joint;
        }

        void Box(Transform joint, Vector3 center, Vector3 size)
        {
            var box = joint.gameObject.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size;
            surfaces[box] = surface;
        }

        // kapsuła zwisająca ze stawu: od wysokości top do top - length
        void Limb(Transform joint, float top, float length, float radius)
        {
            var limb = joint.gameObject.AddComponent<CapsuleCollider>();
            limb.center = new Vector3(0f, top - length / 2f, 0f);
            limb.height = length;
            limb.radius = radius;
            surfaces[limb] = surface;
        }

        Transform Arm(Transform shoulder)
        {
            Limb(shoulder, 0.04f, 0.36f, 0.055f);
            var elbow = Joint(shoulder, "Łokieć", new Vector3(0f, -0.3f, 0f));
            Limb(elbow, 0.03f, 0.36f, 0.048f);
            return elbow;
        }

        Transform Leg(Transform hip)
        {
            Limb(hip, 0.05f, 0.52f, 0.08f);
            var knee = Joint(hip, "Kolano", new Vector3(0f, -0.44f, 0f));
            Limb(knee, 0.04f, 0.48f, 0.065f);
            Box(knee, new Vector3(0f, -0.42f, 0.06f), new Vector3(0.1f, 0.07f, 0.24f));   // stopa
            return knee;
        }

        // Chód: phase rośnie o pi na każdy krok, stride 0 (stoi) do 1 (biegnie), lean to pochylenie w stopniach.
        public void Walk(float phase, float stride, float lean)
        {
            float swing = Mathf.Sin(phase), lift = Mathf.Cos(phase);
            hipL.localRotation = Quaternion.Euler(-swing * 32f * stride, 0f, 0f);
            hipR.localRotation = Quaternion.Euler(swing * 32f * stride, 0f, 0f);
            // kolano ugina się, gdy noga idzie do przodu
            kneeL.localRotation = Quaternion.Euler(4f + Mathf.Max(0f, lift) * 50f * stride, 0f, 0f);
            kneeR.localRotation = Quaternion.Euler(4f + Mathf.Max(0f, -lift) * 50f * stride, 0f, 0f);
            shoulderL.localRotation = Quaternion.Euler(swing * 28f * stride, 0f, -5f);
            shoulderR.localRotation = Quaternion.Euler(-swing * 28f * stride, 0f, 5f);
            elbowL.localRotation = elbowR.localRotation = Quaternion.Euler(-(10f + 22f * stride), 0f, 0f);
            chest.localRotation = Quaternion.Euler(lean, swing * 6f * stride, 0f);
            root.localPosition = new Vector3(0f, (Mathf.Abs(lift) * 0.035f - 0.025f) * stride, 0f);
        }

        // Nieruchome pozy posągów.
        public void Statue(int pose)
        {
            Walk(0f, 0f, 0f);
            switch (pose % 4)
            {
                case 0:   // ręka wzniesiona w górę
                    shoulderR.localRotation = Quaternion.Euler(-155f, 0f, 12f);
                    elbowR.localRotation = Quaternion.Euler(-15f, 0f, 0f);
                    shoulderL.localRotation = Quaternion.Euler(10f, 0f, -18f);
                    hipL.localRotation = Quaternion.Euler(-14f, 0f, 0f);
                    kneeL.localRotation = Quaternion.Euler(24f, 0f, 0f);
                    head.localRotation = Quaternion.Euler(-18f, 15f, 0f);
                    break;
                case 1:   // dłonie wyciągnięte przed siebie
                    shoulderL.localRotation = Quaternion.Euler(-65f, 0f, 10f);
                    shoulderR.localRotation = Quaternion.Euler(-65f, 0f, -10f);
                    elbowL.localRotation = elbowR.localRotation = Quaternion.Euler(-35f, 0f, 0f);
                    head.localRotation = Quaternion.Euler(12f, 0f, 0f);
                    break;
                case 2:   // dłoń przy brodzie
                    shoulderR.localRotation = Quaternion.Euler(-35f, 0f, -8f);
                    elbowR.localRotation = Quaternion.Euler(-125f, 0f, 0f);
                    shoulderL.localRotation = Quaternion.Euler(-25f, 0f, 20f);
                    elbowL.localRotation = Quaternion.Euler(-80f, 0f, 0f);
                    head.localRotation = Quaternion.Euler(14f, -20f, 0f);
                    chest.localRotation = Quaternion.Euler(8f, -10f, 0f);
                    break;
                default:  // ręce rozłożone szeroko
                    shoulderL.localRotation = Quaternion.Euler(0f, 0f, -95f);
                    shoulderR.localRotation = Quaternion.Euler(0f, 0f, 80f);
                    elbowR.localRotation = Quaternion.Euler(0f, 0f, 40f);
                    hipR.localRotation = Quaternion.Euler(-10f, 0f, 6f);
                    kneeR.localRotation = Quaternion.Euler(20f, 0f, 0f);
                    head.localRotation = Quaternion.Euler(-8f, -25f, 0f);
                    break;
            }
        }
    }
}
