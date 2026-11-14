using UnityEngine;

namespace Ogrod
{
    // Wszystkie sprite'y powstają z kodu (białe kształty barwione przez SpriteRenderer.color),
    // każdy ma rozmiar 1×1 jednostki.
    public static class SpriteFactory
    {
        static Sprite square, disc, glow, gradient;

        public static Sprite Square => square ??= Build(4, (x, y) => 1f);
        public static Sprite Disc => disc ??= Build(128, (x, y) => Mathf.Clamp01((1f - Radius(x, y)) * 40f));
        public static Sprite Glow => glow ??= Build(128, (x, y) =>
        {
            float a = Mathf.Clamp01(1f - Radius(x, y));
            return a * a;
        });
        // przezroczysty u góry, pełny u dołu
        public static Sprite Gradient => gradient ??= Build(64, (x, y) => 1f - y);

        static float Radius(float x, float y)
        {
            float dx = x - 0.5f, dy = y - 0.5f;
            return Mathf.Sqrt(dx * dx + dy * dy) * 2f;
        }

        static Sprite Build(int size, System.Func<float, float, float> alpha)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha((x + 0.5f) / size, (y + 0.5f) / size));
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        public static SpriteRenderer Make(string name, Sprite sprite, Transform parent, Vector3 position,
            Vector2 scale, Color color, int order, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = color;
            renderer.sortingOrder = order;
            renderer.sharedMaterial = material;
            return renderer;
        }
    }
}
