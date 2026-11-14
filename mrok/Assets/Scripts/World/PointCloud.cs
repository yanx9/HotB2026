using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mrok.World
{
    // Jedyna rzecz, którą widać w grze: punkty zostawione przez skan. Skan wypuszcza promienie
    // we wszystkie strony z miejsca, w którym stoi gracz, i zapisuje trafienia w jednej siatce.
    // Punkty nie ruszają się razem ze światem: strażnik zostaje na "stopklatce" tam, gdzie był.
    public class PointCloud
    {
        const int Capacity = 160000;        // starsze punkty są nadpisywane
        public const int Rays = 36000;

        static readonly int NowId = Shader.PropertyToID("_Now");

        readonly Vector3[] positions = new Vector3[Capacity * 4];
        readonly Vector2[] scans = new Vector2[Capacity * 4];
        readonly Color[] colors = new Color[Capacity * 4];
        Mesh mesh;
        int next;

        public int LastHits { get; private set; }

        public void Build(Transform root)
        {
            var corners = new Vector2[Capacity * 4];
            var triangles = new int[Capacity * 6];
            for (int i = 0; i < Capacity; i++)
            {
                int v = i * 4, t = i * 6;
                corners[v] = new Vector2(-1f, -1f);
                corners[v + 1] = new Vector2(1f, -1f);
                corners[v + 2] = new Vector2(1f, 1f);
                corners[v + 3] = new Vector2(-1f, 1f);
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v; triangles[t + 4] = v + 2; triangles[t + 5] = v + 3;
                for (int k = 0; k < 4; k++) scans[v + k] = new Vector2(-1000f, 0f);   // "dawno zgasły"
            }

            mesh = new Mesh { name = "Chmura punktów", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            mesh.SetVertices(positions);
            mesh.SetUVs(0, corners);
            mesh.SetUVs(1, scans);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);   // nigdy nie odcinana

            var go = new GameObject("Chmura punktów");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = new Material(Resources.Load<Shader>("Shaders/PointCloud"));
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        public void Tick()
        {
            Shader.SetGlobalFloat(NowId, Time.time);
        }

        public void Scan(Vector3 origin, float radius, Dictionary<Collider, Surface> surfaces)
        {
            // promienie po spirali Fibonacciego na sferze, przy każdym skanie obróconej inaczej wokół pionu
            var twist = Quaternion.Euler(0f, Random.value * 360f, 0f);
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            float now = Time.time;
            int hits = 0;
            for (int i = 0; i < Rays; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / Rays;
                y = 0.45f * y + 0.55f * y * Mathf.Abs(y);   // zagęszczenie przy poziomie: więcej punktów na ścianach, mniej na podłodze
                float ring = Mathf.Sqrt(1f - y * y);
                float angle = golden * i;
                Vector3 direction = twist * new Vector3(Mathf.Cos(angle) * ring, y, Mathf.Sin(angle) * ring);
                direction += Random.insideUnitSphere * 0.012f;   // bez drgania spirala układa się w widoczny wzór
                if (!Physics.Raycast(origin, direction, out var hit, radius)) continue;
                hits++;

                surfaces.TryGetValue(hit.collider, out var surface);
                Color color = ColorOf(surface, hit.distance / radius, hit.normal);
                int v = next * 4;
                for (int k = 0; k < 4; k++)
                {
                    positions[v + k] = hit.point;
                    scans[v + k] = new Vector2(now, hit.distance);
                    colors[v + k] = color;
                }
                next = (next + 1) % Capacity;
            }
            LastHits = hits;

            mesh.SetVertices(positions);
            mesh.SetUVs(1, scans);
            mesh.SetColors(colors);
        }

        static Color ColorOf(Surface surface, float far, Vector3 normal)
        {
            switch (surface)
            {
                case Surface.Guard: return new Color(2.4f, 0.55f, 0.12f);
                case Surface.Loot: return new Color(2.2f, 1.7f, 0.3f);
                case Surface.Exit: return new Color(0.4f, 2.2f, 0.5f);
            }
            // mury: blisko morska zieleń, daleko fiolet i jaśniej, bo dalekie punkty są mniejsze;
            // podłoga ciemna, żeby nie zalewała obrazu
            Color wall = Color.Lerp(new Color(0.05f, 0.9f, 0.7f), new Color(0.38f, 0.1f, 1f), Mathf.Sqrt(far)) * (1f + far);
            return wall * (Mathf.Abs(normal.y) > 0.7f ? 0.22f : 1f);
        }
    }
}
