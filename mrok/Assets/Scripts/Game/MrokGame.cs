using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Bci.Signals;
using Mrok.World;

namespace Mrok.Game
{
    // Skradanka w zupełnej ciemności. Gdy gracz zamyka oczy (sygnał "eyes"), gra liczy czas;
    // gdy je otwiera, skan odsłania stopklatkę otoczenia sięgającą tym dalej, im dłużej oczy
    // były zamknięte. Im większy skan, tym dalej słyszą go strażnicy.
    public class MrokGame : MonoBehaviour
    {
        enum Phase { Play, Caught, Won }

        const float MinClosed = 0.5f;        // krótsze zamknięcie to mrugnięcie
        const float FullCharge = 6f;         // po tylu sekundach skan ma pełny zasięg
        const float MinRadius = 4f, MaxRadius = 28f;
        const float ScanNoise = 0.55f;       // jaką część zasięgu skanu słychać
        const float WallMuffle = 0.55f;      // ściana na drodze skraca zasięg hałasu
        const float CatchDistance = 0.95f;
        const float RestartClosed = 1.5f;

        [NonSerialized] bool built;
        Phase phase;
        SignalBindings bindings;
        Player player;
        PointCloud cloud;
        readonly Dictionary<Collider, Surface> surfaces = new Dictionary<Collider, Surface>();
        readonly List<Guard> guards = new List<Guard>();
        readonly List<GameObject> loot = new List<GameObject>();
        int lootTotal;

        bool eyesClosed;
        float closedAt;
        int ticks;

        AudioSource effects;
        AudioClip ping, tick, step, pickup, caught, win;
        Image blackout;
        Text status, message, controls, signals;
        float messageUntil;
        readonly StringBuilder builder = new StringBuilder();

        // do podglądu i testów
        public string PhaseName => phase.ToString();
        public Player Player => player;
        public PointCloud Cloud => cloud;
        public IReadOnlyList<Guard> Guards => guards;
        public float LastRadius { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindAnyObjectByType<MrokGame>() == null) new GameObject("Mrok").AddComponent<MrokGame>();
        }

        // ---------- budowa ----------

        // Wołane na starcie, po każdej wpadce i po przeładowaniu skryptów w trakcie gry.
        void Build()
        {
            built = true;
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Kamera") { tag = "MainCamera" }.AddComponent<Camera>();
                cam.gameObject.AddComponent<AudioListener>();
            }
            cam.transform.SetParent(null);   // kamera jest dzieckiem gracza, a gracz zaraz zniknie
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);
            surfaces.Clear();
            guards.Clear();
            loot.Clear();
            SignalHub.Clear();

            var config = SignalBindings.Load();
            bindings = GetOrAdd<SignalBindings>();
            bindings.Use(config);
            GetOrAdd<KeyboardSignalSource>().bindings = bindings;
            var udp = GetOrAdd<UdpSignalSource>();
            if (udp.port != config.port)
            {
                udp.enabled = false;   // gniazdo otwiera się w OnEnable, więc port zmieniamy na wyłączonym
                udp.port = config.port;
                udp.enabled = true;
            }
            MarkerSender.Port = config.markerPort;

            // żadnego światła: widać tylko punkty skanu
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 75f;
            foreach (var light in FindObjectsByType<Light>()) light.enabled = false;

            Level.Build(transform, surfaces);
            cloud = new PointCloud();
            cloud.Build(transform);

            effects = GetOrAdd<AudioSource>();
            ping = Sfx.Ping();
            tick = Sfx.Tick();
            step = Sfx.Step(0.2f);
            pickup = Sfx.Pickup();
            caught = Sfx.Caught();
            win = Sfx.Win();

            var playerGo = new GameObject("Gracz");
            playerGo.transform.SetParent(transform, false);
            player = playerGo.AddComponent<Player>();
            player.Build(cam, Level.PlayerStart);
            player.Stepped = OnPlayerStep;

            AudioClip guardStep = Sfx.Step(1f), alert = Sfx.Alert();
            foreach (var route in Level.Patrols)
            {
                var guardGo = new GameObject("Strażnik");
                guardGo.transform.SetParent(transform, false);
                var guard = guardGo.AddComponent<Guard>();
                guard.Build(route, surfaces, guardStep, alert);
                guards.Add(guard);
            }

            AudioClip beacon = Sfx.Beacon();
            foreach (var position in Level.LootPositions) loot.Add(BuildLoot(position, beacon));
            lootTotal = loot.Count;
            BuildExit();
            BuildHud();

            phase = Phase.Play;
            eyesClosed = false;
            Say("Jest zupełnie ciemno.\nZamknij oczy, policz do trzech i otwórz.", 600f);
            MarkerSender.Send("session_start");
        }

        T GetOrAdd<T>() where T : Component
        {
            var component = GetComponent<T>();
            return component != null ? component : gameObject.AddComponent<T>();
        }

        // łup na postumencie; dzwoni cicho, żeby dało się go znaleźć na słuch
        GameObject BuildLoot(Vector3 position, AudioClip beacon)
        {
            var go = new GameObject("Łup");
            go.transform.SetParent(transform, false);
            go.transform.position = position;
            var pedestal = go.AddComponent<BoxCollider>();
            pedestal.center = new Vector3(0f, 0.45f, 0f);
            pedestal.size = new Vector3(0.5f, 0.9f, 0.5f);
            surfaces[pedestal] = Surface.Wall;
            var treasure = go.AddComponent<SphereCollider>();
            treasure.center = new Vector3(0f, 1.15f, 0f);
            treasure.radius = 0.24f;
            surfaces[treasure] = Surface.Loot;

            var source = go.AddComponent<AudioSource>();
            source.clip = beacon;
            source.loop = true;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1f;
            source.maxDistance = 14f;
            source.volume = 0.5f;
            source.Play();
            return go;
        }

        void BuildExit()
        {
            var go = new GameObject("Wyjście");
            go.transform.SetParent(transform, false);
            go.transform.position = Level.ExitPosition + new Vector3(0f, 1.1f, -Level.Cell / 2f + 0.06f);
            var door = go.AddComponent<BoxCollider>();
            door.size = new Vector3(1.3f, 2.2f, 0.1f);
            surfaces[door] = Surface.Exit;
        }

        void BuildHud()
        {
            var canvasGo = new GameObject("Napisy");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 1f;

            // zasłona na czas zamkniętych oczu: klawiatura udaje sygnał, więc ekran też musi zgasnąć
            var cover = new GameObject("Zasłona");
            cover.transform.SetParent(canvasGo.transform, false);
            blackout = cover.AddComponent<Image>();
            blackout.color = Color.black;
            blackout.raycastTarget = false;
            blackout.rectTransform.anchorMin = Vector2.zero;
            blackout.rectTransform.anchorMax = Vector2.one;
            blackout.rectTransform.offsetMin = blackout.rectTransform.offsetMax = Vector2.zero;
            blackout.enabled = false;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            status = MakeText(canvasGo.transform, font, TextAnchor.UpperLeft, 18, new Vector2(16f, -12f));
            message = MakeText(canvasGo.transform, font, TextAnchor.MiddleCenter, 26, new Vector2(0f, 140f));
            controls = MakeText(canvasGo.transform, font, TextAnchor.LowerLeft, 14, new Vector2(16f, 12f));
            signals = MakeText(canvasGo.transform, font, TextAnchor.LowerRight, 14, new Vector2(-16f, 12f));
            controls.text = "WASD  ruch (Shift: skradanie)\nmysz albo Q / E  obrót\nSpacja (trzymaj)  zamknięte oczy";
            Color dim = new Color(0.55f, 0.6f, 0.65f);
            status.color = controls.color = signals.color = dim;
        }

        static Text MakeText(Transform parent, Font font, TextAnchor anchor, int size, Vector2 offset)
        {
            var go = new GameObject("Tekst");
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(Mathf.Max(0f, offset.x), Mathf.Max(0f, offset.y));
            rect.offsetMax = new Vector2(Mathf.Min(0f, offset.x), Mathf.Min(0f, offset.y));
            return text;
        }

        void Say(string text, float seconds)
        {
            message.text = text;
            messageUntil = Time.time + seconds;
        }

        // ---------- rozgrywka ----------

        void Update()
        {
            if (!built) Build();
            cloud.Tick();
            UpdateEyes();

            if (phase == Phase.Play)
            {
                CollectLoot();
                CheckExit();
                CheckGuards();
            }
            else if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                built = false;
            }

            if (Time.time > messageUntil) message.text = "";
            status.text = $"łup {lootTotal - loot.Count}/{lootTotal}";
            ShowSignals();
        }

        void UpdateEyes()
        {
            bool closed = bindings.Held("eyes");
            blackout.enabled = closed;

            if (closed && !eyesClosed)
            {
                closedAt = Time.time;
                ticks = 0;
                MarkerSender.Send("eyes_closed");
            }
            if (closed)
            {
                // tyknięcie co sekundę, coraz wyższe: jedyna wskazówka, jak duży będzie skan
                float held = Time.time - closedAt;
                if (held >= ticks + 1 && ticks < FullCharge)
                {
                    ticks++;
                    effects.pitch = 1f + 0.12f * ticks;
                    effects.PlayOneShot(tick);
                }
            }
            if (!closed && eyesClosed)
            {
                float held = Time.time - closedAt;
                MarkerSender.Send("eyes_open", held);
                if (phase != Phase.Play)
                {
                    if (held >= RestartClosed) built = false;
                }
                else if (held >= MinClosed)
                {
                    Scan(Mathf.Lerp(MinRadius, MaxRadius, Mathf.Clamp01((held - MinClosed) / (FullCharge - MinClosed))));
                }
            }
            eyesClosed = closed;
        }

        void Scan(float radius)
        {
            LastRadius = radius;
            cloud.Scan(player.EyePosition, radius, surfaces);
            effects.pitch = Mathf.Lerp(1.3f, 0.75f, radius / MaxRadius);   // duży skan brzmi niżej i dłużej
            effects.PlayOneShot(ping);
            Noise(player.transform.position, radius * ScanNoise);
            if (messageUntil > Time.time + 100f) Say("", 0f);   // pierwsza podpowiedź znika po pierwszym skanie
            MarkerSender.Send("scan", radius);
        }

        void OnPlayerStep(float reach)
        {
            effects.pitch = UnityEngine.Random.Range(0.9f, 1.1f);
            effects.PlayOneShot(step, reach > 2f ? 0.35f : 0.12f);
            if (phase == Phase.Play) Noise(player.transform.position, reach);
        }

        // Hałas słyszą strażnicy w zasięgu; ściana po drodze go tłumi.
        void Noise(Vector3 position, float reach)
        {
            Vector3 from = position + Vector3.up * 1.2f;
            foreach (var guard in guards)
            {
                Vector3 to = guard.transform.position + Vector3.up * 1.2f;
                bool wall = Physics.Linecast(from, to, out var hit) && surfaces.TryGetValue(hit.collider, out var surface) && surface == Surface.Wall;
                if (Vector3.Distance(from, to) <= reach * (wall ? WallMuffle : 1f))
                {
                    if (guard.state == Guard.State.Patrol) MarkerSender.Send("alert", guards.IndexOf(guard));
                    guard.Hear(position);
                }
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        void CollectLoot()
        {
            for (int i = loot.Count - 1; i >= 0; i--)
            {
                if (FlatDistance(loot[i].transform.position, player.transform.position) > 1.1f) continue;
                foreach (var collider in loot[i].GetComponents<Collider>()) surfaces.Remove(collider);
                Destroy(loot[i]);
                loot.RemoveAt(i);
                effects.pitch = 1f;
                effects.PlayOneShot(pickup);
                Say(loot.Count > 0 ? $"Masz {lootTotal - loot.Count} z {lootTotal}." : "Masz wszystko. Szukaj zielonych drzwi.", 4f);
                MarkerSender.Send("loot", lootTotal - loot.Count);
            }
        }

        void CheckExit()
        {
            if (FlatDistance(Level.ExitPosition, player.transform.position) > 1.3f) return;
            if (loot.Count > 0)
            {
                Say($"Drzwi. Brakuje jeszcze łupu: {loot.Count}.", 0.5f);
                return;
            }
            End(Phase.Won, win, "Uciekłeś z łupem.\nZamknij oczy na chwilę, aby zagrać jeszcze raz.", "win");
        }

        void CheckGuards()
        {
            foreach (var guard in guards)
            {
                if (FlatDistance(guard.transform.position, player.transform.position) > CatchDistance) continue;
                End(Phase.Caught, caught, "Złapany.\nZamknij oczy na chwilę, aby zacząć od nowa.", "caught");
                return;
            }
        }

        void End(Phase result, AudioClip sound, string text, string marker)
        {
            phase = result;
            player.frozen = true;
            foreach (var guard in guards) guard.enabled = false;
            effects.pitch = 1f;
            effects.PlayOneShot(sound);
            Say(text, 600f);
            MarkerSender.Send(marker);
        }

        void ShowSignals()
        {
            builder.Clear();
            builder.AppendLine("SYGNAŁY");
            foreach (var signal in SignalHub.All)
            {
                bool fresh = SignalHub.TryGet(signal.name, out float value);
                builder.Append(signal.name).Append("  ").Append(fresh ? value.ToString("+0.00;-0.00") : "–")
                    .Append("  ").AppendLine(signal.source);
            }
            foreach (var binding in bindings.config.bindings)
            {
                builder.Append(binding.action).Append(" ← ").Append(binding.source ?? binding.signal).Append("  ");
                builder.AppendLine(binding.live ? binding.value.ToString("+0.00;-0.00") : "brak sygnału");
            }
            bool bridge = Time.unscaledTime - UdpSignalSource.LastPacketAt < 2f;
            builder.Append(bridge ? "most LSL: " + UdpSignalSource.LastSender : "most LSL: brak danych");
            signals.text = builder.ToString();
        }
    }
}
