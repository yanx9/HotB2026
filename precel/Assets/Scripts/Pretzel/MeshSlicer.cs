using System.Collections.Generic;
using UnityEngine;

namespace Precel.Pretzel
{
    // Tnie zamkniętą siatkę płaszczyzną na dwie zamknięte siatki (przekroje dostają denka)
    // i liczy objętość siatki. Masa = objętość razy gęstość, więc nie zależy od tego,
    // jak gęsto siatka jest podzielona na trójkąty.
    public static class MeshSlicer
    {
        class Part
        {
            public readonly List<Vector3> vertices = new List<Vector3>();
            public readonly List<Color> colors = new List<Color>();
            public readonly List<int> triangles = new List<int>();
            readonly Dictionary<long, int> map = new Dictionary<long, int>();

            // key: indeks wierzchołka źródłowego albo klucz przeciętej krawędzi
            public int Index(long key, Vector3 position, Color color)
            {
                if (map.TryGetValue(key, out int index)) return index;
                index = vertices.Count;
                vertices.Add(position);
                colors.Add(color);
                map[key] = index;
                return index;
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name };
                if (triangles.Count == 0) return mesh;
                mesh.SetVertices(vertices);
                mesh.SetColors(colors);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        // Objętość zamkniętej siatki: suma czworościanów ze znakiem, rozpiętych między
        // początkiem układu a każdym trójkątem (twierdzenie o dywergencji).
        public static float Volume(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            double sum = 0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                sum += Vector3.Dot(a, Vector3.Cross(b, c));
            }
            return (float)(sum / 6.0);
        }

        // below: część po stronie przeciwnej do normalnej, above: po stronie normalnej
        public static void Slice(Mesh source, Vector3 planePoint, Vector3 planeNormal, Color capColor, out Mesh below, out Mesh above)
        {
            var vertices = source.vertices;
            var colors = source.colors;
            var triangles = source.triangles;
            bool hasColors = colors.Length == vertices.Length;
            planeNormal.Normalize();

            var distance = new float[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                float d = Vector3.Dot(vertices[i] - planePoint, planeNormal);
                distance[i] = Mathf.Abs(d) < 1e-6f ? 1e-6f : d;   // wierzchołek dokładnie na płaszczyźnie liczymy do góry
            }

            var parts = new[] { new Part(), new Part() };   // 0 = below, 1 = above
            var cutPoints = new Dictionary<long, Vector3>();
            var links = new Dictionary<long, List<long>>();

            Color ColorOf(int i) => hasColors ? colors[i] : Color.white;
            long EdgeKey(int a, int b) => a < b ? ((long)a << 32 | (uint)b) + (1L << 62) : ((long)b << 32 | (uint)a) + (1L << 62);

            int Corner(Part part, int i) => part.Index(i, vertices[i], ColorOf(i));

            int Cut(Part part, int a, int b)
            {
                if (a > b) (a, b) = (b, a);   // zawsze w tę samą stronę, żeby obie części dostały ten sam punkt
                float t = distance[a] / (distance[a] - distance[b]);
                Vector3 p = Vector3.Lerp(vertices[a], vertices[b], t);
                long key = EdgeKey(a, b);
                cutPoints[key] = p;
                return part.Index(key, p, Color.Lerp(ColorOf(a), ColorOf(b), t));
            }

            void Link(long a, long b)
            {
                if (!links.TryGetValue(a, out var list)) links[a] = list = new List<long>(2);
                list.Add(b);
            }

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i], i1 = triangles[i + 1], i2 = triangles[i + 2];
                bool s0 = distance[i0] > 0f, s1 = distance[i1] > 0f, s2 = distance[i2] > 0f;
                if (s0 == s1 && s1 == s2)
                {
                    var whole = parts[s0 ? 1 : 0];
                    whole.triangles.Add(Corner(whole, i0));
                    whole.triangles.Add(Corner(whole, i1));
                    whole.triangles.Add(Corner(whole, i2));
                    continue;
                }

                // a: wierzchołek sam po swojej stronie; b, c: pozostałe, w kolejności obiegu
                int a, b, c;
                if (s1 == s2) { a = i0; b = i1; c = i2; }
                else if (s0 == s2) { a = i1; b = i2; c = i0; }
                else { a = i2; b = i0; c = i1; }

                var alone = parts[distance[a] > 0f ? 1 : 0];
                var pair = parts[distance[a] > 0f ? 0 : 1];

                alone.triangles.Add(Corner(alone, a));
                alone.triangles.Add(Cut(alone, a, b));
                alone.triangles.Add(Cut(alone, a, c));

                int ab = Cut(pair, a, b), ac = Cut(pair, a, c), pb = Corner(pair, b), pc = Corner(pair, c);
                pair.triangles.Add(ab); pair.triangles.Add(pb); pair.triangles.Add(pc);
                pair.triangles.Add(ab); pair.triangles.Add(pc); pair.triangles.Add(ac);

                long keyB = EdgeKey(a, b), keyC = EdgeKey(a, c);
                Link(keyB, keyC);
                Link(keyC, keyB);
            }

            // odcinki przecięcia łączymy w zamknięte pętle; każda pętla to jedno denko
            var visited = new HashSet<long>();
            foreach (var start in links.Keys)
            {
                if (visited.Contains(start)) continue;
                var loop = new List<Vector3>();
                long current = start, previous = -1;
                while (visited.Add(current))
                {
                    loop.Add(cutPoints[current]);
                    var next = links[current];
                    long step = next[0] != previous || next.Count < 2 ? next[0] : next[1];
                    previous = current;
                    current = step;
                }
                if (loop.Count < 3) continue;
                AddCap(parts[0], loop, planeNormal, capColor);
                AddCap(parts[1], loop, -planeNormal, capColor);
            }

            below = parts[0].ToMesh(source.name + " L");
            above = parts[1].ToMesh(source.name + " P");
        }

        // Wachlarz trójkątów ze środka pętli, zwrócony w stronę outward.
        static void AddCap(Part part, List<Vector3> loop, Vector3 outward, Color color)
        {
            Vector3 center = Vector3.zero, normal = Vector3.zero;
            foreach (var p in loop) center += p;
            center /= loop.Count;
            for (int i = 0; i < loop.Count; i++)
                normal += Vector3.Cross(loop[i] - center, loop[(i + 1) % loop.Count] - center);
            bool flip = Vector3.Dot(normal, outward) < 0f;

            int first = part.vertices.Count;
            part.vertices.Add(center);
            part.colors.Add(color);
            foreach (var p in loop)
            {
                part.vertices.Add(p);
                part.colors.Add(color);
            }
            for (int i = 0; i < loop.Count; i++)
            {
                int a = first + 1 + i, b = first + 1 + (i + 1) % loop.Count;
                part.triangles.Add(first);
                part.triangles.Add(flip ? b : a);
                part.triangles.Add(flip ? a : b);
            }
        }
    }
}
