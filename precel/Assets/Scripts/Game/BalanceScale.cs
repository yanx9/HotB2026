using UnityEngine;

namespace Precel.Game
{
    // Waga szalkowa za deską: belka przechyla się w stronę cięższej połówki.
    public class BalanceScale
    {
        const float Arm = 1.5f;        // pół długości belki
        const float Hang = 0.8f;       // szalka wisi tyle pod końcem belki
        const float MaxTilt = 0.26f;   // radiany

        public Transform left, right;  // punkty zawieszenia szalek; połówki są ich dziećmi
        public float PanY => -Hang + 0.012f;

        Transform beam;
        float angle, velocity, target;

        public void Build(Transform root, Vector3 pivot)
        {
            var brass = Stage.Mat(new Color(0.80f, 0.62f, 0.25f), null, 0.7f);
            var steel = Stage.Mat(new Color(0.75f, 0.76f, 0.78f), null, 0.8f);

            float floor = -0.09f;
            Stage.Shape(PrimitiveType.Cylinder, root, "Podstawa wagi", new Vector3(pivot.x, floor + 0.03f, pivot.z), new Vector3(0.8f, 0.03f, 0.8f), brass);
            Stage.Shape(PrimitiveType.Cylinder, root, "Słup wagi", new Vector3(pivot.x, (pivot.y + floor) / 2f, pivot.z),
                new Vector3(0.09f, (pivot.y - floor) / 2f, 0.09f), brass);
            Stage.Shape(PrimitiveType.Sphere, root, "Gałka wagi", pivot + Vector3.up * 0.06f, Vector3.one * 0.16f, brass);

            beam = new GameObject("Belka wagi").transform;
            beam.SetParent(root, false);
            beam.localPosition = pivot;
            Stage.Box(beam, "Belka", Vector3.zero, new Vector3(Arm * 2f, 0.05f, 0.06f), brass);

            left = Pan(root, "Szalka lewa", brass, steel);
            right = Pan(root, "Szalka prawa", brass, steel);
            Place();
        }

        Transform Pan(Transform root, string name, Material brass, Material steel)
        {
            var hanger = new GameObject(name).transform;
            hanger.SetParent(root, false);
            Stage.Shape(PrimitiveType.Cylinder, hanger, "Talerz", new Vector3(0f, -Hang, 0f), new Vector3(1.3f, 0.012f, 1.3f), steel);
            Stage.Rod(hanger, "Cięgno", Vector3.zero, new Vector3(-0.63f, -Hang, 0f), 0.018f, brass);
            Stage.Rod(hanger, "Cięgno", Vector3.zero, new Vector3(0.63f, -Hang, 0f), 0.018f, brass);
            return hanger;
        }

        public void SetLoad(float leftGrams, float rightGrams)
        {
            float total = leftGrams + rightGrams;
            target = total < 0.01f ? 0f : Mathf.Clamp((leftGrams - rightGrams) / total * 2.5f, -MaxTilt, MaxTilt);
        }

        public void Tick(float dt)
        {
            // tłumiona sprężyna: belka buja się chwilę, zanim się ustali
            velocity += ((target - angle) * 28f - velocity * 3.2f) * dt;
            angle += velocity * dt;
            Place();
        }

        void Place()
        {
            beam.localRotation = Quaternion.Euler(0f, 0f, angle * Mathf.Rad2Deg);
            left.position = beam.TransformPoint(new Vector3(-Arm, 0f, 0f));
            right.position = beam.TransformPoint(new Vector3(Arm, 0f, 0f));
        }
    }
}
