using UnityEngine;

namespace Ogrod
{
    // Art bible. Styl: nocny ogród z wycinanki — płaskie, miękkie kształty, rośliny świecą
    // na granatowym tle. Nastrój: światło daje sam ogród; im więcej muzyki, tym jaśniej.
    // Każdy kolor w grze pochodzi stąd.
    public static class Palette
    {
        public static readonly Color SkyTop = Hex("101A33");
        public static readonly Color SkyBottom = Hex("2A3F6B");
        public static readonly Color HillFar = Hex("1F3350");
        public static readonly Color HillNear = Hex("172740");
        public static readonly Color Stem = Hex("4E8F6A");
        public static readonly Color Leaf = Hex("6FBF8A");
        public static readonly Color Dormant = Hex("5A6478");
        public static readonly Color Text = Hex("E8ECF5");

        public static readonly Color MoodNeutral = Hex("D9DEE8");
        public static readonly Color MoodCool = Hex("6C7BFF");
        public static readonly Color MoodWarm = Hex("FF9A4D");

        // kolory roślin, w kolejności głosów z music_bank.json
        public static readonly Color[] Plants = { Hex("F2C14E"), Hex("8FD3F4"), Hex("F28FB1"), Hex("B79CF2") };

        static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var color);
            return color;
        }
    }
}
