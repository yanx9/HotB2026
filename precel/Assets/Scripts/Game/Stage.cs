using UnityEngine;

namespace Precel.Game
{
    // Scenografia namiotu piwnego budowana z kodu: stół, deska, kufle, girlanda żarówek,
    // ściana w bawarskie romby. Bez plików graficznych: tekstury też powstają tutaj.
    public static class Stage
    {
        public static readonly Color Blue = new Color(0.05f, 0.42f, 0.78f);
        public static readonly Color Cream = new Color(0.96f, 0.93f, 0.85f);

        static Shader shader;
        static Texture2D lozenges, wood;

        public static Material Mat(Color color, Texture texture = null, float gloss = 0.1f, float emission = 0f)
        {
            if (shader == null) shader = Resources.Load<Shader>("Shaders/PrecelLit");
            var material = new Material(shader) { color = color, mainTexture = texture };
            material.SetFloat("_Gloss", gloss);
            material.SetFloat("_Emission", emission);
            return material;
        }

        public static GameObject Shape(PrimitiveType type, Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        public static GameObject Box(Transform parent, string name, Vector3 position, Vector3 scale, Material material) =>
            Shape(PrimitiveType.Cube, parent, name, position, scale, material);

        // pręt między dwoma punktami (w układzie rodzica)
        public static GameObject Rod(Transform parent, string name, Vector3 from, Vector3 to, float thickness, Material material)
        {
            var go = Box(parent, name, (from + to) / 2f, new Vector3(thickness, thickness, Vector3.Distance(from, to)), material);
            go.transform.localRotation = Quaternion.LookRotation(to - from);
            return go;
        }

        public static Texture2D Lozenges()
        {
            if (lozenges != null) return lozenges;
            const int size = 256;
            lozenges = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // romb = kratka w układzie obróconym i ściśniętym
                    float u = x / (float)size * 4f, v = y / (float)size * 2f;
                    bool blue = (Mathf.FloorToInt(u + v) + Mathf.FloorToInt(u - v + 8f)) % 2 == 0;
                    pixels[y * size + x] = blue ? Blue : Cream;
                }
            }
            lozenges.SetPixels(pixels);
            lozenges.Apply();
            return lozenges;
        }

        public static Texture2D Wood()
        {
            if (wood != null) return wood;
            const int size = 256;
            wood = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    float warp = Mathf.PerlinNoise(u * 3f, v * 14f) * 2.5f;
                    float grain = 0.5f + 0.5f * Mathf.Sin((v * 22f + warp) * Mathf.PI);
                    float fine = Mathf.PerlinNoise(u * 60f, v * 180f);
                    float shade = 0.78f + 0.16f * grain + 0.08f * fine;
                    pixels[y * size + x] = new Color(shade, shade, shade);
                }
            }
            wood.SetPixels(pixels);
            wood.Apply();
            return wood;
        }

        public static void Build(Transform root)
        {
            var darkWood = Mat(new Color(0.42f, 0.25f, 0.13f), Wood(), 0.2f);
            var lightWood = Mat(new Color(0.86f, 0.66f, 0.40f), Wood(), 0.15f);
            var cloth = Mat(Color.white, Lozenges(), 0.02f);
            cloth.mainTextureScale = new Vector2(3f, 3f);
            var wall = Mat(new Color(0.55f, 0.58f, 0.66f), Lozenges(), 0f);
            wall.mainTextureScale = new Vector2(10f, 3f);

            var table = Box(root, "Stół", new Vector3(0f, -0.19f, 1.4f), new Vector3(13f, 0.2f, 6.4f), darkWood);
            table.GetComponent<MeshRenderer>().sharedMaterial.mainTextureScale = new Vector2(2f, 6f);
            Box(root, "Bieżnik", new Vector3(0f, -0.085f, 0.1f), new Vector3(4.4f, 0.012f, 2.7f), cloth);
            var board = Box(root, "Deska", new Vector3(0f, -0.04f, 0f), new Vector3(2.8f, 0.08f, 1.95f), lightWood);
            board.GetComponent<MeshRenderer>().sharedMaterial.mainTextureScale = new Vector2(1f, 2f);

            Box(root, "Ściana namiotu", new Vector3(0f, 2.4f, 5.2f), new Vector3(26f, 6f, 0.1f), wall);
            Box(root, "Belka", new Vector3(0f, 0.75f, 5.1f), new Vector3(26f, 0.25f, 0.2f), darkWood);

            // girlanda żarówek zwisająca przed ścianą
            var bulb = Mat(new Color(1f, 0.82f, 0.45f), null, 0f, 2.2f);
            var cable = Mat(new Color(0.08f, 0.08f, 0.08f));
            Vector3 previous = Vector3.zero;
            const int bulbs = 23;
            for (int i = 0; i < bulbs; i++)
            {
                float t = i / (float)(bulbs - 1);
                float sag = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3f));
                var p = new Vector3(Mathf.Lerp(-7.5f, 7.5f, t), 2.3f - 0.3f * sag, 4.7f);
                Shape(PrimitiveType.Sphere, root, "Żarówka", p, Vector3.one * 0.13f, bulb);
                if (i > 0) Rod(root, "Kabel", previous + Vector3.up * 0.06f, p + Vector3.up * 0.06f, 0.015f, cable);
                previous = p;
            }

            Stein(root, new Vector3(2.75f, -0.09f, 1.7f), 1f);
            Stein(root, new Vector3(-2.9f, -0.09f, 2.3f), 0.9f);
            Stein(root, new Vector3(-3.6f, -0.09f, 1.5f), 0.9f);
        }

        // kamionkowy kufel z pianą
        static void Stein(Transform root, Vector3 position, float size)
        {
            var holder = new GameObject("Kufel").transform;
            holder.SetParent(root, false);
            holder.localPosition = position;
            holder.localScale = Vector3.one * size;
            holder.localRotation = Quaternion.Euler(0f, 25f, 0f);

            var stone = Mat(new Color(0.72f, 0.73f, 0.72f), null, 0.5f);
            var band = Mat(Blue, null, 0.5f);
            var foam = Mat(new Color(1f, 0.98f, 0.92f), null, 0.05f);
            Shape(PrimitiveType.Cylinder, holder, "Korpus", new Vector3(0f, 0.42f, 0f), new Vector3(0.52f, 0.42f, 0.52f), stone);
            Shape(PrimitiveType.Cylinder, holder, "Pasek", new Vector3(0f, 0.16f, 0f), new Vector3(0.54f, 0.04f, 0.54f), band);
            Shape(PrimitiveType.Cylinder, holder, "Pasek", new Vector3(0f, 0.68f, 0f), new Vector3(0.54f, 0.04f, 0.54f), band);
            Shape(PrimitiveType.Sphere, holder, "Piana", new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.22f, 0.5f), foam);
            Shape(PrimitiveType.Sphere, holder, "Piana", new Vector3(0.12f, 0.9f, 0.05f), new Vector3(0.26f, 0.2f, 0.26f), foam);
            Rod(holder, "Ucho", new Vector3(0.26f, 0.66f, 0f), new Vector3(0.44f, 0.6f, 0f), 0.06f, stone);
            Rod(holder, "Ucho", new Vector3(0.44f, 0.62f, 0f), new Vector3(0.44f, 0.24f, 0f), 0.06f, stone);
            Rod(holder, "Ucho", new Vector3(0.44f, 0.26f, 0f), new Vector3(0.26f, 0.2f, 0f), 0.06f, stone);
        }
    }
}
