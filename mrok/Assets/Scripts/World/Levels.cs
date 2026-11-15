using UnityEngine;

namespace Mrok.World
{
    public enum Roof { Flat, Beams, Vault, Dome, Open }

    public struct Room
    {
        public int x0, y0, x1, y1;
        public Roof roof;
        public float height;      // strop płaski albo wysokość, od której zaczyna się sklepienie
        public bool paintings;

        public Room(int x0, int y0, int x1, int y1, Roof roof, float height, bool paintings = false)
        {
            this.x0 = x0; this.y0 = y0; this.x1 = x1; this.y1 = y1;
            this.roof = roof; this.height = height; this.paintings = paintings;
        }

        public bool Contains(int x, int y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;
        public Vector3 Middle => (Level.Center(x0, y0) + Level.Center(x1, y1)) / 2f;
        public float Width => (x1 - x0 + 1) * Level.Cell;    // wzdłuż osi x
        public float Depth => (y1 - y0 + 1) * Level.Cell;    // wzdłuż osi z
    }

    // Napis z punktów na ścianie pola (x, y): side to strona świata, po której jest ściana
    // (N to wiersz mapy wyżej). Widać go dopiero, gdy obejmie go skan.
    public struct SignInfo
    {
        public string text;
        public int x, y;
        public char side;
        public float height, pixel;

        public SignInfo(string text, int x, int y, char side, float height, float pixel = 0.05f)
        {
            this.text = text; this.x = x; this.y = y; this.side = side; this.height = height; this.pixel = pixel;
        }
    }

    // Posterunek: strażnik stoi na polu (x, y) zwrócony w stronę yaw i schodzi z niego tylko
    // za głośnym hałasem (rzucony kamień) albo w pogoni; potem wraca.
    public struct Post
    {
        public int x, y;
        public float yaw;

        public Post(int x, int y, float yaw) { this.x = x; this.y = y; this.yaw = yaw; }
    }

    public class LevelInfo
    {
        public string title;
        public string[] intro = { };          // tekst ekranu ładowania; w samej grze napisów nie ma
        public string[] map;
        public Room[] rooms = { };            // pierwsza pasująca sala wygrywa
        public Vector2Int[][] patrols = { };  // trasy strażników: kolejne pola (kolumna, wiersz mapy)
        public Post[] posts = { };
        public SignInfo[] signs = { };
        public float wallHeight = 6.5f;
        public float startYaw = 90f;
        public bool hub;                      // sala wyboru poziomu: bez strażników, sama się skanuje
    }

    // Znaki mapy: W mur, D przejście pod łukiem, P gracz, L łup, E wyjście, 1-3 pole przed drzwiami do poziomu (w murze od północy),
    // o kolumna, s posąg, b ławka, v gablota, h regał, t stół, c skrzynie, f fontanna,
    // T drzewo, Y świerk, H żywopłot, R głaz, p roślina w donicy, r kamień do podniesienia
    public static class Levels
    {
        public const int Hub = 0;

        public static readonly LevelInfo[] All =
        {
            new LevelInfo
            {
                title = "",
                intro = new[] { "Walk through a door." },
                hub = true,
                startYaw = 0f,
                wallHeight = 5.5f,
                map = new[]
                {
                    "WWWWWWWWWWW",
                    "W.1..2..3.W",
                    "W.........W",
                    "W.........W",
                    "W.........W",
                    "W....P....W",
                    "W.........W",
                    "WWWWWWWWWWW",
                },
                rooms = new[] { new Room(1, 1, 9, 6, Roof.Vault, 3.6f) },
                signs = new[]
                {
                    new SignInfo("TUTORIAL", 2, 1, 'N', 3.1f),
                    new SignInfo("MUSEUM", 5, 1, 'N', 3.1f),
                    new SignInfo("GARDEN", 8, 1, 'N', 3.1f),
                },
            },

            new LevelInfo
            {
                title = "TUTORIAL",
                intro = new[] { "It is completely dark.", "Close your eyes. Count to three. Open them." },
                map = new[]
                {
                    "WWWWWWWWWWWWWWWWWWW",
                    "W...W.....W.......W",
                    "W.P.D..L..D.......W",
                    "W...W.....W..c.c..W",
                    "WWWWWWWWWWW.......W",
                    "WWWWWWWWWWW..c.c..W",
                    "WWWWWWWWWWW.......W",
                    "WWWWWWWWWWW.......W",
                    "WWWWWWWWWWWWWWWDWWW",
                    "W...W.......D.r...W",
                    "W...W.......W...r.W",
                    "W...D.WWWWWWW.....W",
                    "W...W.......W.r...W",
                    "W.E.W.......D.....W",
                    "WWWWWWWWWWWWWWWWWWW",
                },
                rooms = new[]
                {
                    new Room(1, 1, 3, 3, Roof.Flat, 4f),
                    new Room(5, 1, 9, 3, Roof.Beams, 4f),
                    new Room(11, 1, 17, 7, Roof.Beams, 4.2f),
                    new Room(13, 9, 17, 13, Roof.Beams, 4f),     // sala z kamieniami
                    // Dwie nawy rozdzielone murem, połączone tylko przy wartowniku. Kamień rzucony z północnych
                    // drzwi w północną nawę odciąga go tam, a mur zasłania gracza idącego południową.
                    new Room(5, 9, 11, 13, Roof.Beams, 4.2f),
                    new Room(1, 9, 3, 13, Roof.Flat, 4f),        // wyjście
                },
                patrols = new[] { new[] { new Vector2Int(11, 4), new Vector2Int(17, 4) } },
                // wartownik stoi w jedynym przejściu do wyjścia i zejdzie z niego tylko za rzuconym kamieniem
                posts = new[] { new Post(4, 11, 90f) },
                signs = new[]
                {
                    new SignInfo("EYES SHUT\nLONGER = FARTHER", 3, 2, 'E', 3.4f),
                    new SignInfo("TAKE THE GOLD", 9, 2, 'E', 3.4f),
                    new SignInfo("THEY HEAR YOU\nSHIFT = SNEAK", 14, 1, 'N', 3.3f),
                    new SignInfo("PICK UP THE STONES\nHOLD CLICK TO THROW", 15, 13, 'S', 3.2f),
                    new SignInfo("LURE HIM AWAY\nTHEN SLIP PAST", 13, 11, 'W', 3.4f),
                    new SignInfo("GREEN = EXIT", 1, 11, 'W', 3.3f),
                },
            },

            new LevelInfo
            {
                title = "MUSEUM",
                intro = new[] { "Three pieces. Four guards. One green door.", "They cannot see you. They hear your steps and your scans." },
                map = new[]
                {
                    "WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW",
                    "W......W...............W........W",
                    "W....r.W..o..o..o..o...W..s..s..W",
                    "W.P....D...............D........W",
                    "W......W...b...v...b...W...L....W",
                    "W..b...W...............D........W",
                    "W......W..o..o..o..o...W..s..s..W",
                    "WWWDWWWW...............W........W",
                    "W......WWWWDWWWWWWDWWWWWWWWDWWWWW",
                    "W...............r...............W",
                    "W..p........b.........p.........W",
                    "WWWWDWWWWDWWWWDWWWDWWWWWDWWWWDWWW",
                    "W..........W.........W..........W",
                    "W.hhh..hhh.W.T..r..T.W.cc..c....W",
                    "W..........W.........W.....c..c.W",
                    "W.hhh..hhh.D....f....W.c......c.W",
                    "W..........W.........D.c..cc....W",
                    "W.hhh..hhh.W.T.....T.W......WWWWW",
                    "W....t.....W...b.b...W.cc...D..LW",
                    "W.L........W.........W...E..W...W",
                    "WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW",
                },
                rooms = new[]
                {
                    new Room(1, 1, 6, 6, Roof.Beams, 3.6f),                 // hol
                    new Room(8, 1, 22, 7, Roof.Vault, 3.8f, true),          // galeria
                    new Room(24, 1, 31, 7, Roof.Dome, 4f),                  // rotunda
                    new Room(1, 8, 31, 10, Roof.Beams, 3f),                 // korytarz
                    new Room(1, 12, 10, 19, Roof.Beams, 3.4f),              // biblioteka
                    new Room(12, 12, 20, 19, Roof.Open, 0f),                // dziedziniec
                    new Room(29, 18, 31, 19, Roof.Flat, 2.6f),              // skarbiec, przed magazynem, w którym leży
                    new Room(22, 12, 31, 19, Roof.Beams, 4.4f),             // magazyn
                },
                patrols = new[]
                {
                    new[] { new Vector2Int(1, 9), new Vector2Int(31, 9), new Vector2Int(31, 10), new Vector2Int(1, 10) },
                    new[] { new Vector2Int(9, 5), new Vector2Int(21, 5), new Vector2Int(30, 5), new Vector2Int(30, 3), new Vector2Int(21, 3), new Vector2Int(9, 3) },
                    new[] { new Vector2Int(5, 12), new Vector2Int(4, 18), new Vector2Int(10, 16), new Vector2Int(14, 19), new Vector2Int(19, 19), new Vector2Int(19, 12), new Vector2Int(13, 12) },
                    new[] { new Vector2Int(22, 12), new Vector2Int(31, 12), new Vector2Int(31, 16), new Vector2Int(26, 17), new Vector2Int(22, 19), new Vector2Int(27, 19) },
                },
            },

            new LevelInfo
            {
                title = "GARDEN",
                intro = new[] { "Open sky. Hedges, trees, a pavilion.", "Nobody can see you here. Everybody can hear you." },
                wallHeight = 3.2f,
                map = new[]
                {
                    "WWWWWWWWWWWWWWWWWWWWWWWWWWW",
                    "W..T.....H.......H....Y..LW",
                    "W.P...r..H..HHH..H.......YW",
                    "W....R...H..H.......HHHH..W",
                    "W..Y........H..T..........W",
                    "WHHHH..HHHHHH.....HHH..T..W",
                    "W......H.........o.o...H..W",
                    "W..T...H..s..rb........H..W",
                    "W......H.........o.o...H..W",
                    "W.HH.HHH.....T.........HHHW",
                    "W..........HHHHH..R.......W",
                    "W.Y..R.....H.L.H.....T....W",
                    "W......T...H...H..HH...Y..W",
                    "W..b.......HH.HH...H......W",
                    "W....Y...........R.H.r.f..W",
                    "WL....T.....Y......H....E.W",
                    "WWWWWWWWWWWWWWWWWWWWWWWWWWW",
                },
                rooms = new[]
                {
                    new Room(17, 6, 19, 8, Roof.Dome, 3f),                  // altana
                    new Room(1, 1, 25, 15, Roof.Open, 0f),
                },
                patrols = new[]
                {
                    // pierwszy punkt trasy to miejsce startu strażnika: daleko od gracza
                    new[] { new Vector2Int(24, 4), new Vector2Int(16, 7), new Vector2Int(8, 6), new Vector2Int(1, 4), new Vector2Int(11, 4), new Vector2Int(16, 2) },
                    new[] { new Vector2Int(10, 14), new Vector2Int(1, 13), new Vector2Int(1, 6), new Vector2Int(6, 8), new Vector2Int(4, 10) },
                    new[] { new Vector2Int(13, 13), new Vector2Int(13, 14), new Vector2Int(18, 13), new Vector2Int(17, 9), new Vector2Int(10, 9), new Vector2Int(9, 12) },
                    new[] { new Vector2Int(25, 6), new Vector2Int(25, 13), new Vector2Int(20, 13), new Vector2Int(22, 10), new Vector2Int(24, 10) },
                },
            },
        };
    }
}
