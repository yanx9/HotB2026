using System.Collections.Generic;
using UnityEngine;

namespace Mrok.World
{
    public enum Surface { Wall, Guard, Loot, Exit }

    // Poziom z mapy znakowej. Nic tu nie ma wyglądu: powstają same zderzacze, a gracz widzi
    // tylko punkty, które zostawił na nich skan.
    public static class Level
    {
        public const float Cell = 2f;
        public const float Height = 3f;

        // W ściana, c skrzynia, P gracz, L łup, E wyjście
        static readonly string[] Map =
        {
            "WWWWWWWWWWWWWWWWWWWWWWW",
            "W.....W.......W.......W",
            "W.P...W..c....W...L...W",
            "W.....W.......W.......W",
            "W..c..........W..c....W",
            "W.....W.......W.......W",
            "WWW.WWWWWW.WWWWWWW.WWWW",
            "W.....................W",
            "W..c.......c..........W",
            "WWW.WWWWWW.WWWWWWW.WWWW",
            "W.....W.......W.......W",
            "W..L..W...c...........W",
            "W.....W.......W...c...W",
            "W.....W..L....W.....E.W",
            "WWWWWWWWWWWWWWWWWWWWWWW",
        };

        // trasy patroli: kolejne pola (kolumna, wiersz mapy)
        public static readonly Vector2Int[][] Patrols =
        {
            new[] { new Vector2Int(1, 7), new Vector2Int(21, 7), new Vector2Int(21, 8), new Vector2Int(1, 8) },
            new[] { new Vector2Int(10, 3), new Vector2Int(10, 12), new Vector2Int(8, 11), new Vector2Int(12, 1) },
            new[] { new Vector2Int(18, 4), new Vector2Int(19, 12), new Vector2Int(16, 10), new Vector2Int(20, 1) },
        };

        public static int Width => Map[0].Length;
        public static int Depth => Map.Length;

        public static Vector3 PlayerStart, ExitPosition;
        public static readonly List<Vector3> LootPositions = new List<Vector3>();

        static char At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Depth ? 'W' : Map[y][x];

        public static bool Walkable(int x, int y)
        {
            char c = At(x, y);
            return c != 'W' && c != 'c';
        }

        // wiersz 0 mapy leży najdalej w osi z, żeby mapa w kodzie wyglądała jak widok z góry
        public static Vector3 Center(int x, int y) => new Vector3(x * Cell, 0f, (Depth - 1 - y) * Cell);
        public static Vector3 Center(Vector2Int cell) => Center(cell.x, cell.y);

        public static Vector2Int CellOf(Vector3 position) =>
            new Vector2Int(Mathf.RoundToInt(position.x / Cell), Depth - 1 - Mathf.RoundToInt(position.z / Cell));

        public static void Build(Transform root, Dictionary<Collider, Surface> surfaces)
        {
            LootPositions.Clear();
            foreach (var row in Map)
                if (row.Length != Width) Debug.LogError($"Wiersz mapy ma {row.Length} znaków zamiast {Width}: {row}");

            var holder = new GameObject("Poziom").transform;
            holder.SetParent(root, false);

            Vector3 middle = (Center(0, 0) + Center(Width - 1, Depth - 1)) / 2f;
            var size = new Vector3(Width * Cell, 0.2f, Depth * Cell);
            Box(holder, "Podłoga", middle + Vector3.down * 0.1f, size, surfaces);

            for (int y = 0; y < Depth; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Vector3 center = Center(x, y);
                    switch (Map[y][x])
                    {
                        case 'W':
                            Box(holder, "Ściana", center + Vector3.up * Height / 2f, new Vector3(Cell, Height, Cell), surfaces);
                            break;
                        case 'c':
                            Box(holder, "Skrzynia", center + Vector3.up * 0.55f, new Vector3(1.2f, 1.1f, 1.2f), surfaces);
                            Box(holder, "Skrzynia", center + new Vector3(0.15f, 1.4f, -0.1f), new Vector3(0.7f, 0.6f, 0.7f), surfaces);
                            break;
                        case 'P':
                            PlayerStart = center;
                            break;
                        case 'L':
                            LootPositions.Add(center);
                            break;
                        case 'E':
                            ExitPosition = center;
                            break;
                    }
                }
            }
        }

        static void Box(Transform parent, string name, Vector3 position, Vector3 size, Dictionary<Collider, Surface> surfaces)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            surfaces[box] = Surface.Wall;
        }

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
