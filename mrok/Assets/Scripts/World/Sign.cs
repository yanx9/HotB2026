using System.Collections.Generic;
using UnityEngine;

namespace Mrok.World
{
    // Napis złożony z punktów. Nie ma zderzacza: skan, który go obejmie i ma do niego wolną
    // drogę, dopisuje jego punkty do chmury, więc litery są ostre niezależnie od gęstości promieni.
    public class Sign
    {
        public readonly List<Vector3> points = new List<Vector3>();
        public Vector3 center, normal;
        public Color color = new Color(1.7f, 1.55f, 1.2f);

        // czcionka 5 x 7: wiersze od góry, 1 to zapalony piksel
        static readonly Dictionary<char, string> Font = new Dictionary<char, string>
        {
            ['A'] = "01110100011000111111100011000110001", ['B'] = "11110100011000111110100011000111110",
            ['C'] = "01110100011000010000100001000101110", ['D'] = "11110100011000110001100011000111110",
            ['E'] = "11111100001000011110100001000011111", ['F'] = "11111100001000011110100001000010000",
            ['G'] = "01110100011000010111100011000101111", ['H'] = "10001100011000111111100011000110001",
            ['I'] = "01110001000010000100001000010001110", ['J'] = "00111000100001000010000101001001100",
            ['K'] = "10001100101010011000101001001010001", ['L'] = "10000100001000010000100001000011111",
            ['M'] = "10001110111010110101100011000110001", ['N'] = "10001110011010110011100011000110001",
            ['O'] = "01110100011000110001100011000101110", ['P'] = "11110100011000111110100001000010000",
            ['Q'] = "01110100011000110001101011001001101", ['R'] = "11110100011000111110101001001010001",
            ['S'] = "01111100001000001110000010000111110", ['T'] = "11111001000010000100001000010000100",
            ['U'] = "10001100011000110001100011000101110", ['V'] = "10001100011000110001100010101000100",
            ['W'] = "10001100011000110101101011101110001", ['X'] = "10001100010101000100010101000110001",
            ['Y'] = "10001100010101000100001000010000100", ['Z'] = "11111000010001000100010001000011111",
            ['='] = "00000000001111100000111110000000000", ['-'] = "00000000000000011111000000000000000",
            ['.'] = "00000000000000000000000000110001100", ['!'] = "00100001000010000100001000000000100",
        };

        // Kamień do podniesienia: garść punktów na powierzchni kulki. Tak mały przedmiot zwykły
        // skan trafiłby jednym punktem albo wcale.
        public static Sign Pebble(Vector3 center, float radius)
        {
            var sign = new Sign { center = center, normal = Vector3.up, color = new Color(0.75f, 1.5f, 1.9f) };
            var random = new System.Random(center.GetHashCode());
            for (int i = 0; i < 90; i++)
            {
                var direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() * 0.5f, (float)random.NextDouble() - 0.5f).normalized;
                sign.points.Add(center + Vector3.Scale(direction, new Vector3(radius, radius * 0.7f, radius)));
            }
            return sign;
        }

        Sign() { }

        // center: środek napisu; normal: w którą stronę patrzy ściana; pixel: bok piksela w metrach
        public Sign(string text, Vector3 center, Vector3 normal, float pixel)
        {
            this.center = center;
            this.normal = normal;
            Vector3 right = Vector3.Cross(Vector3.up, -normal).normalized;   // prawa strona patrzącego na ścianę
            string[] lines = text.Split('\n');
            float lineHeight = 9f * pixel;
            var random = new System.Random(text.GetHashCode());
            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l];
                float top = (lines.Length / 2f - l) * lineHeight - pixel, left = -line.Length * 6f * pixel / 2f;
                for (int c = 0; c < line.Length; c++)
                {
                    if (!Font.TryGetValue(line[c], out string glyph)) continue;
                    for (int i = 0; i < 35; i++)
                    {
                        if (glyph[i] != '1') continue;
                        // cztery punkty na piksel, lekko rozrzucone
                        for (int k = 0; k < 4; k++)
                        {
                            float x = left + (c * 6 + i % 5 + 0.25f + 0.5f * (k % 2) + ((float)random.NextDouble() - 0.5f) * 0.3f) * pixel;
                            float y = top - (i / 5 + 0.25f + 0.5f * (k / 2) + ((float)random.NextDouble() - 0.5f) * 0.3f) * pixel;
                            points.Add(center + right * x + Vector3.up * y);
                        }
                    }
                }
            }
        }
    }
}
