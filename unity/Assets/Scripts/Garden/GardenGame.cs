using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Ogrod.Brain;
using Ogrod.Music;

namespace Ogrod
{
    // Ogród dźwięków: buduje scenę z kodu i prowadzi rozgrywkę.
    //   Ogród     – swobodne słuchanie i komponowanie; wyobrażenie ruchu ręki zmienia nastrój.
    //   Poznaj    – gracz słucha motywu rośliny.
    //   Rozpoznaj – motyw pojawia się wśród podobnych sekwencji (auditory oddball);
    //               gracz reaguje, gdy go usłyszy (P300 albo Spacja).
    //   Nagroda   – roślina rośnie, a jej warstwa dołącza do kompozycji.
    // Obraz renderuje się w 320x180 i jest powiększany bez wygładzania (pixel art);
    // cała sceneria porusza się według wspólnego zegara muzyki.
    public class GardenGame : MonoBehaviour
    {
        enum State { Garden, Learn, Recognize, Reward }

        const int HitsToGrow = 3;
        const float TargetChance = 0.35f;
        const float ResponseGrace = 0.6f;     // ile po końcu sekwencji wciąż liczymy odpowiedź
        const float TrialGap = 0.7f;
        const float MoodSeconds = 2.5f;       // powolne przejścia, jak dynamika sygnału EEG
        const float MoodHoldSeconds = 4f;
        const float DuckedGain = 0.15f;       // podkład nie zagłusza bodźców zadania
        const float ScheduleAhead = 1.0f;     // ile przed granicą frazy dyrygent planuje następną
        const int GroundPx = -56;             // linia ziemi w pikselach od środka ekranu
        const float GroundY = GroundPx / (float)PixelArt.PPU;

        static readonly int BeatTimeId = Shader.PropertyToID("_BeatTime");
        static readonly int BeatPulseId = Shader.PropertyToID("_BeatPulse");
        static readonly int EnergyId = Shader.PropertyToID("_Energy");
        static readonly int MoodId = Shader.PropertyToID("_Mood");
        static readonly int MoodColorId = Shader.PropertyToID("_MoodColor");
        static readonly int SwayId = Shader.PropertyToID("_Sway");
        static readonly int PhaseId = Shader.PropertyToID("_Phase");
        static readonly int WaveXId = Shader.PropertyToID("_WaveX");

        MusicBankData bank;
        // nie readonly: lista ma przetrwać przeładowanie skryptów w trybie Play
        List<Plant> plants = new List<Plant>();

        // zegar muzyki i dyrygent
        double loopStartDsp, nextPhraseDsp, smoothDsp;
        float phraseSeconds;
        int phraseIndex;
        float energy;

        Camera cam;
        RenderTexture lowRes;
        RectTransform viewRect;
        Material spriteMaterial, skyMaterial;
        Sprite unitSprite;
        AudioSource stimulus, feedback, ambient;
        AudioClip hitClip, softClip;
        ParticleSystem fireflies;
        SpriteRenderer moodOverlay;
        Text titleText, statusText, controlsText, eegText;

        State state = State.Garden;
        Coroutine lesson;
        Plant active;
        int hits;
        bool selectFlag;
        float mood, moodTarget, moodHoldUntil;   // -1 = chłodny/moll, +1 = ciepły/dur
        float focus = 0.5f, gain = 1f;
        string status = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<GardenGame>() == null)
                new GameObject("Ogród dźwięków").AddComponent<GardenGame>();
        }

        // pola bez serializacji giną przy przeładowaniu skryptów w trybie Play; po tym znaku
        // poznajemy, że scenę trzeba zbudować od nowa
        [System.NonSerialized] bool built;

        void Awake()
        {
            Build();
        }

        void Build()
        {
            bank = MusicBank.Load();
            var spriteShader = Resources.Load<Shader>("Shaders/PixelSprite");
            var skyShader = Resources.Load<Shader>("Shaders/PixelSky");
            if (bank == null || spriteShader == null || skyShader == null)
            {
                Debug.LogError("[Ogród] Brak banku muzyki albo szaderów w Resources");
                enabled = false;
                return;
            }
            spriteMaterial = new Material(spriteShader);
            skyMaterial = new Material(skyShader);
            unitSprite = PixelArt.Unit();

            BuildCamera();
            BuildScenery();
            BuildPlants();
            BuildAudio();
            BuildFireflies();
            BuildHud();

            gameObject.AddComponent<KeyboardBrainSimulator>();
            gameObject.AddComponent<UdpBrainReceiver>();
            MarkerSender.Send("session_start");
            built = true;
        }

        // Po przeładowaniu skryptów w trakcie gry: buduje scenę od nowa, zachowując poziomy roślin.
        void Rebuild()
        {
            var levels = plants.ConvertAll(p => p != null ? p.level : 0);
            var mutes = plants.ConvertAll(p => p != null && p.muted);
            for (int i = transform.childCount - 1; i >= 0; i--) DestroyImmediate(transform.GetChild(i).gameObject);
            foreach (var component in GetComponents<Component>())
                if (component != this && !(component is Transform)) DestroyImmediate(component);
            if (lowRes != null) lowRes.Release();
            plants = new List<Plant>();
            lesson = null;
            state = State.Garden;
            status = "";

            Build();
            if (!built) return;
            for (int i = 0; i < plants.Count && i < levels.Count; i++)
            {
                plants[i].muted = mutes[i];
                if (levels[i] > 0) SetLevel(plants[i], levels[i]);
            }
        }

        void OnEnable()
        {
            BrainInput.Select += OnSelect;
            BrainInput.Left += OnLeft;
            BrainInput.Right += OnRight;
            if (!built && plants != null && plants.Count > 0) Rebuild();
        }

        void OnDisable()
        {
            BrainInput.Select -= OnSelect;
            BrainInput.Left -= OnLeft;
            BrainInput.Right -= OnRight;
        }

        void OnDestroy()
        {
            if (cam != null) cam.targetTexture = null;
            if (lowRes != null) lowRes.Release();
        }

        void OnSelect() { selectFlag = true; }
        void OnLeft() { moodTarget = -1f; moodHoldUntil = Time.time + MoodHoldSeconds; }
        void OnRight() { moodTarget = 1f; moodHoldUntil = Time.time + MoodHoldSeconds; }

        // ---------- budowa sceny ----------

        void BuildCamera()
        {
            cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }
            cam.orthographic = true;
            cam.orthographicSize = PixelArt.ScreenH / 2f / PixelArt.PPU;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.backgroundColor = Palette.SkyTop;
            cam.clearFlags = CameraClearFlags.SolidColor;

            // gra rysuje się w niskiej rozdzielczości; HUD pokazuje ten obraz powiększony
            lowRes = new RenderTexture(PixelArt.ScreenW, PixelArt.ScreenH, 24)
            {
                name = "Ogród 320x180",
                filterMode = FilterMode.Point
            };
            lowRes.Create();
            cam.targetTexture = lowRes;

            // druga kamera rysuje sam ekran (czarne tło pod HUD-em)
            var screen = new GameObject("Kamera ekranu").AddComponent<Camera>();
            screen.transform.SetParent(transform, false);
            screen.cullingMask = 0;
            screen.clearFlags = CameraClearFlags.SolidColor;
            screen.backgroundColor = Color.black;
            screen.depth = cam.depth + 1f;

            if (FindAnyObjectByType<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();
        }

        static float Px(float pixels)
        {
            return pixels / PixelArt.PPU;
        }

        SpriteRenderer AddSprite(string name, Sprite sprite, Transform parent, Vector3 position, int order,
            float sway = 0f, float phase = 0f, float waveX = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sharedMaterial = spriteMaterial;
            renderer.sortingOrder = order;
            if (sway != 0f)
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                block.SetFloat(SwayId, sway);
                block.SetFloat(PhaseId, phase);
                block.SetFloat(WaveXId, waveX);
                renderer.SetPropertyBlock(block);
            }
            return renderer;
        }

        void BuildScenery()
        {
            var root = new GameObject("Sceneria").transform;
            root.SetParent(transform, false);
            var screenSize = new Vector3(Px(PixelArt.ScreenW), Px(PixelArt.ScreenH), 1f);

            var sky = AddSprite("Niebo", unitSprite, root, Vector3.zero, -20);
            sky.transform.localScale = screenSize;
            skyMaterial.SetColor("_Top", Palette.SkyTop);
            skyMaterial.SetColor("_Mid", Palette.SkyMid);
            skyMaterial.SetColor("_Horizon", Palette.SkyHorizon);
            skyMaterial.SetColor("_Moon", Palette.Moon);
            skyMaterial.SetColor("_Aurora", Palette.Aurora);
            sky.sharedMaterial = skyMaterial;

            const int farBase = 46, farAmp = 10, nearBase = 22, nearAmp = 6;
            AddSprite("Wzgórza dalekie", PixelArt.Hills(1, Palette.HillFar, Palette.HillFarEdge, farBase, farAmp), root, new Vector3(0f, GroundY), -16);
            AddSprite("Wzgórza bliskie", PixelArt.Hills(2, Palette.HillNear, Palette.HillNearEdge, nearBase, nearAmp), root, new Vector3(0f, GroundY), -12);

            // drzewa tła stoją na bliskich wzgórzach; korony kołyszą się w rytm, każda z inną fazą
            int[] treeX = { -148, -112, -70, -34, 18, 52, 96, 138 };
            for (int i = 0; i < treeX.Length; i++)
            {
                bool conifer = i % 3 == 1;
                int hill = PixelArt.HillHeight(2, treeX[i] + PixelArt.ScreenW / 2, nearBase, nearAmp);
                var tree = AddSprite(conifer ? "Świerk" : "Drzewo", PixelArt.BackgroundTree(i * 7 + 3, conifer), root,
                    new Vector3(Px(treeX[i]), GroundY + Px(hill - 3)), -14, conifer ? 2f : 3f, i * 0.9f);
                tree.color = new Color(0.85f, 0.85f, 0.95f, 1f);
            }

            AddSprite("Ziemia", PixelArt.Ground(), root, new Vector3(0f, GroundY + Px(2)), -5);
            AddSprite("Trawa", PixelArt.Grass(), root, new Vector3(0f, GroundY + Px(1)), 4, 2f, 0f, 30f);

            moodOverlay = AddSprite("Nastrój", unitSprite, root, Vector3.zero, 8);
            moodOverlay.transform.localScale = screenSize;
            moodOverlay.color = Color.clear;
        }

        void BuildPlants()
        {
            int count = bank.voices.Length;
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Roślina " + bank.voices[i].label);
                go.transform.SetParent(transform, false);
                float x = count == 1 ? 0f : Mathf.Lerp(-98f, 98f, i / (float)(count - 1));
                go.transform.position = new Vector3(Px(Mathf.Round(x)), GroundY, 0f);
                var plant = go.AddComponent<Plant>();
                plant.Build(bank.voices[i], i, Palette.Plants[i % Palette.Plants.Length], spriteMaterial);
                plants.Add(plant);
            }
        }

        void BuildAudio()
        {
            stimulus = AddSource(false);
            feedback = AddSource(false);
            ambient = AddSource(true);
            phraseSeconds = bank.TotalBeats * bank.BeatSeconds;
            loopStartDsp = AudioSettings.dspTime + 0.3;
            nextPhraseDsp = loopStartDsp + phraseSeconds;
            smoothDsp = AudioSettings.dspTime;

            // stały, cichy ambient: tonika i kwinta pasują i do dur, i do moll
            var drone = new List<Note>
            {
                new Note { degree = 0, beat = 0f, length = bank.TotalBeats },
                new Note { degree = 4, beat = 0f, length = bank.TotalBeats }
            };
            ambient.clip = Synth.Render("ambient", drone, "pad", 3, false, bank.BeatSeconds, bank.TotalBeats, true, 0.12f);
            ambient.PlayScheduled(loopStartDsp);

            hitClip = Synth.Render("trafienie", MusicBank.MotifNotes(new[] { 4, 7, 9 }), "bell", 6, false, 0.2f, 1.5f, false, 0.18f);
            softClip = Synth.Render("pudło", MusicBank.MotifNotes(new[] { 0 }), "bass", 3, false, 0.5f, 0.5f, false, 0.2f);
        }

        AudioSource AddSource(bool loop)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            return source;
        }

        void BuildFireflies()
        {
            var go = new GameObject("Świetliki");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(0f, 0f, 0f);
            fireflies = go.AddComponent<ParticleSystem>();

            var main = fireflies.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(Px(1), Px(2));   // 1–2 piksele
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 400;

            var shape = fireflies.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(17f, 6f, 0.1f);

            var fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0f, 1f) });
            var colorOverLifetime = fireflies.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = fade;

            var noise = fireflies.noise;
            noise.enabled = true;
            noise.strength = 0.25f;
            noise.frequency = 0.3f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            var particleMaterial = new Material(spriteMaterial);
            particleMaterial.SetTexture("_MainTex", unitSprite.texture);
            renderer.sharedMaterial = particleMaterial;
            renderer.sortingOrder = 6;
        }

        void BuildHud()
        {
            var canvasGo = new GameObject("HUD");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);

            // obraz gry: powiększony bez wygładzania, z zachowaniem proporcji 16:9
            var viewGo = new GameObject("Widok", typeof(RectTransform));
            viewGo.transform.SetParent(canvasGo.transform, false);
            var view = viewGo.AddComponent<RawImage>();
            view.texture = lowRes;
            view.raycastTarget = false;
            viewRect = view.rectTransform;
            var fitter = viewGo.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = PixelArt.ScreenW / (float)PixelArt.ScreenH;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleText = MakeText(canvasGo.transform, font, TextAnchor.UpperCenter, 30);
            statusText = MakeText(canvasGo.transform, font, TextAnchor.UpperCenter, 20);
            statusText.rectTransform.offsetMax = new Vector2(-24f, -64f);
            controlsText = MakeText(canvasGo.transform, font, TextAnchor.LowerLeft, 15);
            eegText = MakeText(canvasGo.transform, font, TextAnchor.LowerRight, 15);
        }

        static Text MakeText(Transform parent, Font font, TextAnchor anchor, int size)
        {
            var go = new GameObject("Tekst", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Palette.Text;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(24f, 16f);
            rect.offsetMax = new Vector2(-24f, -16f);
            return text;
        }

        // ---------- pętla ----------

        void Update()
        {
            HandleGameKeys();
            if (state == State.Garden && selectFlag)
            {
                // sam sygnał "select" wystarcza, żeby zacząć kolejną lekcję — da się grać bez myszy
                selectFlag = false;
                var next = plants.Find(p => p.level < Plant.MaxLevel);
                if (next != null) StartLesson(next);
            }

            if (Time.time > moodHoldUntil) moodTarget = 0f;
            mood = Mathf.MoveTowards(mood, moodTarget, Time.deltaTime / MoodSeconds);
            focus = Mathf.Lerp(focus, BrainInput.Focus, Time.deltaTime * 2f);
            gain = Mathf.MoveTowards(gain, state == State.Garden || state == State.Reward ? 1f : DuckedGain, Time.deltaTime / 0.8f);

            Conduct();
            float minorMix = Mathf.Clamp01(-mood);
            float layerGain = gain * (0.8f + 0.2f * Mathf.Clamp01(mood));
            foreach (var plant in plants) plant.Tick(minorMix, layerGain, focus);
            ambient.volume = 0.5f + 0.5f * gain;

            UpdateRhythm();
            UpdateMoodVisuals();
            UpdateHud();
        }

        // Dyrygent: tuż przed granicą frazy planuje każdej roślinie następny wariant.
        void Conduct()
        {
            double now = AudioSettings.dspTime;
            while (now >= nextPhraseDsp - ScheduleAhead)
            {
                phraseIndex++;
                foreach (var plant in plants) plant.Schedule(phraseIndex, nextPhraseDsp);
                nextPhraseDsp += phraseSeconds;
            }
        }

        public void SetLevel(Plant plant, int level)
        {
            plant.level = Mathf.Clamp(level, 0, Plant.MaxLevel);
            double now = AudioSettings.dspTime;
            double scheduledStart = nextPhraseDsp - phraseSeconds;   // ostatnia granica, którą dyrygent już obsłużył
            bool scheduledAhead = scheduledStart > now && scheduledStart > loopStartDsp;
            double currentStart = scheduledAhead ? scheduledStart - phraseSeconds : scheduledStart;
            plant.OnLevelChanged(bank, System.Math.Max(currentStart, loopStartDsp));
            // dyrygent zaplanował już następną frazę, zanim roślina urosła — dogrywamy ją
            if (scheduledAhead) plant.Schedule(phraseIndex, scheduledStart);
        }

        // Wspólny zegar: szadery i sceneria dostają czas w uderzeniach i puls każdego uderzenia.
        void UpdateRhythm()
        {
            // dspTime skacze co bufor audio, więc wygładzamy go czasem klatki
            double dsp = AudioSettings.dspTime;
            smoothDsp += Time.unscaledDeltaTime;
            if (System.Math.Abs(smoothDsp - dsp) > 0.1) smoothDsp = dsp;
            else smoothDsp += (dsp - smoothDsp) * 0.05;

            float beats = (float)((smoothDsp - loopStartDsp) / bank.BeatSeconds);
            float phase = beats - Mathf.Floor(beats);
            int beatInBar = ((Mathf.FloorToInt(beats) % bank.beatsPerBar) + bank.beatsPerBar) % bank.beatsPerBar;
            float pulse = Mathf.Exp(-phase * 5f) * (beatInBar == 0 ? 1f : 0.6f);

            // im więcej roślin gra, tym żywiej porusza się cały ogród
            int audible = 0;
            foreach (var plant in plants)
                if (plant.Audible) audible++;
            energy = Mathf.MoveTowards(energy, gain * audible / Mathf.Max(1, plants.Count), Time.deltaTime * 0.5f);

            Shader.SetGlobalFloat(BeatTimeId, beats);
            Shader.SetGlobalFloat(BeatPulseId, pulse);
            Shader.SetGlobalFloat(EnergyId, energy);
        }

        void HandleGameKeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.escapeKey.wasPressedThisFrame && state != State.Garden) ReturnToGarden("Przerwa. Ogród gra dalej.");
                if (state == State.Garden)
                {
                    var digits = new[] { keyboard.digit1Key, keyboard.digit2Key, keyboard.digit3Key, keyboard.digit4Key };
                    for (int i = 0; i < digits.Length && i < plants.Count; i++)
                        if (digits[i].wasPressedThisFrame) StartLesson(plants[i]);
                }
            }

            var mouse = Mouse.current;
            if (mouse == null || state != State.Garden) return;
            bool left = mouse.leftButton.wasPressedThisFrame, right = mouse.rightButton.wasPressedThisFrame;
            if (!left && !right) return;

            // kamera rysuje do tekstury, więc pozycję myszy przeliczamy przez prostokąt widoku
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewRect, mouse.position.ReadValue(), null, out var local)) return;
            var rect = viewRect.rect;
            var viewport = new Vector3((local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height, 10f);
            Vector3 world = cam.ViewportToWorldPoint(viewport);

            var plant = plants.Find(p => Mathf.Abs(world.x - p.transform.position.x) < 1.3f && world.y < p.HeadPosition.y + 1.2f);
            if (plant == null) return;
            if (left) StartLesson(plant);
            else if (plant.level > 0)
            {
                plant.muted = !plant.muted;
                status = plant.muted ? $"{plant.voice.label} milczy." : $"{plant.voice.label} gra dalej.";
                MarkerSender.Send(plant.muted ? "mute" : "unmute", plant.index);
            }
        }

        void UpdateMoodVisuals()
        {
            Color moodColor = mood < 0f
                ? Color.Lerp(Palette.MoodNeutral, Palette.MoodCool, -mood)
                : Color.Lerp(Palette.MoodNeutral, Palette.MoodWarm, mood);
            Shader.SetGlobalFloat(MoodId, mood);
            Shader.SetGlobalColor(MoodColorId, moodColor);

            Color overlay = moodColor;
            overlay.a = 0.03f * Mathf.Abs(mood);   // mieszanie jest liniowe, więc to już wyraźny odcień
            moodOverlay.color = overlay;

            var main = fireflies.main;
            main.startColor = moodColor;
            var emission = fireflies.emission;
            emission.rateOverTime = Mathf.Lerp(3f, 30f, focus);   // neurofeedback: skupienie ożywia ogród
        }

        void UpdateHud()
        {
            titleText.text = "Ogród dźwięków";
            statusText.text = status;
            switch (state)
            {
                case State.Garden:
                    controlsText.text = "Kliknij roślinę lub wciśnij 1–4: poznaj jej motyw\nPrawy przycisk myszy: wycisz / włącz roślinę\n← / →: nastrój ogrodu    ↑ / ↓: skupienie";
                    break;
                case State.Learn:
                    controlsText.text = "Spacja: zacznij rozpoznawanie\nR: powtórz motyw    Esc: wróć do ogrodu";
                    break;
                case State.Recognize:
                    controlsText.text = "Spacja: to ten motyw!\nEsc: przerwa";
                    break;
                default:
                    controlsText.text = "";
                    break;
            }

            float age = Time.unscaledTime - BrainInput.LastEventTime;
            string source = age > 100f ? "czekam na sygnał" : $"{BrainInput.Source}, {age:0.0} s temu";
            string moodName = mood < -0.2f ? "chłodny (lewa ręka)" : mood > 0.2f ? "ciepły (prawa ręka)" : "neutralny";
            eegText.text = $"Wejście BCI: {source}\nSkupienie: {Mathf.RoundToInt(focus * 100f)}%    Nastrój: {moodName}";
        }

        // ---------- lekcja ----------

        void StartLesson(Plant plant)
        {
            if (lesson != null) StopCoroutine(lesson);
            active = plant;
            selectFlag = false;
            lesson = StartCoroutine(Lesson(plant));
        }

        void ReturnToGarden(string message)
        {
            if (lesson != null) StopCoroutine(lesson);
            lesson = null;
            active = null;
            stimulus.Stop();
            selectFlag = false;
            state = State.Garden;
            status = message;
            MarkerSender.Send("garden");
        }

        IEnumerator Lesson(Plant plant)
        {
            // poziom trudności rośnie z poziomem rośliny; w pełni rozwinięta roślina służy do ćwiczeń
            int difficulty = Mathf.Min(plant.level, Plant.MaxLevel - 1);
            float motifSeconds = plant.motif.Length * MusicBank.MotifNoteBeats * bank.BeatSeconds;

            state = State.Learn;
            MarkerSender.Send("learn", plant.index);
            status = $"{plant.voice.label}: posłuchaj motywu.";
            yield return new WaitForSeconds(0.8f);   // podkład zdąży ucichnąć

            bool replay = true;
            selectFlag = false;
            while (!selectFlag)
            {
                if (replay)
                {
                    replay = false;
                    for (int i = 0; i < 2; i++)
                    {
                        PlayStimulus(plant, plant.motif, plant.voice, "pattern");
                        yield return new WaitForSeconds(motifSeconds + 0.6f);
                    }
                    status = $"{plant.voice.label}: gdy zapamiętasz motyw, wciśnij Spację.";
                }
                var keyboard = Keyboard.current;
                if (keyboard != null && keyboard.rKey.wasPressedThisFrame) replay = true;
                yield return null;
            }

            state = State.Recognize;
            hits = 0;
            int sinceTarget = 1;
            MarkerSender.Send("recognize", plant.index);
            while (hits < HitsToGrow)
            {
                status = $"Zareaguj, gdy usłyszysz motyw.   {Progress()}";
                yield return new WaitForSeconds(TrialGap);

                bool isTarget = sinceTarget >= 3 || (sinceTarget >= 1 && Random.value < TargetChance);
                sinceTarget = isTarget ? 0 : sinceTarget + 1;
                // najwyższy poziom: motyw trzeba rozpoznać także w barwie innego instrumentu
                var timbre = difficulty >= 2 ? plants[Random.Range(0, plants.Count)].voice : plant.voice;
                PlayStimulus(plant, isTarget ? plant.motif : MakeDistractor(plant.motif, difficulty), timbre,
                    isTarget ? "target" : "distractor");

                selectFlag = false;
                bool responded = false;
                float windowEnd = Time.time + motifSeconds + ResponseGrace;
                while (Time.time < windowEnd)
                {
                    if (selectFlag && !responded)
                    {
                        responded = true;
                        if (isTarget) Hit(plant);
                        else FalseAlarm(plant);
                    }
                    selectFlag = false;
                    yield return null;
                }
                if (isTarget && !responded)
                {
                    MarkerSender.Send("miss", plant.index);
                    status = $"To był ten motyw. Nic straconego, pojawi się znowu.   {Progress()}";
                    yield return new WaitForSeconds(1.2f);
                }
            }

            state = State.Reward;
            if (plant.level < Plant.MaxLevel)
            {
                plant.muted = false;
                SetLevel(plant, plant.level + 1);
                status = $"{plant.voice.label} rośnie i dołącza do muzyki ogrodu.";
            }
            else status = $"{plant.voice.label} gra już pełnym głosem. Dobre ćwiczenie!";
            MarkerSender.Send("grow", plant.level);
            Burst(plant.HeadPosition, Palette.Plants[plant.index % Palette.Plants.Length], 70);
            plant.Pulse(1f);
            yield return new WaitForSeconds(5f);

            lesson = null;
            ReturnToGarden(plants.TrueForAll(p => p.level > 0)
                ? "Cały ogród gra. Słuchaj, wyciszaj rośliny i zmieniaj nastrój."
                : "Wybierz kolejną roślinę.");
        }

        string Progress()
        {
            return new string('●', hits) + new string('○', HitsToGrow - hits);
        }

        void PlayStimulus(Plant plant, int[] degrees, VoiceData timbre, string marker)
        {
            float totalBeats = degrees.Length * MusicBank.MotifNoteBeats;
            stimulus.clip = Synth.Render("bodziec", MusicBank.MotifNotes(degrees), timbre.instrument, timbre.octave,
                false, bank.BeatSeconds, totalBeats, false, 0.3f);
            stimulus.Play();
            MarkerSender.Send(marker, plant.index);
            plant.Pulse(0.8f);
        }

        void Hit(Plant plant)
        {
            hits++;
            MarkerSender.Send("hit", plant.index);
            feedback.PlayOneShot(hitClip);
            Burst(plant.HeadPosition, Palette.Plants[plant.index % Palette.Plants.Length], 25);
            plant.Pulse(1f);
            status = $"Tak, to ten motyw!   {Progress()}";
        }

        void FalseAlarm(Plant plant)
        {
            MarkerSender.Send("false_alarm", plant.index);
            feedback.PlayOneShot(softClip);
            status = $"To była inna sekwencja. Słuchaj dalej.   {Progress()}";
        }

        void Burst(Vector3 position, Color color, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var emit = new ParticleSystem.EmitParams
                {
                    position = position,
                    applyShapeToPosition = false,
                    velocity = Random.insideUnitCircle * Random.Range(0.5f, 3f),
                    startColor = color,
                    startSize = Px(Random.Range(1, 3)),
                    startLifetime = Random.Range(0.8f, 2f)
                };
                fireflies.Emit(emit, 1);
            }
        }

        // Trudność zmienia jedną rzecz naraz:
        //   0 – sekwencja wyraźnie inna, 1 – różni się jedną nutą, 2 – zamieniona kolejność dwóch nut.
        static int[] MakeDistractor(int[] motif, int difficulty)
        {
            var d = (int[])motif.Clone();
            if (difficulty <= 0)
            {
                System.Array.Reverse(d);
                for (int i = 0; i < d.Length; i++) d[i] += i % 2 == 0 ? 3 : -2;
            }
            else if (difficulty == 1)
            {
                d[Random.Range(0, d.Length)] += Random.value < 0.5f ? 1 : -1;
            }
            else
            {
                int start = Random.Range(0, d.Length - 1);
                for (int k = 0; k < d.Length - 1; k++)
                {
                    int i = (start + k) % (d.Length - 1);
                    if (d[i] == d[i + 1]) continue;
                    (d[i], d[i + 1]) = (d[i + 1], d[i]);
                    return d;
                }
                d[d.Length - 1] += 1;
            }
            return d;
        }
    }
}
