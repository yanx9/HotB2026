using UnityEngine;

namespace Ogrod
{
    // Art bible. Styl: nocny ogród w pixel arcie (320x180), ciemne sylwetki tła i rośliny,
    // które świecą własnym kolorem. Nastrój: światło daje sam ogród; wszystko, co świeci
    // i kołysze się, robi to w rytm muzyki. Każdy kolor w grze pochodzi stąd.
    public static class Palette
    {
        public static readonly Color SkyTop = Hex("0B1026");
        public static readonly Color SkyMid = Hex("1B2452");
        public static readonly Color SkyHorizon = Hex("4A3F7A");
        public static readonly Color Moon = Hex("F4F1D8");
        public static readonly Color Aurora = Hex("40E0B0");

        public static readonly Color HillFar = Hex("242C60");
        public static readonly Color HillFarEdge = Hex("343C78");
        public static readonly Color HillNear = Hex("171D42");
        public static readonly Color HillNearEdge = Hex("222A58");

        public static readonly Color TreeDark = Hex("12283A");
        public static readonly Color TreeMid = Hex("1A3F4E");
        public static readonly Color TreeLight = Hex("2A6066");
        public static readonly Color TreeTrunk = Hex("1C1F36");

        public static readonly Color Earth = Hex("141C30");
        public static readonly Color EarthDark = Hex("0E1424");
        public static readonly Color Grass = Hex("2F6B4F");
        public static readonly Color GrassLight = Hex("58B070");

        public static readonly Color Stem = Hex("3E8F5A");
        public static readonly Color StemDark = Hex("2A6044");
        public static readonly Color Leaf = Hex("7BD389");
        public static readonly Color Trunk = Hex("6B4A3A");
        public static readonly Color TrunkDark = Hex("4A3228");
        public static readonly Color WillowDark = Hex("2A6A5A");
        public static readonly Color WillowMid = Hex("3E9A78");
        public static readonly Color WillowLight = Hex("86DDA8");
        public static readonly Color Cream = Hex("E8DCC0");
        public static readonly Color CreamDark = Hex("B8A888");
        public static readonly Color Outline = Hex("080C1A");

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
