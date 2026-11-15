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
    // były zamknięte. Strażnicy gracza nie widzą, tylko słyszą.
    //
    // Otwarte oczy to presja, zamknięte to schronienie:
    //  - skan po długim, spokojnym zamknięciu jest prawie niesłyszalny; pospieszny jest głośny,
    //  - dopóki oczy są zamknięte, strażnicy zwalniają i szybciej porzucają poszukiwania,
    //  - napięcie rośnie, gdy strażnik jest blisko albo zaalarmowany: bije serce, punkty drżą
    //    i szybciej gasną; zamknięcie oczu je rozładowuje, a po długim obraz trzyma się dłużej,
    //  - ekran końcowy podaje liczbę i długość pauz.
    //
    // W czasie gry na ekranie nie ma napisów: tekst pojawia się tylko na ekranie ładowania
    // i po zakończeniu poziomu, a wskazówki w świecie to napisy z punktów, które odsłania skan.
    // Poziom wybiera się w sali z trojgiem drzwi, przechodząc przez jedne z nich.
    //
    // Kamienie leżą na podłodze; podniesione można rzucać (lewy przycisk myszy). Dopóki przycisk
    // jest wciśnięty, siła rzutu kołysze się tam i z powrotem, a tor lotu widać jako łuk punktów.
    // Strażnicy idą sprawdzić miejsce, w które kamień uderzył.
    public class MrokGame : MonoBehaviour
    {
        enum Phase { Loading, Play, Caught, Won }

        const float MinClosed = 0.5f;        // krótsze zamknięcie to mrugnięcie
        const float FullCharge = 6f;         // po tylu sekundach skan ma pełny zasięg
        const float MinRadius = 4f, MaxRadius = 28f;
        const float RushedNoise = 12f, CalmNoise = 2f;   // z jak daleka słychać skan: pospieszny i spokojny
        const float QuietAfter = 3.5f;       // po tylu sekundach zamkniętych oczu skan jest najcichszy
        const float PauseSeconds = 2f;       // zamknięcie co najmniej tak długie liczy się jako pauza
        const float SerenePause = 3f;        // po takiej pauzie obraz trzyma się dłużej
        const float PressureRange = 12f;     // bliżej niż tyle strażnik budzi napięcie
        const float WallMuffle = 0.55f;      // ściana na drodze skraca zasięg hałasu
        const float CatchDistance = 0.95f;
        const float RestartClosed = 1.5f;
        const float StepHeard = 11f;         // z jak daleka kroki strażnika zostawiają widoczny ślad
        const float HubPulse = 6f;           // co ile sekund sala wyboru skanuje się sama
        const float HubRadius = 16f;
        const float RockNoise = 20f;         // z jak daleka słychać upadek kamienia
        const float ThrowSlow = 6f, ThrowFast = 16f, ThrowSwing = 1.1f;   // prędkość rzutu i czas jednego wahnięcia siły
        const float ThrowGravity = 12f, ThrowLift = 14f;                  // łuk celowo wyraźny; rzut idzie nieco ponad celownik
        const int MaxStones = 6;
        const float RockScanSeconds = 1f;    // kamień, upadając, skanuje okolicę jak po tylu sekundach zamkniętych oczu

        static int levelIndex = Levels.Hub;
        static bool showDebug;               // F1: napisy diagnostyczne dla prowadzącego pokaz

        [NonSerialized] bool built;
        Phase phase;
        SignalBindings bindings;
        Player player;
        PointCloud cloud, echoes;   // skan i ślady kroków, które gracz słyszy
        readonly Dictionary<Collider, Surface> surfaces = new Dictionary<Collider, Surface>();
        readonly List<Guard> guards = new List<Guard>();
        readonly List<GameObject> loot = new List<GameObject>();
        int lootTotal;

        readonly List<Sign> pebbles = new List<Sign>();   // kamienie leżące na poziomie
        readonly List<Vector3> line = new List<Vector3>();
        PointCloud marks;                                 // tor rzutu i lecący kamień
        int stones;
        bool aiming, flying, lockedBefore;
        float aimAt;
        Vector3 rockAt, rockVelocity;
        AudioClip clack;

        bool eyesClosed;
        float closedAt;
        int ticks;

        float stress, serenity, beatAt;   // napięcie 0-1, spokój po długiej pauzie 0-1
        int pauses;
        float longestPause;
        AudioSource heart;
        AudioClip heartbeat;

        // Tryb sterowania oczami (F2). W obu trybach spacja to "prawda": gracz trzyma ją, gdy
        // ma zamknięte oczy, a gra wysyła znaczniki truth_closed / truth_open do nagrania EEG.
        // Tak samo F to prawda o zaciśniętej szczęce: truth_jaw_on / truth_jaw_off, a reakcję gry
        // zapisują znaczniki sprint_on / sprint_off (odpowiedniki eyes_closed / eyes_open).
        //   KLAWISZ: grą steruje spacja; potok BCI tylko nagrywa (zbieranie danych do strojenia).
        //   EEG:     grą steruje sygnał eyes_closed_active; spacja niczego w grze nie zmienia.
        //            Bez danych z potoku spacja steruje jak dawniej, żeby dało się grać bez opaski.
        static readonly int TrembleId = Shader.PropertyToID("_Tremble"), LifeScaleId = Shader.PropertyToID("_LifeScale");

        static bool keyboardMode;
        KeyboardSignalSource keyboardSource;
        float truthAt, sprintTruthAt, sprintAt;
        bool truthEyes, truthJaw;

        AudioSource effects;
        AudioClip ping, tick, step, pickup, caught, win, deny;
        Image blackout, curtain;
        Text title, body, footer, toast, status, signals;
        GameObject debug;
        readonly List<Image> pips = new List<Image>(), dots = new List<Image>();
        float loadUntil, curtainTarget, toastUntil, pulseAt, deniedAt = -10f;
        float fps = 60f;
        readonly StringBuilder builder = new StringBuilder();

        // do podglądu i testów
        public string PhaseName => phase.ToString();
        public Player Player => player;
        public PointCloud Cloud => cloud;
        public IReadOnlyList<Guard> Guards => guards;
        public float LastRadius { get; private set; }

        // Projekt wchodzi w Play bez przeładowania domeny, więc pola statyczne przeżywają zatrzymanie
        // gry. Bez tego Play zaczynałby się w poziomie, w którym skończyła się poprzednia rozgrywka.
        // Tryb KEY / EEG i napisy diagnostyczne celowo zostają takie, jak ustawił je prowadzący.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void StartAtTheDoors()
        {
            levelIndex = Levels.Hub;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindAnyObjectByType<MrokGame>() == null) new GameObject("Mrok").AddComponent<MrokGame>();
        }

        // Poziom o podanym numerze (Levels.All) zaczyna się od następnej klatki.
        public void Open(int level)
        {
            levelIndex = Mathf.Clamp(level, 0, Levels.All.Length - 1);
            built = false;
        }

        // ---------- budowa ----------

        // Wołane na starcie, przy zmianie poziomu, po każdej wpadce i po przeładowaniu skryptów w trakcie gry.
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
            pips.Clear();
            dots.Clear();
            pebbles.Clear();
            stones = 0;
            aiming = flying = false;
            stress = serenity = 0f;
            pauses = 0;
            longestPause = 0f;
            Guard.Pace = 1f;
            SignalHub.Clear();

            var config = SignalBindings.Load();
            bindings = GetOrAdd<SignalBindings>();
            bindings.Use(config);
            keyboardSource = GetOrAdd<KeyboardSignalSource>();
            keyboardSource.bindings = bindings;
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

            LevelInfo level = Levels.All[levelIndex];
            Level.Current = level;
            Level.Build(transform, surfaces);
            cloud = new PointCloud();
            cloud.Build(transform);
            echoes = new PointCloud(4096);
            echoes.Build(transform);
            marks = new PointCloud(4096);
            marks.Build(transform);
            foreach (var position in Level.RockPositions)
            {
                var pebble = Sign.Pebble(position + Vector3.up * 0.07f, 0.1f);
                pebbles.Add(pebble);
                Level.Signs.Add(pebble);
            }

            effects = GetOrAdd<AudioSource>();
            ping = Sfx.Ping();
            tick = Sfx.Tick();
            step = Sfx.Step(0.2f);
            pickup = Sfx.Pickup();
            caught = Sfx.Caught();
            win = Sfx.Win();
            deny = Sfx.Deny();
            heartbeat = Sfx.Heartbeat();
            // serce ma własne źródło: pozostałe efekty zmieniają wysokość wspólnego
            heart = new GameObject("Serce").AddComponent<AudioSource>();
            heart.transform.SetParent(transform, false);
            heart.playOnAwake = false;
            clack = Sfx.Clack();

            var playerGo = new GameObject("Gracz");
            playerGo.transform.SetParent(transform, false);
            player = playerGo.AddComponent<Player>();
            player.Build(cam, Level.PlayerStart, level.startYaw);
            player.Stepped = OnPlayerStep;
            player.frozen = true;

            AudioClip guardStep = Sfx.Step(1f), alert = Sfx.Alert();
            foreach (var route in Level.Patrols)
            {
                var guardGo = new GameObject("Strażnik");
                guardGo.transform.SetParent(transform, false);
                var guard = guardGo.AddComponent<Guard>();
                guard.Build(route, surfaces, guardStep, alert);
                AddGuard(guard);
            }
            foreach (var post in level.posts)
            {
                var guardGo = new GameObject("Wartownik");
                guardGo.transform.SetParent(transform, false);
                var guard = guardGo.AddComponent<Guard>();
                guard.Build(new[] { new Vector2Int(post.x, post.y) }, surfaces, guardStep, alert, post.yaw);
                AddGuard(guard);
            }

            AudioClip beacon = Sfx.Beacon();
            foreach (var position in Level.LootPositions) loot.Add(BuildLoot(position, beacon));
            lootTotal = loot.Count;
            if (level.hub)
                foreach (var portal in Level.Portals.Values) BuildDoor(portal, true);
            else
                BuildDoor(Level.ExitPosition, false);
            BuildHud();

            // ekran ładowania: jedyne miejsce, gdzie gra mówi do gracza tekstem
            phase = Phase.Loading;
            eyesClosed = false;
            title.text = Spaced(level.title);
            body.text = string.Join("\n", level.intro);
            footer.text = Gamepad.current != null
                ? "Left stick move     Right stick look     Clench jaw to sprint     B sneak     Hold RT to throw     Select doors"
                : "WASD move     Clench jaw to sprint     Shift sneak     Hold click to throw     Esc doors";
            curtain.color = Color.black;
            curtainTarget = 1f;
            loadUntil = Time.time + (level.hub ? 2.5f : 4.5f);
            MarkerSender.Send("session_start");
            MarkerSender.Send("level", levelIndex);
            MarkerSender.Send(keyboardMode ? "mode_keyboard" : "mode_eeg");
        }

        void AddGuard(Guard guard)
        {
            guard.Stepped = OnGuardStep;
            guard.prey = player;
            guard.enabled = false;   // rusza dopiero po ekranie ładowania
            guards.Add(guard);
        }

        static string Spaced(string text) => string.Join(" ", text.ToCharArray());

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
            var shapes = new Shapes(go.transform, surfaces);
            shapes.Lathe(Vector3.zero, Surface.Prop,
                0.34f, 0f, 0.34f, 0.08f, 0.24f, 0.14f, 0.2f, 0.78f, 0.3f, 0.84f, 0.3f, 0.9f, 0f, 0.9f);
            Vector3 top = Vector3.up * 0.9f;
            switch (loot.Count % 3)
            {
                case 0:   // waza
                    shapes.Lathe(top, Surface.Loot,
                        0f, 0f, 0.09f, 0f, 0.07f, 0.04f, 0.19f, 0.2f, 0.2f, 0.3f, 0.1f, 0.44f, 0.08f, 0.5f, 0.13f, 0.54f, 0f, 0.5f);
                    break;
                case 1:   // kielich
                    shapes.Lathe(top, Surface.Loot,
                        0f, 0f, 0.13f, 0f, 0.12f, 0.03f, 0.03f, 0.06f, 0.03f, 0.22f, 0.15f, 0.36f, 0.17f, 0.5f, 0.15f, 0.5f, 0.02f, 0.3f, 0f, 0.3f);
                    break;
                default:  // klejnot na obręczy
                    shapes.Lathe(top, Surface.Loot, 0f, 0f, 0.14f, 0f, 0.14f, 0.05f, 0.06f, 0.07f, 0f, 0.07f);
                    shapes.Box(top + Vector3.up * 0.27f, Vector3.one * 0.24f, Surface.Loot, Quaternion.Euler(45f, 0f, 35.26f));
                    break;
            }

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

        // Zielone drzwi w murze za polem: ościeżnica z nadprożem i uchylone skrzydło. W poziomie
        // to wyjście (mur od południa), w sali wyboru wejście do poziomu (mur od północy).
        void BuildDoor(Vector3 cell, bool north)
        {
            var go = new GameObject("Drzwi");
            go.transform.SetParent(transform, false);
            go.transform.position = cell + new Vector3(0f, 0f, north ? Level.Cell / 2f : -Level.Cell / 2f);
            go.transform.rotation = Quaternion.Euler(0f, north ? 180f : 0f, 0f);
            var shapes = new Shapes(go.transform, surfaces);
            shapes.Box(new Vector3(-0.75f, 1.2f, 0.06f), new Vector3(0.14f, 2.4f, 0.14f), Surface.Exit);
            shapes.Box(new Vector3(0.75f, 1.2f, 0.06f), new Vector3(0.14f, 2.4f, 0.14f), Surface.Exit);
            shapes.Box(new Vector3(0f, 2.47f, 0.06f), new Vector3(1.64f, 0.14f, 0.14f), Surface.Exit);
            shapes.Box(new Vector3(-0.68f, 1.2f, 0.03f), new Vector3(1.36f, 2.4f, 0.06f), Surface.Exit, Quaternion.Euler(0f, -14f, 0f)).center = new Vector3(0.68f, 0f, 0f);
            shapes.Sphere(new Vector3(0.45f, 1.05f, 0.32f), 0.05f, Surface.Exit);   // klamka
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
            blackout = Cover(canvasGo.transform, "Zasłona");
            blackout.enabled = false;

            // łup jako rząd rombów u dołu ekranu: zebrane świecą na złoto
            for (int i = 0; i < lootTotal; i++)
            {
                var pip = new GameObject("Łup").AddComponent<Image>();
                pip.transform.SetParent(canvasGo.transform, false);
                pip.raycastTarget = false;
                pip.rectTransform.anchorMin = pip.rectTransform.anchorMax = new Vector2(0.5f, 0f);
                pip.rectTransform.sizeDelta = new Vector2(9f, 9f);
                pip.rectTransform.anchoredPosition = new Vector2((i - (lootTotal - 1) / 2f) * 24f, 26f);
                pip.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
                pips.Add(pip);
            }

            // kamienie w kieszeni: rząd kropek pod rombami
            for (int i = 0; i < MaxStones; i++)
            {
                var dot = new GameObject("Kamień").AddComponent<Image>();
                dot.transform.SetParent(canvasGo.transform, false);
                dot.raycastTarget = false;
                dot.color = new Color(0.55f, 0.85f, 1f, 0.85f);
                dot.rectTransform.anchorMin = dot.rectTransform.anchorMax = new Vector2(0.5f, 0f);
                dot.rectTransform.sizeDelta = new Vector2(5f, 5f);
                dots.Add(dot);
            }

            // kurtyna z tekstem: ekran ładowania i zakończenie poziomu
            curtain = Cover(canvasGo.transform, "Kurtyna");
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            title = MakeText(curtain.transform, font, TextAnchor.MiddleCenter, 40, new Vector2(0f, 50f));
            body = MakeText(curtain.transform, font, TextAnchor.MiddleCenter, 21, new Vector2(0f, -110f));
            footer = MakeText(curtain.transform, font, TextAnchor.LowerCenter, 13, new Vector2(0f, 28f));
            body.lineSpacing = 1.5f;
            body.color = new Color(0.72f, 0.76f, 0.8f);
            footer.color = new Color(0.4f, 0.44f, 0.48f);

            // napisy dla prowadzącego pokaz, domyślnie ukryte (F1)
            debug = new GameObject("Diagnostyka");
            debug.transform.SetParent(canvasGo.transform, false);
            var area = debug.AddComponent<RectTransform>();
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.offsetMin = area.offsetMax = Vector2.zero;
            status = MakeText(debug.transform, font, TextAnchor.UpperLeft, 14, new Vector2(16f, -12f));
            signals = MakeText(debug.transform, font, TextAnchor.LowerRight, 14, new Vector2(-16f, 12f));
            toast = MakeText(canvasGo.transform, font, TextAnchor.UpperCenter, 16, new Vector2(0f, -14f));
            status.color = signals.color = toast.color = new Color(0.55f, 0.6f, 0.65f);
            debug.SetActive(showDebug);
        }

        static Image Cover(Transform parent, string name)
        {
            var image = new GameObject(name).AddComponent<Image>();
            image.transform.SetParent(parent, false);
            image.color = Color.black;
            image.raycastTarget = false;
            image.rectTransform.anchorMin = Vector2.zero;
            image.rectTransform.anchorMax = Vector2.one;
            image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
            return image;
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

        // ---------- rozgrywka ----------

        void Update()
        {
            if (!built) Build();
            cloud.Tick();
            UpdateKeys();
            UpdateEyes();
            UpdatePressure();

            if (phase == Phase.Loading && Time.time >= loadUntil)
            {
                phase = Phase.Play;
                curtainTarget = 0f;
                player.frozen = false;
                foreach (var guard in guards) guard.enabled = true;
                pulseAt = Time.time + 0.3f;
            }
            if (phase == Phase.Play)
            {
                if (Level.Current.hub)
                {
                    // sala wyboru odsłania się sama, żeby drzwi i napisy było widać bez opaski
                    if (Time.time >= pulseAt)
                    {
                        pulseAt = Time.time + HubPulse;
                        Scan(HubRadius, 0.35f, 0f);
                    }
                    CheckPortals();
                }
                else
                {
                    CollectLoot();
                    CollectStones();
                    UpdateThrow();
                    CheckExit();
                    CheckGuards();
                }
            }
            UpdateHud();
        }

        void UpdateKeys()
        {
            // pad: Start ponawia poziom po jego zakończeniu, Select wraca do sali z drzwiami
            var gamepad = Gamepad.current;
            if (gamepad != null)
            {
                if (gamepad.startButton.wasPressedThisFrame && (phase == Phase.Caught || phase == Phase.Won)) built = false;
                if (gamepad.selectButton.wasPressedThisFrame && !Level.Current.hub) Open(Levels.Hub);
            }

            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.f1Key.wasPressedThisFrame)
            {
                showDebug = !showDebug;
                debug.SetActive(showDebug);
            }
            if (keyboard.f2Key.wasPressedThisFrame)
            {
                keyboardMode = !keyboardMode;
                MarkerSender.Send(keyboardMode ? "mode_keyboard" : "mode_eeg");
                Toast(keyboardMode ? "KEY mode: space controls the eyes" : "EEG mode: the headset controls the eyes");
            }
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (Level.Current.hub) Cursor.lockState = CursorLockMode.None;
                else Open(Levels.Hub);
            }
            if (keyboard.rKey.wasPressedThisFrame && (phase == Phase.Caught || phase == Phase.Won)) built = false;
            // skróty dla prowadzącego: w sali wyboru cyfra otwiera poziom
            if (Level.Current.hub && phase == Phase.Play)
            {
                if (keyboard.digit1Key.wasPressedThisFrame) Open(1);
                if (keyboard.digit2Key.wasPressedThisFrame) Open(2);
                if (keyboard.digit3Key.wasPressedThisFrame) Open(3);
            }
        }

        void Toast(string text)
        {
            toast.text = text;
            toastUntil = Time.time + 2.5f;
        }

        static bool BridgeAlive => Time.unscaledTime - UdpSignalSource.LastPacketAt < 2f;

        void UpdateEyes()
        {
            var keyboard = Keyboard.current;

            var gamepad = Gamepad.current;

            // prawda do nagrania: spacja albo A na padzie = gracz ma zamknięte oczy (w obu trybach)
            bool space = (keyboard != null && keyboard.spaceKey.isPressed) || (gamepad != null && gamepad.buttonSouth.isPressed);
            if (space != truthEyes)
            {
                if (space) truthAt = Time.time;
                MarkerSender.Send(space ? "truth_closed" : "truth_open", space ? 0f : Time.time - truthAt);
                truthEyes = space;
            }

            // to samo dla sprintu: klawisz z powiązania (F) albo lewy spust = gracz zaciska szczękę
            bool clench = gamepad != null && gamepad.leftTrigger.isPressed;
            if (keyboard != null && Enum.TryParse(bindings.Get("sprint")?.key, true, out Key sprintKey) && sprintKey != Key.None)
                clench |= keyboard[sprintKey].isPressed;
            if (clench != truthJaw)
            {
                if (clench) sprintTruthAt = Time.time;
                MarkerSender.Send(clench ? "truth_jaw_on" : "truth_jaw_off", clench ? 0f : Time.time - sprintTruthAt);
                truthJaw = clench;
            }

            // w trybie EEG klawisze nie mogą podmieniać sygnału z opaski, chyba że potoku nie ma;
            // pad nie przechodzi przez KeyboardSignalSource, więc jego przyciski dokładamy tutaj
            bool keys = keyboardMode || !BridgeAlive;
            keyboardSource.enabled = keys;
            bool closed = keyboardMode ? space : bindings.Held("eyes") || (keys && space);

            // Sprint: zaciśnięta szczęka, zero-jedynkowo. Mięśnie szczęki zakłócają też EEG nad
            // potylicą, więc w trakcie biegu sygnał z opaski nie może zacząć "zamykać oczu".
            bool sprinting = bindings.Held("sprint") || (keys && clench);
            if (sprinting != player.sprint)
            {
                if (sprinting) sprintAt = Time.time;
                MarkerSender.Send(sprinting ? "sprint_on" : "sprint_off", sprinting ? 0f : Time.time - sprintAt);
            }
            player.sprint = sprinting;
            if (!keyboardMode && BridgeAlive && player.sprint && !eyesClosed) closed = false;
            blackout.enabled = closed;

            if (closed && !eyesClosed)
            {
                closedAt = Time.time;
                ticks = 0;
                MarkerSender.Send("eyes_closed");
            }
            if (closed && phase != Phase.Loading)
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
                if (phase == Phase.Caught)
                {
                    if (held >= RestartClosed) built = false;
                }
                else if (phase == Phase.Won)
                {
                    if (held >= RestartClosed) Open(Levels.Hub);
                }
                else if (phase == Phase.Play && held >= MinClosed)
                {
                    // im dłużej i spokojniej, tym ciszej: pospieszny skan słychać z daleka
                    float calm = Mathf.Clamp01((held - MinClosed) / (QuietAfter - MinClosed));
                    Scan(ScanRadius(held), Mathf.Lerp(1f, 0.45f, calm), Mathf.Lerp(RushedNoise, CalmNoise, calm));
                    pulseAt = Time.time + HubPulse;
                    if (held >= PauseSeconds && !Level.Current.hub)
                    {
                        pauses++;
                        longestPause = Mathf.Max(longestPause, held);
                    }
                    if (held >= SerenePause) serenity = 1f;
                }
            }
            eyesClosed = closed;
        }

        // zasięg skanu po tylu sekundach zamkniętych oczu
        static float ScanRadius(float held) => Mathf.Lerp(MinRadius, MaxRadius, Mathf.Clamp01((held - MinClosed) / (FullCharge - MinClosed)));

        // Napięcie i wytchnienie: tempo strażników, bicie serca, drżenie i trwałość obrazu.
        void UpdatePressure()
        {
            float dt = Time.deltaTime;
            bool playing = phase == Phase.Play && !Level.Current.hub;
            Guard.Pace = Mathf.MoveTowards(Guard.Pace, playing && eyesClosed ? 0.5f : 1f, 2f * dt);

            float target = 0f;
            if (playing)
            {
                foreach (var guard in guards)
                {
                    float near = Mathf.Clamp01(1f - FlatDistance(guard.transform.position, player.transform.position) / PressureRange);
                    target = Mathf.Max(target, guard.Alerted ? Mathf.Max(near, 0.35f) : near * 0.65f);
                }
            }
            // zamknięte oczy rozładowują napięcie niezależnie od tego, co dzieje się wokół
            if (eyesClosed || !playing) stress = Mathf.MoveTowards(stress, 0f, 0.45f * dt);
            else stress = Mathf.MoveTowards(stress, target, (target > stress ? 0.3f : 0.12f) * dt);
            serenity = Mathf.MoveTowards(serenity, 0f, dt / 25f);

            Shader.SetGlobalFloat(TrembleId, stress * stress * 0.03f);
            Shader.SetGlobalFloat(LifeScaleId, Mathf.Lerp(1f, 0.45f, stress) * (1f + 0.35f * serenity));

            if (playing && stress > 0.12f && Time.time >= beatAt)
            {
                beatAt = Time.time + Mathf.Lerp(1.05f, 0.48f, stress);
                heart.PlayOneShot(heartbeat, Mathf.Lerp(0.15f, 0.9f, stress));
            }
        }

        void Scan(float radius, float volume, float noise)
        {
            LastRadius = radius;
            Physics.SyncTransforms();   // strażnicy ruszają się poza krokiem fizyki; skan ma trafić w ich obecną pozę
            cloud.Scan(player.EyePosition, radius, surfaces, Level.Signs);
            effects.pitch = Mathf.Lerp(1.3f, 0.75f, radius / MaxRadius);   // duży skan brzmi niżej i dłużej
            effects.PlayOneShot(ping, volume);
            if (noise > 0f) Noise(player.transform.position, noise);
            MarkerSender.Send("scan", radius);
        }

        void OnPlayerStep(float reach)
        {
            effects.pitch = UnityEngine.Random.Range(0.9f, 1.1f);
            effects.PlayOneShot(step, reach > 4f ? 0.35f : 0.12f);
            if (phase == Phase.Play) Noise(player.transform.position, reach);
        }

        // Krok strażnika, który gracz słyszy, zostawia na podłodze gasnący ślad. Zza muru słychać
        // słabiej, więc ślad pojawia się dopiero z bliska.
        void OnGuardStep(Guard guard, Vector3 foot)
        {
            Vector3 from = player.EyePosition, to = foot + Vector3.up * 1.2f;
            bool wall = Physics.Linecast(from, to, out var hit) && surfaces.TryGetValue(hit.collider, out var surface) && surface == Surface.Wall;
            if (Vector3.Distance(from, to) > StepHeard * (wall ? WallMuffle : 1f)) return;
            bool alert = guard.Alerted;
            echoes.Footprint(foot, guard.transform.forward, alert ? new Color(2.6f, 0.3f, 0.1f) : new Color(1.5f, 0.42f, 0.1f), alert ? 5f : 3.5f);
        }

        // Hałas słyszą strażnicy w zasięgu; ściana po drodze go tłumi.
        void Noise(Vector3 position, float reach, bool loud = false)
        {
            Vector3 from = position + Vector3.up * 1.2f;
            foreach (var guard in guards)
            {
                Vector3 to = guard.transform.position + Vector3.up * 1.2f;
                bool wall = Physics.Linecast(from, to, out var hit) && surfaces.TryGetValue(hit.collider, out var surface) && surface == Surface.Wall;
                if (Vector3.Distance(from, to) <= reach * (wall ? WallMuffle : 1f))
                {
                    if (guard.state == Guard.State.Patrol) MarkerSender.Send("alert", guards.IndexOf(guard));
                    guard.Hear(position, loud);
                }
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        // w sali wyboru podejście do drzwi otwiera poziom
        void CheckPortals()
        {
            foreach (var portal in Level.Portals)
            {
                if (FlatDistance(portal.Value + Vector3.forward * Level.Cell / 2f, player.transform.position) > 1.1f) continue;
                Open(portal.Key);
                return;
            }
        }

        void CollectLoot()
        {
            for (int i = loot.Count - 1; i >= 0; i--)
            {
                if (FlatDistance(loot[i].transform.position, player.transform.position) > 1.1f) continue;
                foreach (var collider in loot[i].GetComponentsInChildren<Collider>()) surfaces.Remove(collider);
                Destroy(loot[i]);
                loot.RemoveAt(i);
                effects.pitch = 1f;
                effects.PlayOneShot(pickup);
                MarkerSender.Send("loot", lootTotal - loot.Count);
            }
        }

        void CollectStones()
        {
            for (int i = pebbles.Count - 1; i >= 0; i--)
            {
                if (stones >= MaxStones || FlatDistance(pebbles[i].center, player.transform.position) > 0.9f) continue;
                Level.Signs.Remove(pebbles[i]);
                pebbles.RemoveAt(i);
                stones++;
                effects.pitch = 0.6f;
                effects.PlayOneShot(pickup, 0.6f);
                MarkerSender.Send("stone", stones);
            }
        }

        // Rzut: przytrzymany lewy przycisk kołysze siłę i rysuje tor, puszczenie rzuca.
        void UpdateThrow()
        {
            var mouse = Mouse.current;
            var gamepad = Gamepad.current;
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            // lewy przycisk myszy albo prawy spust pada; kliknięcie, które dopiero łapie kursor, nie zaczyna rzutu
            bool pressed = (mouse != null && mouse.leftButton.wasPressedThisFrame && lockedBefore)
                        || (gamepad != null && gamepad.rightTrigger.wasPressedThisFrame);
            bool holding = (mouse != null && mouse.leftButton.isPressed) || (gamepad != null && gamepad.rightTrigger.isPressed);
            if (pressed && stones > 0 && !flying)
            {
                aiming = true;
                aimAt = Time.time;
            }
            lockedBefore = locked;

            if (aiming)
            {
                float swing = Mathf.PingPong((Time.time - aimAt) / ThrowSwing, 1f);
                var eyes = Camera.main.transform;
                Vector3 direction = Quaternion.AngleAxis(-ThrowLift, eyes.right) * eyes.forward;
                Vector3 from = eyes.position + eyes.forward * 0.3f - eyes.up * 0.15f + eyes.right * 0.15f;
                Vector3 velocity = direction * Mathf.Lerp(ThrowSlow, ThrowFast, swing);

                if (!holding)
                {
                    aiming = false;
                    flying = true;
                    stones--;
                    rockAt = from;
                    rockVelocity = velocity;
                    MarkerSender.Send("throw", velocity.magnitude);
                }
                else
                {
                    // tor do pierwszej przeszkody; na końcu krążek w miejscu upadku
                    line.Clear();
                    Vector3 at = from, speed = velocity, normal = Vector3.up;
                    for (int i = 0; i < 90; i++)
                    {
                        Vector3 next = at + speed * 0.035f;
                        speed += Vector3.down * ThrowGravity * 0.035f;
                        if (Physics.Linecast(at, next, out var hit))
                        {
                            at = hit.point;
                            normal = hit.normal;
                            break;
                        }
                        at = next;
                        if (i >= 5) line.Add(at);   // punkty tuż przy oku zasłaniałyby pół ekranu
                    }
                    Vector3 side = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.7f ? Vector3.right : Vector3.up).normalized, other = Vector3.Cross(normal, side);
                    for (int i = 0; i < 16; i++)
                        line.Add(at + normal * 0.03f + (side * Mathf.Cos(i * Mathf.PI / 8f) + other * Mathf.Sin(i * Mathf.PI / 8f)) * 0.22f);
                    marks.Trail(line, new Color(0.75f, 1.5f, 1.9f), 0.05f);
                }
            }

            if (flying)
            {
                Vector3 next = rockAt + rockVelocity * Time.deltaTime;
                rockVelocity += Vector3.down * ThrowGravity * Time.deltaTime;
                line.Clear();
                line.Add(rockAt);
                line.Add((rockAt + next) / 2f);
                line.Add(next);
                marks.Trail(line, new Color(1.2f, 2.2f, 2.6f), 0.35f);
                if (Physics.Linecast(rockAt, next, out var hit) || next.y < -1f)
                {
                    flying = false;
                    Vector3 point = hit.collider != null ? hit.point : next;
                    AudioSource.PlayClipAtPoint(clack, point, 1f);
                    Vector3 normal = hit.collider != null ? hit.normal : Vector3.up;
                    echoes.Burst(point, normal, new Color(1.2f, 2.2f, 2.6f), 2.5f);
                    // uderzenie odsłania okolicę jak mały skan (tyle, co sekunda zamkniętych oczu);
                    // strażnicy słyszą sam kamień, nie ten skan
                    Physics.SyncTransforms();
                    cloud.Scan(point + normal * 0.3f, ScanRadius(RockScanSeconds), surfaces, Level.Signs);
                    Noise(point, RockNoise, true);
                    MarkerSender.Send("rock");
                }
                rockAt = next;
            }
        }

        void CheckExit()
        {
            if (FlatDistance(Level.ExitPosition, player.transform.position) > 1.3f) return;
            if (loot.Count > 0)
            {
                // drzwi nie puszczają bez całego łupu: dźwięk i mignięcie brakujących rombów
                if (Time.time - deniedAt > 2.5f)
                {
                    deniedAt = Time.time;
                    effects.pitch = 1f;
                    effects.PlayOneShot(deny);
                }
                return;
            }
            End(Phase.Won, win, "ESCAPED", "Close your eyes for a moment to return to the doors.", "win");
        }

        void CheckGuards()
        {
            foreach (var guard in guards)
            {
                if (FlatDistance(guard.transform.position, player.transform.position) > CatchDistance) continue;
                End(Phase.Caught, caught, "BREATHE", "Close your eyes for a moment to try again.", "caught");
                return;
            }
        }

        void End(Phase result, AudioClip sound, string heading, string text, string marker)
        {
            phase = result;
            player.frozen = true;
            foreach (var guard in guards) guard.enabled = false;
            effects.pitch = 1f;
            effects.PlayOneShot(sound);
            title.text = Spaced(heading);
            // chwila refleksji: ile razy gracz się zatrzymał i na jak długo
            body.text = text + "\n\n" + (pauses > 0
                ? $"Pauses  {pauses}        Longest  {longestPause:0.0} s"
                : "No pauses taken.");
            footer.text = "";
            curtainTarget = 0.82f;
            MarkerSender.Send(marker);
        }

        // ---------- ekran ----------

        void UpdateHud()
        {
            // kurtyna i jej tekst gasną i wracają razem
            float alpha = Mathf.MoveTowards(curtain.color.a, curtainTarget, Time.deltaTime * 1.4f);
            curtain.color = new Color(0f, 0f, 0f, alpha);
            float text = Mathf.Clamp01(alpha / Mathf.Max(curtainTarget, 0.5f));
            SetAlpha(title, text);
            SetAlpha(body, text);
            SetAlpha(footer, text);

            bool denied = Time.time - deniedAt < 0.6f;
            int have = lootTotal - loot.Count;
            for (int i = 0; i < pips.Count; i++)
            {
                pips[i].enabled = phase == Phase.Play;
                pips[i].color = i < have ? new Color(1f, 0.82f, 0.25f) : denied ? new Color(0.9f, 0.25f, 0.2f) : new Color(1f, 1f, 1f, 0.16f);
            }

            for (int i = 0; i < dots.Count; i++)
            {
                dots[i].enabled = phase == Phase.Play && i < stones;
                dots[i].rectTransform.anchoredPosition = new Vector2((i - (stones - 1) / 2f) * 12f, 11f);
            }

            if (Time.time > toastUntil) toast.text = "";
            if (!showDebug) return;
            status.text = $"{Level.Current.title}  loot {have}/{lootTotal}  {phase}\n" + (keyboardMode
                ? "KEY mode: space controls, EEG is only recorded"
                : BridgeAlive ? "EEG mode: headset controls, space logs ground truth"
                              : "EEG mode: no BCI pipeline, space controls") +
                "\nF1 debug   F2 KEY / EEG   1-3 level   R retry";
            ShowSignals();
        }

        static void SetAlpha(Graphic graphic, float alpha)
        {
            Color color = graphic.color;
            color.a = alpha;
            graphic.color = color;
        }

        void ShowSignals()
        {
            builder.Clear();
            builder.AppendLine("SIGNALS");
            foreach (var signal in SignalHub.All)
            {
                bool fresh = SignalHub.TryGet(signal.name, out float value);
                builder.Append(signal.name).Append("  ").Append(fresh ? value.ToString("+0.00;-0.00") : "–")
                    .Append("  ").AppendLine(signal.source);
            }
            foreach (var binding in bindings.config.bindings)
            {
                builder.Append(binding.action).Append(" ← ").Append(binding.source ?? binding.signal).Append("  ");
                builder.AppendLine(binding.live ? binding.value.ToString("+0.00;-0.00") : "no signal");
            }
            // klatki na sekundę: wolna pętla gry opóźnia reakcję na sygnał z opaski
            fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
            builder.Append("fps ").AppendLine(fps.ToString("0"));
            builder.Append(BridgeAlive ? "BCI pipeline: " + UdpSignalSource.LastSender : "BCI pipeline: no data");
            signals.text = builder.ToString();
        }
    }
}
