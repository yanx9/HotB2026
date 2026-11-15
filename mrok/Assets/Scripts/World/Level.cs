using System.Collections.Generic;
using UnityEngine;

namespace Mrok.World
{
    // Wall musi zostać pierwszy: zderzacz bez wpisu liczy się jak mur.
    public enum Surface { Wall, Floor, Grass, Ceiling, Prop, Statue, Plant, Guard, Loot, Exit }

    // Poziom zbudowany z mapy znakowej (Levels.cs). Nic tu nie ma wyglądu: powstają same zderzacze,
    // a gracz widzi tylko punkty, które zostawił na nich skan.
    public static class Level
    {
        public const float Cell = 2f;
        const float VaultRise = 1.7f, DomeRise = 2f;

        public static LevelInfo Current = Levels.All[Levels.Hub];

        static string[] Map => Current.map;
        static Room[] Rooms => Current.rooms;
        public static Vector2Int[][] Patrols => Current.patrols;
        public static float WallHeight => Current.wallHeight;

        public static int Width => Map[0].Length;
        public static int Depth => Map.Length;

        public static Vector3 PlayerStart, ExitPosition;
        public static readonly List<Vector3> LootPositions = new List<Vector3>();
        public static readonly Dictionary<int, Vector3> Portals = new Dictionary<int, Vector3>();   // numer poziomu -> pole z drzwiami
        public static readonly List<Sign> Signs = new List<Sign>();
        public static readonly List<Vector3> RockPositions = new List<Vector3>();

        static char At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Depth ? 'W' : Map[y][x];

        // strażnicy chodzą po środkach pól, więc pole z meblem albo postumentem omijają
        public static bool Walkable(int x, int y)
        {
            char c = At(x, y);
            return c == '.' || c == 'P' || c == 'E' || c == 'D' || c == 'r' || char.IsDigit(c);
        }

        // wiersz 0 mapy leży najdalej w osi z, żeby mapa w kodzie wyglądała jak widok z góry
        public static Vector3 Center(int x, int y) => new Vector3(x * Cell, 0f, (Depth - 1 - y) * Cell);
        public static Vector3 Center(Vector2Int cell) => Center(cell.x, cell.y);

        public static Vector2Int CellOf(Vector3 position) =>
            new Vector2Int(Mathf.RoundToInt(position.x / Cell), Depth - 1 - Mathf.RoundToInt(position.z / Cell));

        static Room RoomAt(int x, int y)
        {
            foreach (var room in Rooms)
                if (room.Contains(x, y)) return room;
            return new Room(x, y, x, y, Roof.Flat, 3f);   // pole poza salami
        }

        // powtarzalna "losowość" z położenia pola: ten sam poziom przy każdym uruchomieniu
        static int Hash(int x, int y) => ((x * 73856093) ^ (y * 19349663)) & 0x7fffffff;

        public static void Build(Transform root, Dictionary<Collider, Surface> surfaces)
        {
            LootPositions.Clear();
            Portals.Clear();
            Signs.Clear();
            RockPositions.Clear();
            foreach (var row in Map)
                if (row.Length != Width) Debug.LogError($"Wiersz mapy ma {row.Length} znaków zamiast {Width}: {row}");

            var holder = new GameObject("Poziom").transform;
            holder.SetParent(root, false);
            var shapes = new Shapes(holder, surfaces);

            Vector3 middle = (Center(0, 0) + Center(Width - 1, Depth - 1)) / 2f;
            shapes.Box(middle + Vector3.down * 0.1f, new Vector3(Width * Cell, 0.2f, Depth * Cell), Surface.Floor);

            for (int y = 0; y < Depth; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Vector3 center = Center(x, y);
                    char c = Map[y][x];
                    switch (c)
                    {
                        case 'W':
                        case 'H':
                            // jeden zderzacz na cały ciąg muru albo żywopłotu w wierszu
                            int end = x;
                            while (end + 1 < Width && Map[y][end + 1] == c) end++;
                            float tall = c == 'W' ? WallHeight : 2.3f;
                            shapes.Box((center + Center(end, y)) / 2f + Vector3.up * tall / 2f,
                                       new Vector3((end - x + 1) * Cell, tall, Cell), c == 'W' ? Surface.Wall : Surface.Plant);
                            x = end;
                            break;
                        case '1': case '2': case '3': Portals[c - '0'] = center; break;
                        case 'r': RockPositions.Add(center); break;
                        case 'Y': Pine(shapes, center, Hash(x, y)); break;
                        case 'R': Rock(shapes, center, Hash(x, y)); break;
                        case 'D': Arch(shapes, x, y); break;
                        case 'P': PlayerStart = center; break;
                        case 'L': LootPositions.Add(center); break;
                        case 'E': ExitPosition = center; break;
                        case 'o': Column(shapes, center, CeilingAt(x, y)); break;
                        case 's': Statue(holder, shapes, surfaces, x, y); break;
                        case 'b': Bench(shapes, center); break;
                        case 'v': Showcase(shapes, center); break;
                        case 'h': Bookshelf(shapes, center, Hash(x, y)); break;
                        case 't': Table(shapes, center); break;
                        case 'c': Crates(shapes, center, Hash(x, y)); break;
                        case 'f': Fountain(shapes, center); break;
                        case 'T': Tree(shapes, center, Hash(x, y)); break;
                        case 'p': Plant(shapes, center); break;
                    }
                }
            }

            foreach (var room in Rooms) Cover(shapes, room);

            foreach (var info in Current.signs)
            {
                Vector3 normal = info.side == 'N' ? Vector3.back : info.side == 'S' ? Vector3.forward : info.side == 'E' ? Vector3.left : Vector3.right;
                Vector3 at = Center(info.x, info.y) - normal * (Cell / 2f - 0.03f) + Vector3.up * info.height;
                Signs.Add(new Sign(info.text, at, normal, info.pixel));
            }
        }

        // ---------- stropy ----------

        // wysokość stropu nad środkiem pola; pod sklepieniem zależy od odległości od jego osi
        static float CeilingAt(int x, int y)
        {
            Room room = RoomAt(x, y);
            if (room.roof != Roof.Vault) return room.height;
            bool alongX = room.Width >= room.Depth;
            Vector3 offset = Center(x, y) - room.Middle;
            float across = alongX ? offset.z : offset.x, half = (alongX ? room.Depth : room.Width) / 2f;
            return room.height + VaultRise * Mathf.Sqrt(Mathf.Max(0f, 1f - across * across / (half * half)));
        }

        static void Cover(Shapes shapes, Room room)
        {
            Vector3 middle = room.Middle;
            bool alongX = room.Width >= room.Depth;
            switch (room.roof)
            {
                case Roof.Open:
                    shapes.Box(middle + Vector3.up * 0.005f, new Vector3(room.Width, 0.01f, room.Depth), Surface.Grass);
                    break;

                case Roof.Flat:
                case Roof.Beams:
                    shapes.Box(middle + Vector3.up * (room.height + 0.1f), new Vector3(room.Width, 0.2f, room.Depth), Surface.Ceiling);
                    if (room.roof == Roof.Flat) break;
                    // belki w poprzek sali, co dwa pola
                    float length = alongX ? room.Width : room.Depth, span = alongX ? room.Depth : room.Width;
                    for (float along = -length / 2f + Cell; along < length / 2f - 0.1f; along += 2f * Cell)
                    {
                        Vector3 offset = alongX ? new Vector3(along, 0f, 0f) : new Vector3(0f, 0f, along);
                        Vector3 size = alongX ? new Vector3(0.3f, 0.35f, span) : new Vector3(span, 0.35f, 0.3f);
                        shapes.Box(middle + offset + Vector3.up * (room.height - 0.175f), size, Surface.Ceiling);
                    }
                    if (room.height >= 3.4f && room.Width > 8f && room.Depth > 8f) Chandelier(shapes, middle, room.height);
                    break;

                case Roof.Vault:
                {
                    // kolebka wzdłuż dłuższego boku, z wąskich pasów stycznych do łuku
                    const int Strips = 16;
                    float half = (alongX ? room.Depth : room.Width) / 2f, run = alongX ? room.Width : room.Depth;
                    for (int i = 0; i < Strips; i++)
                    {
                        float a = -half + 2f * half * i / Strips, b = -half + 2f * half * (i + 1) / Strips;
                        float ya = VaultRise * Mathf.Sqrt(Mathf.Max(0f, 1f - a * a / (half * half)));
                        float yb = VaultRise * Mathf.Sqrt(Mathf.Max(0f, 1f - b * b / (half * half)));
                        float chord = Mathf.Sqrt((b - a) * (b - a) + (yb - ya) * (yb - ya)) + 0.06f;
                        float tilt = Mathf.Atan2(yb - ya, b - a) * Mathf.Rad2Deg;
                        Vector3 at = middle + Vector3.up * (room.height + (ya + yb) / 2f + 0.08f);
                        if (alongX)
                            shapes.Box(at + new Vector3(0f, 0f, (a + b) / 2f), new Vector3(run, 0.16f, chord), Surface.Ceiling, Quaternion.Euler(-tilt, 0f, 0f));
                        else
                            shapes.Box(at + new Vector3((a + b) / 2f, 0f, 0f), new Vector3(chord, 0.16f, run), Surface.Ceiling, Quaternion.Euler(0f, 0f, tilt));
                    }
                    break;
                }

                case Roof.Dome:
                {
                    float radius = Mathf.Min(room.Width, room.Depth) / 2f - 0.1f, rise = Mathf.Min(DomeRise, radius * 0.55f);
                    const int Steps = 9;
                    var profile = new float[(Steps + 1) * 2];
                    for (int i = 0; i <= Steps; i++)
                    {
                        float t = Mathf.PI / 2f * i / Steps;
                        profile[i * 2] = radius * Mathf.Sin(t);
                        profile[i * 2 + 1] = rise * Mathf.Cos(t);
                    }
                    shapes.Lathe(middle + Vector3.up * room.height, Surface.Ceiling, 28, profile);
                    // płaski strop w narożnikach, poza kręgiem kopuły
                    for (int y = room.y0; y <= room.y1; y++)
                    {
                        for (int x = room.x0; x <= room.x1; x++)
                        {
                            Vector3 offset = Center(x, y) - middle;
                            if (offset.magnitude + Cell * 0.71f < radius) continue;
                            shapes.Box(Center(x, y) + Vector3.up * (room.height + 0.1f), new Vector3(Cell, 0.2f, Cell), Surface.Ceiling);
                        }
                    }
                    break;
                }
            }

            if (room.paintings) Paintings(shapes, room);
        }

        // obrazy na dłuższych ścianach sali
        static void Paintings(Shapes shapes, Room room)
        {
            for (int x = room.x0; x <= room.x1; x++)
            {
                if ((x - room.x0) % 2 != 1) continue;
                for (int side = 0; side < 2; side++)
                {
                    int y = side == 0 ? room.y0 : room.y1, beyond = side == 0 ? y - 1 : y + 1;
                    if (At(x, beyond) != 'W' || At(x - 1, beyond) != 'W' || At(x + 1, beyond) != 'W') continue;
                    int hash = Hash(x, y);
                    float width = 1f + hash % 5 * 0.15f, height = 0.8f + hash / 5 % 4 * 0.2f;
                    float z = (side == 0 ? 1f : -1f) * (Cell / 2f - 0.04f);
                    shapes.Box(Center(x, y) + new Vector3(0f, 1.85f, z), new Vector3(width, height, 0.08f), Surface.Prop);
                }
            }
        }

        static void Chandelier(Shapes shapes, Vector3 middle, float ceiling)
        {
            shapes.Box(middle + Vector3.up * (ceiling - 0.3f), new Vector3(0.04f, 0.6f, 0.04f), Surface.Prop);
            shapes.Lathe(middle + Vector3.up * (ceiling - 1f), Surface.Prop,
                0f, 0f, 0.1f, 0.02f, 0.16f, 0.14f, 0.55f, 0.16f, 0.62f, 0.3f, 0.55f, 0.32f, 0.14f, 0.26f, 0.06f, 0.42f, 0f, 0.42f);
        }

        // ---------- przejścia i meble ----------

        // łuk nad przejściem: pionowe płyty, których spód układa się w półelipsę
        static void Arch(Shapes shapes, int x, int y)
        {
            bool acrossX = At(x - 1, y) == 'W' && At(x + 1, y) == 'W';   // mury po bokach w osi x
            const int Slabs = 10;
            float width = Cell / Slabs;
            for (int i = 0; i < Slabs; i++)
            {
                float u = -Cell / 2f + (i + 0.5f) * width;
                float under = 2.1f + 0.8f * Mathf.Sqrt(Mathf.Max(0f, 1f - u * u / (Cell * Cell / 4f)));
                Vector3 center = Center(x, y) + (acrossX ? new Vector3(u, 0f, 0f) : new Vector3(0f, 0f, u)) + Vector3.up * (under + WallHeight) / 2f;
                Vector3 size = acrossX ? new Vector3(width, WallHeight - under, Cell) : new Vector3(Cell, WallHeight - under, width);
                shapes.Box(center, size, Surface.Wall);
            }
        }

        static void Column(Shapes shapes, Vector3 center, float height)
        {
            shapes.Lathe(center, Surface.Wall,
                0.46f, 0f, 0.46f, 0.22f, 0.36f, 0.3f, 0.3f, 0.4f, 0.27f, height - 0.45f,
                0.36f, height - 0.3f, 0.46f, height - 0.22f, 0.46f, height);
        }

        static void Statue(Transform holder, Shapes shapes, Dictionary<Collider, Surface> surfaces, int x, int y)
        {
            Vector3 center = Center(x, y);
            shapes.Box(center + Vector3.up * 0.1f, new Vector3(1f, 0.2f, 1f), Surface.Prop);
            shapes.Box(center + Vector3.up * 0.5f, new Vector3(0.8f, 0.6f, 0.8f), Surface.Prop);
            // posągi patrzą na środek sali
            Vector3 toMiddle = RoomAt(x, y).Middle - center;
            toMiddle.y = 0f;
            var pivot = new GameObject("Posąg").transform;
            pivot.SetParent(holder, false);
            pivot.localPosition = center + Vector3.up * 0.8f;
            pivot.localRotation = Quaternion.LookRotation(toMiddle.sqrMagnitude > 0.01f ? toMiddle : Vector3.forward);
            pivot.localScale = Vector3.one * 1.15f;
            new Figure(pivot, surfaces, Surface.Statue, false).Statue(Hash(x, y) / 7);
        }

        static void Bench(Shapes shapes, Vector3 center)
        {
            shapes.Box(center + Vector3.up * 0.45f, new Vector3(1.7f, 0.08f, 0.5f), Surface.Prop);
            shapes.Box(center + new Vector3(-0.7f, 0.21f, 0f), new Vector3(0.08f, 0.42f, 0.45f), Surface.Prop);
            shapes.Box(center + new Vector3(0.7f, 0.21f, 0f), new Vector3(0.08f, 0.42f, 0.45f), Surface.Prop);
        }

        static void Showcase(Shapes shapes, Vector3 center)
        {
            shapes.Box(center + Vector3.up * 0.45f, new Vector3(0.8f, 0.9f, 0.8f), Surface.Prop);
            shapes.Box(center + Vector3.up * 0.93f, new Vector3(0.9f, 0.06f, 0.9f), Surface.Prop);
            // amfora
            shapes.Lathe(center + Vector3.up * 0.96f, Surface.Statue,
                0f, 0f, 0.1f, 0f, 0.08f, 0.05f, 0.22f, 0.28f, 0.24f, 0.42f, 0.16f, 0.6f, 0.07f, 0.68f, 0.07f, 0.78f, 0.12f, 0.82f, 0f, 0.82f);
        }

        // regał dwustronny wzdłuż osi x; sąsiednie pola łączą się w długi rząd
        static void Bookshelf(Shapes shapes, Vector3 center, int hash)
        {
            const float Tall = 2.3f, Deep = 0.6f;
            shapes.Box(center + new Vector3(-0.97f, Tall / 2f, 0f), new Vector3(0.06f, Tall, Deep), Surface.Prop);
            shapes.Box(center + new Vector3(0.97f, Tall / 2f, 0f), new Vector3(0.06f, Tall, Deep), Surface.Prop);
            shapes.Box(center + Vector3.up * (Tall - 0.03f), new Vector3(Cell, 0.06f, Deep), Surface.Prop);
            var random = new System.Random(hash);
            for (int level = 0; level < 4; level++)
            {
                float shelf = 0.06f + level * 0.55f;
                shapes.Box(center + Vector3.up * shelf, new Vector3(Cell, 0.05f, Deep), Surface.Prop);
                // książki: bloki o różnej szerokości i wysokości, czasem z przerwą
                float x = -0.92f;
                while (x < 0.7f)
                {
                    float width = 0.16f + (float)random.NextDouble() * 0.3f, height = 0.26f + (float)random.NextDouble() * 0.2f;
                    width = Mathf.Min(width, 0.92f - x);
                    if (random.Next(7) != 0)
                        shapes.Box(center + new Vector3(x + width / 2f, shelf + 0.025f + height / 2f, 0f), new Vector3(width - 0.02f, height, Deep - 0.1f), Surface.Prop);
                    x += width;
                }
            }
        }

        static void Table(Shapes shapes, Vector3 center)
        {
            shapes.Box(center + Vector3.up * 0.76f, new Vector3(1.5f, 0.06f, 0.9f), Surface.Prop);
            foreach (float x in new[] { -0.68f, 0.68f })
                foreach (float z in new[] { -0.38f, 0.38f })
                    shapes.Box(center + new Vector3(x, 0.365f, z), new Vector3(0.07f, 0.73f, 0.07f), Surface.Prop);
            // otwarta księga, globus i świecznik
            shapes.Box(center + new Vector3(-0.3f, 0.82f, 0.05f), new Vector3(0.42f, 0.05f, 0.3f), Surface.Prop, Quaternion.Euler(0f, 18f, 0f));
            shapes.Lathe(center + new Vector3(0.4f, 0.79f, -0.1f), Surface.Prop, 0f, 0f, 0.1f, 0f, 0.1f, 0.03f, 0.02f, 0.06f, 0.02f, 0.16f);
            shapes.Sphere(center + new Vector3(0.4f, 1.09f, -0.1f), 0.15f, Surface.Statue);
            shapes.Lathe(center + new Vector3(0.05f, 0.79f, 0.28f), Surface.Prop, 0f, 0f, 0.07f, 0f, 0.02f, 0.04f, 0.02f, 0.3f, 0f, 0.34f);
        }

        static void Crates(Shapes shapes, Vector3 center, int hash)
        {
            Quaternion turn = Quaternion.Euler(0f, hash % 40 - 20f, 0f);
            switch (hash / 40 % 3)
            {
                case 0:
                    shapes.Box(center + Vector3.up * 0.55f, new Vector3(1.2f, 1.1f, 1.2f), Surface.Prop, turn);
                    shapes.Box(center + new Vector3(0.15f, 1.4f, -0.1f), new Vector3(0.7f, 0.6f, 0.7f), Surface.Prop, Quaternion.Euler(0f, hash % 70, 0f));
                    break;
                case 1:
                    // dwie beczki i skrzynka
                    foreach (var offset in new[] { new Vector3(-0.38f, 0f, 0.25f), new Vector3(0.4f, 0f, -0.2f) })
                        shapes.Lathe(center + offset, Surface.Prop, 0f, 0f, 0.28f, 0f, 0.36f, 0.3f, 0.37f, 0.55f, 0.28f, 0.9f, 0f, 0.9f);
                    shapes.Box(center + new Vector3(0.35f, 0.25f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), Surface.Prop, turn);
                    break;
                default:
                    shapes.Box(center + new Vector3(-0.1f, 0.7f, 0f), new Vector3(1.4f, 1.4f, 1.1f), Surface.Prop, turn);
                    shapes.Box(center + new Vector3(0.55f, 0.3f, 0.65f), new Vector3(0.6f, 0.6f, 0.6f), Surface.Prop, Quaternion.Euler(0f, hash % 70, 0f));
                    break;
            }
        }

        static void Fountain(Shapes shapes, Vector3 center)
        {
            // basen z cembrowiną, trzon, górna misa i wylewka
            shapes.Lathe(center, Surface.Prop,
                1.45f, 0f, 1.45f, 0.45f, 1.3f, 0.5f, 1.25f, 0.25f, 0.3f, 0.25f, 0.2f, 0.4f, 0.16f, 0.95f,
                0.62f, 1.1f, 0.68f, 1.22f, 0.58f, 1.22f, 0.5f, 1.12f, 0.12f, 1.1f, 0.08f, 1.55f, 0f, 1.6f);
        }

        static void Tree(Shapes shapes, Vector3 center, int hash)
        {
            float lean = hash % 5 * 0.06f - 0.12f;
            shapes.Lathe(center, Surface.Prop, 0.3f, 0f, 0.2f, 0.4f, 0.17f, 1.4f, 0.13f, 2.5f, 0f, 2.5f);
            shapes.Sphere(center + new Vector3(lean, 3.2f, 0f), 1.05f, Surface.Plant);
            shapes.Sphere(center + new Vector3(0.65f, 2.8f, 0.35f), 0.7f, Surface.Plant);
            shapes.Sphere(center + new Vector3(-0.55f, 2.9f, -0.4f), 0.75f, Surface.Plant);
            shapes.Sphere(center + new Vector3(0.1f + lean, 3.95f, -0.2f), 0.65f, Surface.Plant);
        }

        // świerk: pień i trzy piętra gałęzi
        static void Pine(Shapes shapes, Vector3 center, int hash)
        {
            float tall = 1f + hash % 4 * 0.12f;
            shapes.Lathe(center, Surface.Prop, 0.2f, 0f, 0.14f, 1.2f, 0f, 1.2f);
            shapes.Lathe(center + Vector3.up * 0.9f, Surface.Plant, 0f, 0f, 1.25f, 0.05f, 0.45f, 1.3f * tall, 0f, 1.3f * tall);
            shapes.Lathe(center + Vector3.up * (0.9f + 1.1f * tall), Surface.Plant, 0f, 0f, 0.95f, 0.05f, 0.3f, 1.2f * tall, 0f, 1.2f * tall);
            shapes.Lathe(center + Vector3.up * (0.9f + 2.1f * tall), Surface.Plant, 0f, 0f, 0.65f, 0.05f, 0f, 1.5f * tall);
        }

        static void Rock(Shapes shapes, Vector3 center, int hash)
        {
            float size = 0.8f + hash % 5 * 0.1f;
            shapes.Lathe(center, Surface.Prop, 12, new[] { 0.75f * size, 0f, 0.9f * size, 0.3f * size, 0.7f * size, 0.75f * size, 0.3f * size, 1f * size, 0f, 1.05f * size });
            shapes.Lathe(center + new Vector3(0.75f * size, 0f, 0.35f), Surface.Prop, 12, new[] { 0.4f * size, 0f, 0.45f * size, 0.2f * size, 0.2f * size, 0.45f * size, 0f, 0.48f * size });
        }

        static void Plant(Shapes shapes, Vector3 center)
        {
            shapes.Lathe(center, Surface.Prop, 0f, 0f, 0.2f, 0f, 0.3f, 0.5f, 0.26f, 0.5f, 0f, 0.44f);
            shapes.Sphere(center + new Vector3(0f, 0.85f, 0f), 0.33f, Surface.Plant);
            shapes.Sphere(center + new Vector3(0.17f, 1.15f, 0.1f), 0.24f, Surface.Plant);
            shapes.Sphere(center + new Vector3(-0.15f, 1.3f, -0.08f), 0.2f, Surface.Plant);
        }

        // ---------- drogi strażników ----------

        // Najkrótsza droga po polach (wszerz, bez skosów). Pusta lista, gdy drogi nie ma.
        public static List<Vector3> Path(Vector3 from, Vector3 to)
        {
            Vector2Int start = Nearest(CellOf(from)), goal = Nearest(CellOf(to));
            var cameFrom = new Dictionary<Vector2Int, Vector2Int> { [start] = start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            Vector2Int[] steps = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
            while (queue.Count > 0 && !cameFrom.ContainsKey(goal))
            {
                var cell = queue.Dequeue();
                foreach (var step in steps)
                {
                    var next = cell + step;
                    if (!Walkable(next.x, next.y) || cameFrom.ContainsKey(next)) continue;
                    cameFrom[next] = cell;
                    queue.Enqueue(next);
                }
            }

            var path = new List<Vector3>();
            if (!cameFrom.ContainsKey(goal)) return path;
            for (var cell = goal; cell != start; cell = cameFrom[cell]) path.Add(Center(cell));
            path.Reverse();
            return path;
        }

        // pole, po którym da się chodzić, najbliższe wskazanemu (hałas mógł dojść zza ściany)
        static Vector2Int Nearest(Vector2Int cell)
        {
            if (Walkable(cell.x, cell.y)) return cell;
            for (int radius = 1; radius < 4; radius++)
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                        if (Walkable(cell.x + dx, cell.y + dy)) return new Vector2Int(cell.x + dx, cell.y + dy);
            return cell;
        }
    }
}
