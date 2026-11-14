using System.Collections.Generic;
using UnityEngine;

namespace Precel.Pretzel
{
    // Precel jako rura o zmiennej grubości poprowadzona po krzywej: gruby brzuch, cienkie
    // ramiona skrzyżowane pośrodku, końce oparte na brzuchu. Każdy precel jest trochę inny
    // (kształt, grubość, obrót), więc połowa masy nie leży w połowie szerokości.
    public static class PretzelBuilder
    {
        const int SamplesPerSegment = 10;
        const int RingSegments = 14;

        // x, z: położenie na desce; r: promień ciasta; lift: uniesienie nad deskę
        static readonly float[,] Shape =
        {
            { -0.42f, -0.36f, 0.047f, 0.19f },   // koniec ramienia, leży na brzuchu
            { -0.20f, -0.02f, 0.050f, 0.07f },
            {  0.00f,  0.20f, 0.055f, 0.115f },  // skrzyżowanie, to ramię idzie górą
            {  0.24f,  0.44f, 0.062f, 0.02f },
            {  0.56f,  0.52f, 0.072f, 0.00f },
            {  0.86f,  0.26f, 0.084f, 0.00f },
            {  0.82f, -0.20f, 0.098f, 0.00f },
            {  0.46f, -0.52f, 0.112f, 0.00f },
            {  0.00f, -0.62f, 0.120f, 0.00f },   // środek brzucha
        };

        public class Result
        {
            public Mesh mesh;
            public List<Vector3> salt = new List<Vector3>();
            public List<Vector3> saltNormals = new List<Vector3>();
        }

        public static Result Build(System.Random random)
        {
            float Rand(float spread) => ((float)random.NextDouble() * 2f - 1f) * spread;

            // punkty kontrolne: połowa z tabeli, druga połowa to jej odbicie, każdy lekko przesunięty
            int half = Shape.GetLength(0);
            int count = half * 2 - 1;
            var points = new Vector3[count];
            var radii = new float[count];
            var lifts = new float[count];
            float lean = Rand(0.16f);   // jedna strona grubsza od drugiej
            for (int i = 0; i < count; i++)
            {
                int k = i < half ? i : count - 1 - i;
                float side = i < half ? 1f : -1f;
                float x = Shape[k, 0] * (i < half ? 1f : -1f);
                points[i] = new Vector3(x + Rand(0.025f), 0f, Shape[k, 1] + Rand(0.025f));
                radii[i] = Shape[k, 2] * (1f + Rand(0.1f) + lean * Mathf.Sign(x) * Mathf.Min(1f, Mathf.Abs(x) * 2f));
                lifts[i] = Shape[k, 3];
                if (k == 2 && side < 0f) lifts[i] = 0f;   // drugie ramię idzie dołem
            }

            // próbki wzdłuż krzywej Catmulla-Roma
            var centers = new List<Vector3>();
            var sizes = new List<float>();
            for (int i = 0; i < count - 1; i++)
            {
                int a = Mathf.Max(i - 1, 0), d = Mathf.Min(i + 2, count - 1);
                int steps = i == count - 2 ? SamplesPerSegment + 1 : SamplesPerSegment;
                for (int s = 0; s < steps; s++)
                {
                    float t = s / (float)SamplesPerSegment;
                    Vector3 p = CatmullRom(points[a], points[i], points[i + 1], points[d], t);
                    float r = Mathf.Max(0.03f, CatmullRom(radii[a], radii[i], radii[i + 1], radii[d], t));
                    float lift = Mathf.Max(0f, CatmullRom(lifts[a], lifts[i], lifts[i + 1], lifts[d], t));
                    p.y = r + lift;
                    centers.Add(p);
                    sizes.Add(r);
                }
            }

            int rings = centers.Count;
            float[] taper = { 0.45f, 0.78f, 0.94f };   // zaokrąglone końce
            for (int i = 0; i < taper.Length; i++)
            {
                sizes[i] *= taper[i];
                sizes[rings - 1 - i] *= taper[i];
            }

            float turn = (float)random.NextDouble() * 360f;
            var rotation = Quaternion.Euler(0f, turn, 0f);

            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            var result = new Result();

            Color crustDark = new Color(0.33f, 0.14f, 0.05f).linear;
            Color crustLight = new Color(0.70f, 0.38f, 0.12f).linear;
            Color pale = new Color(0.85f, 0.62f, 0.32f).linear;
            Color split = new Color(0.95f, 0.80f, 0.55f).linear;

            for (int i = 0; i < rings; i++)
            {
                Vector3 tangent = (centers[Mathf.Min(i + 1, rings - 1)] - centers[Mathf.Max(i - 1, 0)]).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, tangent).normalized;
                Vector3 up = Vector3.Cross(tangent, side);
                float along = i / (float)(rings - 1);
                float belly = Mathf.Clamp01(1f - Mathf.Abs(along - 0.5f) / 0.13f);   // pęknięcie na brzuchu

                for (int j = 0; j < RingSegments; j++)
                {
                    float angle = j / (float)RingSegments * Mathf.PI * 2f;
                    Vector3 offset = Mathf.Cos(angle) * side + Mathf.Sin(angle) * up;
                    vertices.Add(rotation * (centers[i] + offset * sizes[i]));

                    float top = offset.y;
                    float noise = Mathf.PerlinNoise(along * 40f + turn, j * 0.9f);
                    Color c = Color.Lerp(crustLight, crustDark, Mathf.Clamp01(0.25f + 0.5f * top + 0.5f * (noise - 0.5f)));
                    if (top < -0.3f) c = Color.Lerp(c, pale, Mathf.Clamp01((-top - 0.3f) * 2f));
                    if (belly > 0f && top > 0.86f) c = Color.Lerp(c, split, belly);
                    colors.Add(c);

                    // sól: grube kryształy na wierzchu, gęściej na brzuchu
                    bool body = i > 6 && i < rings - 7;
                    if (body && top > 0.55f && random.NextDouble() < 0.035 + 0.05 * belly)
                    {
                        result.salt.Add(rotation * (centers[i] + offset * (sizes[i] + 0.004f)));
                        result.saltNormals.Add(rotation * offset);
                    }
                }
            }

            for (int i = 0; i < rings - 1; i++)
            {
                for (int j = 0; j < RingSegments; j++)
                {
                    int a = i * RingSegments + j, b = i * RingSegments + (j + 1) % RingSegments;
                    int c = a + RingSegments, d = b + RingSegments;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(b); triangles.Add(d); triangles.Add(c);
                }
            }

            // denka na końcach: bez nich siatka nie jest zamknięta i nie da się policzyć objętości
            for (int end = 0; end < 2; end++)
            {
                int ring = end == 0 ? 0 : rings - 1;
                Vector3 outward = (centers[ring] - centers[end == 0 ? 1 : rings - 2]).normalized;
                int center = vertices.Count;
                vertices.Add(rotation * (centers[ring] + outward * sizes[ring] * 0.5f));
                colors.Add(crustLight);
                for (int j = 0; j < RingSegments; j++)
                {
                    int a = ring * RingSegments + j, b = ring * RingSegments + (j + 1) % RingSegments;
                    if (end == 0) { triangles.Add(center); triangles.Add(b); triangles.Add(a); }
                    else { triangles.Add(center); triangles.Add(a); triangles.Add(b); }
                }
            }

            // środek ramki ograniczającej na x = 0, z = 0: środek masy wypada gdzie indziej
            var min = new Vector3(float.MaxValue, 0f, float.MaxValue);
            var max = new Vector3(float.MinValue, 0f, float.MinValue);
            foreach (var v in vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            var shift = new Vector3((min.x + max.x) / 2f, 0f, (min.z + max.z) / 2f);
            for (int i = 0; i < vertices.Count; i++) vertices[i] -= shift;
            for (int i = 0; i < result.salt.Count; i++) result.salt[i] -= shift;

            var mesh = new Mesh { name = "Precel" };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            if (MeshSlicer.Volume(mesh) < 0f)   // ściany mają patrzeć na zewnątrz
            {
                for (int i = 0; i < triangles.Count; i += 3) (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
                mesh.SetTriangles(triangles, 0);
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            result.mesh = mesh;
            return result;
        }

        static Vector3 CatmullRom(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * b + (c - a) * t + (2f * a - 5f * b + 4f * c - d) * t2 + (3f * b - a - 3f * c + d) * t3);
        }

        static float CatmullRom(float a, float b, float c, float d, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * b + (c - a) * t + (2f * a - 5f * b + 4f * c - d) * t2 + (3f * b - a - 3f * c + d) * t3);
        }
    }
}
