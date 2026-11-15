using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Mrok.World
{
    // Bryły, z których składa się świat. Nic nie ma siatki do rysowania: każda bryła to zderzacz,
    // a jej kształt widać dopiero po punktach, które zostawi na niej skan.
    // Położenia są lokalne względem rodzica.
    public class Shapes
    {
        const int Sides = 18;

        static readonly Dictionary<string, Mesh> lathes = new Dictionary<string, Mesh>();

        readonly Transform parent;
        readonly Dictionary<Collider, Surface> surfaces;

        public Shapes(Transform parent, Dictionary<Collider, Surface> surfaces)
        {
            this.parent = parent;
            this.surfaces = surfaces;
        }

        T Add<T>(string name, Vector3 position, Quaternion rotation, Surface surface) where T : Collider
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            var collider = go.AddComponent<T>();
            surfaces[collider] = surface;
            return collider;
        }

        public BoxCollider Box(Vector3 center, Vector3 size, Surface surface, Quaternion? rotation = null)
        {
            var box = Add<BoxCollider>("Bryła", center, rotation ?? Quaternion.identity, surface);
            box.size = size;
            return box;
        }

        public SphereCollider Sphere(Vector3 center, float radius, Surface surface)
        {
            var sphere = Add<SphereCollider>("Kula", center, Quaternion.identity, surface);
            sphere.radius = radius;
            return sphere;
        }

        // Bryła obrotowa wokół pionu. Profil to pary (promień, wysokość) obwiedzione od dołu po
        // zewnętrznej stronie do góry; odcinek biegnący z powrotem w dół tworzy wnętrze misy,
        // a profil od wierzchołka w dół i na zewnątrz daje kopułę widzianą od środka.
        public MeshCollider Lathe(Vector3 foot, Surface surface, params float[] profile)
        {
            var lathe = Add<MeshCollider>("Toczona", foot, Quaternion.identity, surface);
            lathe.sharedMesh = LatheMesh(profile, Sides);
            return lathe;
        }

        public MeshCollider Lathe(Vector3 foot, Surface surface, int sides, float[] profile)
        {
            var lathe = Add<MeshCollider>("Toczona", foot, Quaternion.identity, surface);
            lathe.sharedMesh = LatheMesh(profile, sides);
            return lathe;
        }

        static Mesh LatheMesh(float[] profile, int sides)
        {
            var key = new StringBuilder().Append(sides);
            foreach (float value in profile) key.Append(';').Append(value.ToString("0.###"));
            if (lathes.TryGetValue(key.ToString(), out var cached) && cached != null) return cached;

            int rings = profile.Length / 2;
            var vertices = new Vector3[rings * sides];
            for (int i = 0; i < rings; i++)
            {
                for (int j = 0; j < sides; j++)
                {
                    float angle = 2f * Mathf.PI * j / sides;
                    vertices[i * sides + j] = new Vector3(profile[i * 2] * Mathf.Cos(angle), profile[i * 2 + 1], profile[i * 2] * Mathf.Sin(angle));
                }
            }

            var triangles = new List<int>();
            for (int i = 0; i + 1 < rings; i++)
            {
                for (int j = 0; j < sides; j++)
                {
                    int a = i * sides + j, b = a + sides;
                    int c = i * sides + (j + 1) % sides, d = c + sides;
                    // na osi (promień 0) czworokąt zapada się w trójkąt
                    if (profile[i * 2] > 1e-4f) { triangles.Add(a); triangles.Add(b); triangles.Add(c); }
                    if (profile[i * 2 + 2] > 1e-4f) { triangles.Add(c); triangles.Add(b); triangles.Add(d); }
                }
            }

            var mesh = new Mesh { name = "Bryła obrotowa" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            lathes[key.ToString()] = mesh;
            return mesh;
        }
    }
}
