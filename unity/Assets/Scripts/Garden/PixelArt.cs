using System;
using UnityEngine;

namespace Ogrod
{
    // Małe płótno do rysowania pixel artu z kodu.
    public class PixelCanvas
    {
        public readonly int W, H;
        readonly Color32[] pixels;

        public PixelCanvas(int width, int height)
        {
            W = width;
            H = height;
            pixels = new Color32[width * height];
        }

        public void Set(int x, int y, Color32 color)
        {
            if (x >= 0 && x < W && y >= 0 && y < H) pixels[y * W + x] = color;
        }

        public bool Filled(int x, int y)
        {
            return x >= 0 && x < W && y >= 0 && y < H && pixels[y * W + x].a > 0;
        }

        public void Rect(int x, int y, int width, int height, Color32 color)
        {
            for (int j = y; j < y + height; j++)
                for (int i = x; i < x + width; i++)
                    Set(i, j, color);
        }

        public void Disc(float cx, float cy, float rx, float ry, Color32 color)
        {
            if (rx <= 0f || ry <= 0f) return;
            for (int y = Mathf.FloorToInt(cy - ry); y <= Mathf.CeilToInt(cy + ry); y++)
                for (int x = Mathf.FloorToInt(cx - rx); x <= Mathf.CeilToInt(cx + rx); x++)
                {
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    if (dx * dx + dy * dy <= 1f) Set(x, y, color);
                }
        }

        public void Line(int x0, int y0, int x1, int y1, Color32 color)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                Set(x0, y0, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * error;
                if (e2 >= dy) { error += dy; x0 += sx; }
                if (e2 <= dx) { error += dx; y0 += sy; }
            }
        }

        // ciemny kontur wokół wszystkiego, co narysowane
        public void Outline(Color32 color)
        {
            var filled = new bool[W * H];
            for (int i = 0; i < filled.Length; i++) filled[i] = pixels[i].a > 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (filled[y * W + x]) continue;
                    bool edge = (x > 0 && filled[y * W + x - 1]) || (x < W - 1 && filled[y * W + x + 1])
                        || (y > 0 && filled[(y - 1) * W + x]) || (y < H - 1 && filled[(y + 1) * W + x]);
                    if (edge) pixels[y * W + x] = color;
                }
        }

        public Sprite ToSprite(Vector2 pivot, float pixelsPerUnit)
        {
            var texture = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply();
            // FullRect: szader kołysania przesuwa wiersze w obrębie całego prostokąta
            return Sprite.Create(texture, new Rect(0, 0, W, H), pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
        }
    }

    // Cała grafika gry: rośliny w czterech stadiach wzrostu, drzewa tła, wzgórza, ziemia, trawa.
    public static class PixelArt
    {
        public const int PPU = 18;              // 180 px wysokości ekranu = 10 jednostek
        public const int ScreenW = 320, ScreenH = 180;
        public const int SpeciesCount = 4;
        static readonly Vector2 BottomCenter = new Vector2(0.5f, 0f);

        public struct PlantArt
        {
            public Sprite[] levels;       // 0 = uśpiona, 1–3 = kolejne stadia
            public Vector2[] glowCenters; // środek poświaty względem podstawy, w jednostkach
            public float sway;            // kołysanie w pikselach
        }

        static Color32 Solid(Color c, float brightness = 1f)
        {
            return new Color32(Channel(c.r * brightness), Channel(c.g * brightness), Channel(c.b * brightness), 255);
        }

        // alfa 128 oznacza w szaderze PixelSprite piksel świecący
        static Color32 Glow(Color c, float brightness = 1f)
        {
            return new Color32(Channel(c.r * brightness), Channel(c.g * brightness), Channel(c.b * brightness), 128);
        }

        static byte Channel(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        }

        // ---------- rośliny ----------

        public static PlantArt Plant(int species, Color color)
        {
            var art = new PlantArt { levels = new Sprite[4], glowCenters = new Vector2[4] };
            for (int level = 0; level < 4; level++)
            {
                PixelCanvas canvas;
                Vector2Int glow;
                switch (species % SpeciesCount)
                {
                    case 0: canvas = new PixelCanvas(44, 58); glow = Bellflower(canvas, level, color); art.sway = 2f; break;
                    case 1: canvas = new PixelCanvas(46, 66); glow = Reeds(canvas, level, color); art.sway = 2.5f; break;
                    case 2: canvas = new PixelCanvas(60, 74); glow = Willow(canvas, level, color); art.sway = 3f; break;
                    default: canvas = new PixelCanvas(52, 44); glow = Mushrooms(canvas, level, color); art.sway = 1f; break;
                }
                canvas.Outline(Solid(Palette.Outline));
                art.levels[level] = canvas.ToSprite(BottomCenter, PPU);
                art.glowCenters[level] = new Vector2((glow.x - canvas.W / 2f) / PPU, glow.y / (float)PPU);
            }
            return art;
        }

        // Dzwonki: łukowata łodyga z wiszącymi kielichami.
        static Vector2Int Bellflower(PixelCanvas c, int level, Color color)
        {
            const int cx = 18;
            int height = new[] { 9, 24, 36, 48 }[level];
            int bend = new[] { 1, 5, 8, 11 }[level];
            int bells = new[] { 0, 1, 3, 5 }[level];
            Vector2Int Point(float t) => new Vector2Int(cx + Mathf.RoundToInt(bend * t * t), Mathf.RoundToInt(height * t));

            for (int y = 0; y <= height; y++)
            {
                var p = Point(y / (float)height);
                c.Set(p.x, y, Solid(Palette.Stem));
                if (level >= 2 && y < height / 3) c.Set(p.x - 1, y, Solid(Palette.StemDark));
            }
            int leaf = level == 0 ? 3 : 6;
            c.Line(cx - 1, 1, cx - leaf, 1 + leaf * 2 / 3, Solid(Palette.Leaf));
            c.Line(cx - 1, 0, cx - leaf, leaf * 2 / 3, Solid(Palette.Stem));
            c.Line(cx + 1, 1, cx + leaf, 1 + leaf * 2 / 3, Solid(Palette.Leaf));
            c.Line(cx + 1, 0, cx + leaf, leaf * 2 / 3, Solid(Palette.Stem));
            if (level >= 2)
            {
                var m = Point(0.45f);
                c.Line(m.x, m.y, m.x - 5, m.y + 3, Solid(Palette.Leaf));
                c.Line(m.x, m.y - 1, m.x - 4, m.y + 1, Solid(Palette.Stem));
            }

            for (int i = 0; i < bells; i++)
            {
                var s = Point(1f - i * 0.15f);
                int side = i % 2 == 0 ? 1 : -1;
                int bx = s.x + side * 3, by = s.y - 2;
                c.Line(s.x, s.y, bx, by + 1, Solid(Palette.Stem));
                c.Rect(bx - 1, by, 3, 1, Glow(color));
                c.Rect(bx - 2, by - 2, 5, 2, Glow(color));
                c.Set(bx - 2, by - 3, Glow(color));
                c.Set(bx, by - 3, Glow(color, 0.8f));
                c.Set(bx + 2, by - 3, Glow(color, 0.8f));
                c.Set(bx - 1, by, Glow(color, 1.35f));
                c.Set(bx - 2, by - 1, Glow(color, 1.35f));
                c.Set(bx + 2, by - 2, Glow(color, 0.75f));
                c.Set(bx, by - 4, Glow(Palette.Moon));
            }

            var tip = Point(1f);
            if (level == 0) c.Rect(tip.x, tip.y + 1, 1, 2, Solid(color, 0.6f));
            return new Vector2Int(tip.x + 1, tip.y - 2);
        }

        // Trzciny: kępa pałek o świecących kolbach.
        static Vector2Int Reeds(PixelCanvas c, int level, Color color)
        {
            const int cx = 23;
            int count = new[] { 1, 2, 4, 5 }[level];
            int maxHeight = new[] { 10, 28, 42, 56 }[level];
            int[] offset = { 0, -5, 5, -9, 9 };
            float[] heightFactor = { 1f, 0.8f, 0.9f, 0.65f, 0.72f };
            int[] lean = { 1, -3, 4, -5, 5 };
            var glow = new Vector2Int(cx, maxHeight);

            for (int i = count - 1; i >= 0; i--)
            {
                int height = Mathf.RoundToInt(maxHeight * heightFactor[i]);
                int X(int y) => cx + offset[i] + Mathf.RoundToInt(lean[i] * (y / (float)height) * (y / (float)height));

                // długi liść odchodzący od podstawy
                int sign = lean[i] >= 0 ? 1 : -1;
                int bladeHeight = height / 2;
                for (int y = 0; y <= bladeHeight; y++)
                {
                    float t = y / (float)Mathf.Max(1, bladeHeight);
                    c.Set(cx + offset[i] + sign * Mathf.RoundToInt(7f * t * t), y, Solid(y > bladeHeight - 3 ? Palette.Leaf : Palette.StemDark));
                }

                for (int y = 0; y <= height; y++) c.Set(X(y), y, Solid(Palette.Stem));
                if (level >= 1)
                {
                    for (int y = height - 10; y <= height - 4; y++)
                    {
                        c.Set(X(y) - 1, y, Glow(color, 1.3f));
                        c.Set(X(y), y, Glow(color));
                        c.Set(X(y) + 1, y, Glow(color, 0.75f));
                    }
                    if (i == 0) glow = new Vector2Int(X(height - 7), height - 7);
                }
            }
            if (level == 0)
            {
                c.Set(cx + 2, 5, Solid(Palette.Leaf));
                c.Set(cx + 3, 6, Solid(Palette.Leaf));
            }
            return glow;
        }

        // Wierzba: drzewko z koroną i zwisającymi witkami ("strunami") z kwiatami.
        static Vector2Int Willow(PixelCanvas c, int level, Color color)
        {
            const int cx = 30;
            if (level == 0)
            {
                for (int y = 0; y < 8; y++) c.Set(cx, y, Solid(Palette.Stem));
                c.Line(cx, 4, cx - 3, 6, Solid(Palette.Leaf));
                c.Line(cx, 6, cx + 3, 8, Solid(Palette.Leaf));
                return new Vector2Int(cx, 8);
            }

            int trunkHeight = new[] { 8, 18, 28, 36 }[level];
            float r = new[] { 0f, 8f, 12f, 15f }[level];
            int vines = new[] { 0, 4, 7, 10 }[level];

            for (int y = 0; y < trunkHeight; y++)
            {
                int width = Mathf.RoundToInt(Mathf.Lerp(level >= 2 ? 5f : 3f, 2f, y / (float)trunkHeight));
                int x0 = cx - width / 2;
                c.Rect(x0, y, width, 1, Solid(Palette.Trunk));
                c.Set(x0 + width - 1, y, Solid(Palette.TrunkDark));
            }
            c.Set(cx - 3, 0, Solid(Palette.TrunkDark));
            c.Set(cx + 3, 0, Solid(Palette.TrunkDark));

            float cy = trunkHeight + r * 0.55f;
            c.Disc(cx, cy, r, r * 0.75f, Solid(Palette.WillowDark));
            c.Disc(cx - r * 0.45f, cy + r * 0.2f, r * 0.6f, r * 0.5f, Solid(Palette.WillowMid));
            c.Disc(cx + r * 0.4f, cy + r * 0.3f, r * 0.55f, r * 0.45f, Solid(Palette.WillowMid));
            c.Disc(cx - r * 0.35f, cy + r * 0.45f, r * 0.35f, r * 0.25f, Solid(Palette.WillowLight));

            for (int i = 0; i < vines; i++)
            {
                int vx = Mathf.RoundToInt(cx - r + (i + 0.5f) * (2f * r / vines));
                float u = (vx - cx) / r;
                int top = Mathf.RoundToInt(cy - 0.75f * r * Mathf.Sqrt(Mathf.Max(0f, 1f - u * u)));
                int length = Mathf.Min(5 + (i * 5) % 7 + level, top - 4);
                for (int k = 0; k < length; k++)
                    c.Set(vx, top - k, Solid(k % 2 == 0 ? Palette.WillowLight : Palette.WillowMid));
                c.Set(vx, top - length, Glow(color));
                if (i % 2 == 0) c.Set(vx, top - length / 2, Glow(color, 1.2f));

                // kwiaty w koronie
                c.Set(Mathf.RoundToInt(cx + Mathf.Cos(i * 2.4f) * r * 0.6f), Mathf.RoundToInt(cy + Mathf.Sin(i * 2.4f) * r * 0.45f), Glow(color, 1.15f));
            }
            return new Vector2Int(cx, Mathf.RoundToInt(cy));
        }

        // Grzyby: przysadzista kępa ze świecącymi kapeluszami.
        static Vector2Int Mushrooms(PixelCanvas c, int level, Color color)
        {
            const int cx = 26;
            // (przesunięcie, wysokość trzonu, szerokość trzonu, promienie kapelusza), od tyłu do przodu
            int[][] specs;
            switch (level)
            {
                case 0: specs = new[] { new[] { 0, 2, 2, 3, 2 } }; break;
                case 1: specs = new[] { new[] { 0, 6, 3, 6, 4 } }; break;
                case 2: specs = new[] { new[] { -5, 10, 4, 9, 5 }, new[] { 8, 5, 3, 5, 3 } }; break;
                default: specs = new[] { new[] { -7, 15, 5, 12, 7 }, new[] { 9, 9, 4, 8, 5 }, new[] { 1, 4, 2, 4, 3 } }; break;
            }

            foreach (var s in specs)
            {
                int mx = cx + s[0], stemHeight = s[1], stemWidth = s[2], rx = s[3], ry = s[4];
                int x0 = mx - stemWidth / 2;
                c.Rect(x0, 0, stemWidth, stemHeight, Solid(Palette.Cream));
                c.Rect(x0 + stemWidth - 1, 0, 1, stemHeight, Solid(Palette.CreamDark));

                Func<float, Color32> paint = b => level == 0 ? Solid(color, 0.55f * b) : Glow(color, b);
                for (int y = 0; y <= ry; y++)
                    for (int x = -rx; x <= rx; x++)
                        if (x * x / (float)(rx * rx) + y * y / (float)(ry * ry) <= 1f)
                            c.Set(mx + x, stemHeight + y, paint(y == 0 ? 0.6f : 1f));
                c.Disc(mx - rx * 0.35f, stemHeight + ry * 0.6f, rx * 0.28f, Mathf.Max(1f, ry * 0.22f), paint(1.35f));
                if (rx >= 5)
                {
                    c.Set(mx + Mathf.RoundToInt(rx * 0.3f), stemHeight + Mathf.RoundToInt(ry * 0.45f), paint(1.6f));
                    c.Set(mx - Mathf.RoundToInt(rx * 0.05f), stemHeight + Mathf.RoundToInt(ry * 0.78f), paint(1.6f));
                    c.Set(mx + Mathf.RoundToInt(rx * 0.62f), stemHeight + Mathf.RoundToInt(ry * 0.2f), paint(1.6f));
                }
            }
            var main = specs[0];
            return new Vector2Int(cx + main[0], main[1] + main[4] / 2);
        }

        // ---------- sceneria ----------

        public static Sprite BackgroundTree(int seed, bool conifer)
        {
            if (conifer)
            {
                var c = new PixelCanvas(34, 62);
                const int cx = 17;
                c.Rect(cx - 1, 0, 2, 6, Solid(Palette.TreeTrunk));
                int tiers = 4 + seed % 2;
                for (int k = 0; k < tiers; k++)
                {
                    int baseY = 5 + k * 9, half = 11 - k * 2, tierHeight = 13;
                    for (int y = 0; y < tierHeight; y++)
                    {
                        int w = Mathf.RoundToInt(half * (1f - y / (float)tierHeight));
                        c.Rect(cx - w, baseY + y, 2 * w + 1, 1, Solid(k % 2 == 0 ? Palette.TreeDark : Palette.TreeMid));
                        c.Set(cx - w, baseY + y, Solid(Palette.TreeLight));
                    }
                }
                return c.ToSprite(BottomCenter, PPU);
            }
            else
            {
                var c = new PixelCanvas(46, 62);
                const int cx = 23;
                int trunk = 14 + seed % 8;
                float r = 11 + seed % 4;
                float cy = trunk + r * 0.6f;
                c.Rect(cx - 1, 0, 3, trunk + 2, Solid(Palette.TreeTrunk));
                c.Disc(cx, cy, r, r * 0.85f, Solid(Palette.TreeDark));
                c.Disc(cx - r * 0.4f, cy + r * 0.2f, r * 0.6f, r * 0.55f, Solid(Palette.TreeMid));
                c.Disc(cx + r * 0.45f, cy - r * 0.1f, r * 0.5f, r * 0.45f, Solid(Palette.TreeMid));
                c.Disc(cx - r * 0.3f, cy + r * 0.45f, r * 0.35f, r * 0.28f, Solid(Palette.TreeLight));
                return c.ToSprite(BottomCenter, PPU);
            }
        }

        public static int HillHeight(int seed, int x, int baseHeight, int amplitude)
        {
            float h = baseHeight + amplitude * Mathf.Sin(x * 0.021f + seed * 1.7f) + amplitude * 0.5f * Mathf.Sin(x * 0.047f + seed * 3.1f);
            return Mathf.Max(1, Mathf.RoundToInt(h));
        }

        public static Sprite Hills(int seed, Color fill, Color edge, int baseHeight, int amplitude)
        {
            var c = new PixelCanvas(ScreenW, baseHeight + amplitude * 2 + 2);
            for (int x = 0; x < ScreenW; x++)
            {
                int h = HillHeight(seed, x, baseHeight, amplitude);
                c.Rect(x, 0, 1, h, Solid(fill));
                c.Set(x, h - 1, Solid(edge));
            }
            return c.ToSprite(BottomCenter, PPU);
        }

        public static Sprite Ground()
        {
            var c = new PixelCanvas(ScreenW, 40);
            var rng = new System.Random(11);
            c.Rect(0, 0, ScreenW, 40, Solid(Palette.Earth));
            for (int i = 0; i < 260; i++)
                c.Set(rng.Next(ScreenW), rng.Next(36), Solid(Palette.EarthDark));
            c.Rect(0, 37, ScreenW, 3, Solid(Palette.Grass));
            for (int x = 0; x < ScreenW; x++)
            {
                if (rng.Next(3) == 0) c.Set(x, 39, Solid(Palette.GrassLight));
                if (rng.Next(4) == 0) c.Set(x, 36, Solid(Palette.Grass));
            }
            return c.ToSprite(new Vector2(0.5f, 1f), PPU);
        }

        // pas źdźbeł; szader kołysze nim falą biegnącą wzdłuż ekranu
        public static Sprite Grass()
        {
            var c = new PixelCanvas(ScreenW, 9);
            var rng = new System.Random(5);
            for (int x = 0; x < ScreenW; x++)
            {
                if (rng.Next(5) < 2) continue;
                int h = 2 + rng.Next(6);
                bool light = rng.Next(3) == 0;
                for (int y = 0; y < h; y++)
                    c.Set(x, y, Solid(light && y >= h - 2 ? Palette.GrassLight : Palette.Grass));
            }
            return c.ToSprite(BottomCenter, PPU);
        }

        // miękka poświata; szader rastruje ją do twardych pikseli
        public static Sprite Halo()
        {
            const int size = 32;
            var c = new PixelCanvas(size, size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size - 0.5f, dy = (y + 0.5f) / size - 0.5f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy) * 2f);
                    c.Set(x, y, new Color32(255, 255, 255, (byte)(a * a * 255f)));
                }
            return c.ToSprite(new Vector2(0.5f, 0.5f), PPU);
        }

        // biały kwadrat 1x1 jednostki: niebo, nakładka nastroju, cząsteczki
        public static Sprite Unit()
        {
            var c = new PixelCanvas(1, 1);
            c.Set(0, 0, new Color32(255, 255, 255, 255));
            return c.ToSprite(new Vector2(0.5f, 0.5f), 1f);
        }
    }
}
