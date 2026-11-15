using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mrok.World
{
    // Jedyna rzecz, którą widać w grze: punkty zostawione przez skan. Skan wypuszcza promienie
    // we wszystkie strony z miejsca, w którym stoi gracz, i zapisuje trafienia w jednej siatce.
    // Punkty nie ruszają się razem ze światem: strażnik zostaje na "stopklatce" tam, gdzie był.
    public class PointCloud
    {
        public const int Rays = 54000;
        const float ScanLife = 14f;          // po tylu sekundach punkt skanu gaśnie

        const MeshUpdateFlags Quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;

        static readonly int NowId = Shader.PropertyToID("_Now");

        // zmienna część wierzchołka; narożniki czworokątów leżą w osobnym, stałym strumieniu
        [StructLayout(LayoutKind.Sequential)]
        struct Vertex
        {
            public Vector3 position;
            public Color color;
            public Vector3 scan;            // (czas, odległość od źródła, czas gaśnięcia)
        }

        static Vector3[] directions;        // stały wzór promieni; skan tylko go obraca

        readonly int capacity;              // starsze punkty są nadpisywane
        readonly Vertex[] vertices;
        Mesh mesh;
        int next, pending;                  // pending: punkty dodane od ostatniego wysłania do karty

        public PointCloud(int capacity = 240000)
        {
            this.capacity = capacity;
            vertices = new Vertex[capacity * 4];
        }

        public int LastHits { get; private set; }

        public void Build(Transform root)
        {
            var corners = new Vector2[capacity * 4];
            var triangles = new int[capacity * 6];
            for (int i = 0; i < capacity; i++)
            {
                int v = i * 4, t = i * 6;
                corners[v] = new Vector2(-1f, -1f);
                corners[v + 1] = new Vector2(1f, -1f);
                corners[v + 2] = new Vector2(1f, 1f);
                corners[v + 3] = new Vector2(-1f, 1f);
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v; triangles[t + 4] = v + 2; triangles[t + 5] = v + 3;
                for (int k = 0; k < 4; k++) vertices[v + k].scan = new Vector3(-1000f, 0f, 1f);   // "dawno zgasły"
            }

            mesh = new Mesh { name = "Chmura punktów", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            mesh.SetVertexBufferParams(capacity * 4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 3, 0));
            mesh.SetVertexBufferData(vertices, 0, 0, vertices.Length, 0, Quiet);
            mesh.SetVertexBufferData(corners, 0, 0, corners.Length, 1, Quiet);
            mesh.SetTriangles(triangles, 0, false);
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

        public void Scan(Vector3 origin, float radius, Dictionary<Collider, Surface> surfaces, List<Sign> signs = null)
        {
            // promienie po spirali Fibonacciego na sferze, przy każdym skanie obróconej inaczej wokół pionu
            var twist = Quaternion.Euler(0f, Random.value * 360f, 0f);
            float now = Time.time;
            int hits = 0;
            // wszystkie promienie naraz, na wątkach roboczych: skan nie może zatrzymać gry w chwili otwarcia oczu
            directions ??= Pattern();
            var commands = new NativeArray<RaycastCommand>(Rays, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var results = new NativeArray<RaycastHit>(Rays, Allocator.TempJob);
            for (int i = 0; i < Rays; i++)
                commands[i] = new RaycastCommand(origin, twist * directions[i], QueryParameters.Default, radius);
            RaycastCommand.ScheduleBatch(commands, results, 512, 1).Complete();

            for (int i = 0; i < Rays; i++)
            {
                RaycastHit hit = results[i];
                Collider collider = hit.collider;
                if (collider == null) continue;
                hits++;

                surfaces.TryGetValue(collider, out var surface);
                Color color = ColorOf(surface, hit.distance / radius, hit.normal, hit.point);
                // każdy mebel ma swój odcień: książki na półce i skrzynie w stosie dają się odróżnić
                if (surface == Surface.Prop) color *= 0.6f + 0.65f * ((uint)(collider.GetHashCode() * 2654435761u) >> 8 & 0xff) / 255f;
                Add(hit.point, color, new Vector3(now, hit.distance, ScanLife));
            }
            commands.Dispose();
            results.Dispose();

            // napisy w zasięgu, do których nic nie zasłania drogi
            if (signs != null)
            {
                foreach (var sign in signs)
                {
                    if (Vector3.Distance(origin, sign.center) > radius || Physics.Linecast(origin, sign.center + sign.normal * 0.15f)) continue;
                    foreach (var point in sign.points) Add(point, sign.color, new Vector3(now, Vector3.Distance(origin, point), ScanLife));
                }
            }
            LastHits = hits;
            Upload();
        }

        // spirala Fibonacciego na sferze, zagęszczona przy poziomie: więcej punktów na ścianach,
        // mniej na podłodze i stropie; bez drgania układałaby się w widoczny wzór
        static Vector3[] Pattern()
        {
            var pattern = new Vector3[Rays];
            var random = new System.Random(7);
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < Rays; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / Rays;
                y = 0.45f * y + 0.55f * y * Mathf.Abs(y);
                float ring = Mathf.Sqrt(1f - y * y);
                float angle = golden * i;
                var jitter = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.02f;
                pattern[i] = (new Vector3(Mathf.Cos(angle) * ring, y, Mathf.Sin(angle) * ring) + jitter).normalized;
            }
            return pattern;
        }

        // Ślad kroku na podłodze: garść punktów w kształcie podeszwy i słabnący krąg wokół.
        // Tak widać dźwięk, który gracz słyszy, ale którego źródła jeszcze nie zeskanował.
        public void Footprint(Vector3 position, Vector3 forward, Color color, float life)
        {
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, forward), ground = new Vector3(position.x, 0.03f, position.z);
            float now = Time.time;
            for (int i = 0; i < 26; i++)
            {
                Vector2 spot = Random.insideUnitCircle;
                Add(ground + side * spot.x * 0.055f + forward * spot.y * 0.14f, color, new Vector3(now, 0f, life));
            }
            for (int i = 0; i < 30; i++)
            {
                float angle = 2f * Mathf.PI * (i + Random.value) / 30f;
                Add(ground + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.45f, color * 0.35f, new Vector3(now, 0.45f, life * 0.4f));
            }
            Upload();
        }

        // Krótko żyjące punkty wzdłuż linii: tor rzutu rysowany co klatkę i lecący kamień.
        public void Trail(List<Vector3> line, Color color, float life)
        {
            float now = Time.time;
            for (int i = 0; i < line.Count; i++)
                Add(line[i], color * (0.35f + 0.65f * i / Mathf.Max(1, line.Count - 1)), new Vector3(now, 0f, life));
            Upload();
        }

        // Uderzenie o podłogę albo ścianę: kręgi punktów rozchodzące się od miejsca trafienia.
        public void Burst(Vector3 center, Vector3 normal, Color color, float life)
        {
            Vector3 side = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.7f ? Vector3.right : Vector3.up).normalized, other = Vector3.Cross(normal, side);
            float now = Time.time;
            for (int ring = 1; ring <= 4; ring++)
            {
                float radius = 0.35f * ring;
                int count = 14 * ring;
                for (int i = 0; i < count; i++)
                {
                    float angle = 2f * Mathf.PI * (i + Random.value) / count;
                    Add(center + normal * 0.03f + (side * Mathf.Cos(angle) + other * Mathf.Sin(angle)) * radius, color / ring, new Vector3(now, radius * 6f, life));
                }
            }
            Upload();
        }

        void Add(Vector3 point, Color color, Vector3 scan)
        {
            int v = next * 4;
            for (int k = 0; k < 4; k++)
            {
                vertices[v + k].position = point;
                vertices[v + k].color = color;
                vertices[v + k].scan = scan;
            }
            next = (next + 1) % capacity;
            pending = Mathf.Min(pending + 1, capacity);
        }

        // do karty graficznej idą tylko punkty dodane od ostatniego razu, nie cała chmura
        void Upload()
        {
            int first = (next - pending + capacity) % capacity, head = Mathf.Min(pending, capacity - first);
            mesh.SetVertexBufferData(vertices, first * 4, first * 4, head * 4, 0, Quiet);
            if (pending > head) mesh.SetVertexBufferData(vertices, 0, 0, (pending - head) * 4, 0, Quiet);
            pending = 0;
        }

        static Color ColorOf(Surface surface, float far, Vector3 normal, Vector3 point)
        {
            float depth = Mathf.Sqrt(far);
            switch (surface)
            {
                case Surface.Guard: return new Color(2.4f, 0.55f, 0.12f);
                case Surface.Loot: return new Color(2.2f, 1.7f, 0.3f);
                case Surface.Exit: return new Color(0.4f, 2.2f, 0.5f);
                // meble jaśniejsze i bledsze od murów, posągi prawie białe, zieleń zielona
                case Surface.Prop:
                    return Color.Lerp(new Color(0.5f, 0.85f, 0.8f), new Color(0.65f, 0.5f, 1f), depth) * (0.9f + far) * (normal.y > 0.7f ? 0.6f : 1f);
                case Surface.Statue:
                    return Color.Lerp(new Color(0.95f, 1f, 1f), new Color(0.85f, 0.75f, 1f), depth) * (1.15f + far);
                case Surface.Plant:
                    return Color.Lerp(new Color(0.3f, 0.95f, 0.25f), new Color(0.25f, 0.55f, 0.9f), depth) * (0.75f + far);
            }

            // mury: blisko morska zieleń, daleko fiolet i jaśniej, bo dalekie punkty są mniejsze
            Color wall = Color.Lerp(new Color(0.05f, 0.9f, 0.7f), new Color(0.38f, 0.1f, 1f), depth) * (1f + far);
            switch (surface)
            {
                case Surface.Floor:
                {
                    // posadzka w szachownicę z płyt wielkości pola, ciemna, żeby nie zalewała obrazu
                    bool dark = ((Mathf.FloorToInt((point.x + 1f) / Level.Cell) + Mathf.FloorToInt((point.z + 1f) / Level.Cell)) & 1) == 0;
                    return wall * (dark ? 0.07f : 0.2f);
                }
                case Surface.Grass:
                    return Color.Lerp(new Color(0.2f, 0.9f, 0.3f), new Color(0.2f, 0.45f, 0.9f), depth) * (1f + far) * 0.22f;
                case Surface.Ceiling:
                {
                    if (normal.y > -0.3f) return wall * 0.6f;   // boki belek jaśniejsze od stropu
                    // kasetony: jasne żebra co pole, ciemne wypełnienie; dzięki nim widać krzywiznę sklepień
                    float x = Mathf.Abs(Mathf.Repeat(point.x + 1f, Level.Cell) - 1f), z = Mathf.Abs(Mathf.Repeat(point.z + 1f, Level.Cell) - 1f);
                    return wall * (Mathf.Max(x, z) > 0.9f ? 0.7f : 0.1f);
                }
            }
            if (Mathf.Abs(normal.y) > 0.7f) return wall * 0.22f;
            // ciemniejszy cokół do wysokości metra, zamknięty jasną listwą
            return wall * (point.y < 0.94f ? 0.55f : point.y < 1.06f ? 1.7f : 1f);
        }
    }
}
