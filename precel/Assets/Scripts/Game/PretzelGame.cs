using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Precel.Pretzel;
using Bci.Signals;

namespace Precel.Game
{
    // Mistrzostwa w krojeniu precla na pół. Sygnał "steer" przesuwa nóż, sygnał "cut"
    // (trzymany) opuszcza go na precla. Połówki trafiają na wagę; liczy się jak najmniejsza
    // różnica masy. Zawody to pięć precli.
    public class PretzelGame : MonoBehaviour
    {
        enum Phase { Intro, Aim, Weigh, Summary }

        public const int Rounds = 5;
        const float RoundSeconds = 25f;
        const float KnifeRange = 1.25f;
        const float KnifeSpeed = 0.55f;      // jednostek na sekundę przy pełnym wychyleniu
        const float HoverY = 0.62f;
        const float DescendSeconds = 0.8f;   // tyle trzeba trzymać sygnał cięcia
        const float LookBackSeconds = 0.4f;  // cięcie bierze pozycję noża sprzed zaciśnięcia szczęki
        const float Density = 590f;          // gramów na jednostkę sześcienną: precel waży ok. 85 g
        const float HalfScale = 0.62f;
        const float FlightStart = 0.35f, FlightEnd = 1.25f;

        static readonly CultureInfo Polish = new CultureInfo("pl-PL");

        [NonSerialized] bool built;
        Phase phase;
        SignalBindings bindings;
        Hud hud;
        BalanceScale scale;
        AudioSource music, effects;
        AudioClip chop, bell, buzz;
        System.Random random;

        Transform knife, guide;
        float knifeX, knifeY, knifeVelocity;
        bool descending, forced;
        readonly Queue<(float time, float x)> knifeHistory = new Queue<(float, float)>();

        GameObject pretzel;
        PretzelBuilder.Result pretzelData;
        Material doughMaterial, saltMaterial;
        readonly List<Transform> salt = new List<Transform>();
        readonly GameObject[] halves = new GameObject[2];
        readonly Vector3[] halfFrom = new Vector3[2], halfTo = new Vector3[2];

        int round;
        float phaseTime, timeLeft, dropTime;
        float massLeft, massRight, difference;
        int points, totalPoints;
        float totalDifference;
        bool resultShown;

        // do podglądu i testów
        public int Round => round;
        public float KnifeX => knifeX;
        public float LastDifference => difference;
        public string PhaseName => phase.ToString();
        public float MassLeft => massLeft;
        public float MassRight => massRight;
        public Mesh PretzelMesh => pretzelData?.mesh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindAnyObjectByType<PretzelGame>() == null) new GameObject("Mistrzostwa").AddComponent<PretzelGame>();
        }

        // ---------- budowa sceny ----------

        // Wołane też po przeładowaniu skryptów w trakcie gry: pola spoza serializacji
        // przepadają, więc scena powstaje od nowa, a zawody wracają do ekranu startowego.
        void Build()
        {
            built = true;
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);
            random = new System.Random();
            SignalHub.Clear();   // przy wyłączonym przeładowaniu domeny zostają sygnały z poprzedniej gry

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

            BuildCamera();
            Stage.Build(transform);
            scale = new BalanceScale();
            scale.Build(transform, new Vector3(0f, 1.42f, 1.75f));
            BuildKnife();
            hud = new Hud();
            hud.Build(transform);

            doughMaterial = Stage.Mat(Color.white, null, 0.35f);
            saltMaterial = Stage.Mat(Color.white, null, 0.6f, 0.25f);

            var sources = GetComponents<AudioSource>();
            music = sources.Length > 0 ? sources[0] : gameObject.AddComponent<AudioSource>();
            effects = sources.Length > 1 ? sources[1] : gameObject.AddComponent<AudioSource>();
            music.clip = Sfx.Polka();
            music.loop = true;
            music.volume = 0.22f;
            music.Play();
            chop = Sfx.Chop();
            bell = Sfx.Bell();
            buzz = Sfx.Buzz();

            knifeX = 0f;
            knifeY = HoverY;
            Enter(Phase.Intro);
            MarkerSender.Send("session_start");
        }

        T GetOrAdd<T>() where T : Component
        {
            var component = GetComponent<T>();
            return component != null ? component : gameObject.AddComponent<T>();
        }

        void BuildCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Kamera") { tag = "MainCamera" }.AddComponent<Camera>();
                cam.gameObject.AddComponent<AudioListener>();
            }
            cam.transform.position = new Vector3(0f, 2.55f, -3.0f);
            cam.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.62f, 0.75f) - cam.transform.position);
            cam.fieldOfView = 50f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.1f);

            var sun = FindAnyObjectByType<Light>();
            if (sun == null) sun = new GameObject("Lampa").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
            sun.color = new Color(1f, 0.93f, 0.8f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
        }

        void BuildKnife()
        {
            knife = new GameObject("Nóż").transform;
            knife.SetParent(transform, false);
            var steel = Stage.Mat(new Color(0.86f, 0.88f, 0.9f), null, 1f);
            var handle = Stage.Mat(new Color(0.25f, 0.14f, 0.08f), Stage.Wood(), 0.3f);
            var rivet = Stage.Mat(new Color(0.85f, 0.7f, 0.3f), null, 0.8f);
            Stage.Box(knife, "Ostrze", new Vector3(0f, 0.13f, 0.05f), new Vector3(0.014f, 0.26f, 2.0f), steel);
            Stage.Box(knife, "Rękojeść", new Vector3(0f, 0.17f, -1.25f), new Vector3(0.07f, 0.13f, 0.62f), handle);
            for (int i = 0; i < 3; i++)
                Stage.Shape(PrimitiveType.Sphere, knife, "Nit", new Vector3(0f, 0.17f, -1.05f - 0.2f * i), new Vector3(0.085f, 0.035f, 0.035f), rivet);

            // cienka kreska na desce pod nożem, żeby było widać, gdzie spadnie
            guide = Stage.Box(transform, "Linia cięcia", Vector3.zero, new Vector3(0.012f, 0.004f, 1.95f),
                Stage.Mat(new Color(0.75f, 0.1f, 0.08f), null, 0f, 0.6f)).transform;
        }

        // ---------- przebieg zawodów ----------

        void Enter(Phase next)
        {
            phase = next;
            phaseTime = 0f;
        }

        void Update()
        {
            if (!built) Build();
            float dt = Time.deltaTime;
            phaseTime += dt;

            switch (phase)
            {
                case Phase.Intro:
                    hud.status.text = "pięć precli · liczy się najmniejsza różnica masy połówek";
                    hud.message.text = "Ustaw nóż ruchem ręki, tnij zaciśnięciem szczęki.\nZaciśnij szczękę, aby zacząć.";
                    Steer(dt);
                    if (phaseTime > 0.5f && bindings.Pressed("cut")) StartCompetition();
                    break;
                case Phase.Aim:
                    UpdateAim(dt);
                    break;
                case Phase.Weigh:
                    UpdateWeigh(dt);
                    break;
                case Phase.Summary:
                    if (phaseTime > 1.5f && bindings.Pressed("cut")) StartCompetition();
                    break;
            }

            scale.Tick(dt);
            knife.localPosition = new Vector3(knifeX, knifeY, 0f);
            guide.localPosition = new Vector3(knifeX, 0.002f, 0f);
            hud.ShowSignals(bindings);
        }

        void StartCompetition()
        {
            round = 0;
            totalPoints = 0;
            totalDifference = 0f;
            StartRound();
        }

        void StartRound()
        {
            ClearPretzel();
            round++;
            pretzelData = PretzelBuilder.Build(random);
            pretzel = new GameObject("Precel");
            pretzel.transform.SetParent(transform, false);
            pretzel.AddComponent<MeshFilter>().sharedMesh = pretzelData.mesh;
            pretzel.AddComponent<MeshRenderer>().sharedMaterial = doughMaterial;
            for (int i = 0; i < pretzelData.salt.Count; i++)
            {
                var grain = Stage.Box(pretzel.transform, "Sól", pretzelData.salt[i], Vector3.one * 0.02f, saltMaterial).transform;
                grain.localRotation = Quaternion.LookRotation(pretzelData.saltNormals[i]) * Quaternion.Euler(0f, 0f, random.Next(90));
                salt.Add(grain);
            }

            timeLeft = RoundSeconds;
            dropTime = 0f;
            descending = forced = false;
            knifeY = HoverY;
            scale.SetLoad(0f, 0f);
            hud.message.text = "";
            Enter(Phase.Aim);
            MarkerSender.Send("round_start", round);
        }

        void ClearPretzel()
        {
            if (pretzel != null) Destroy(pretzel);
            if (pretzelData != null) Destroy(pretzelData.mesh);
            pretzelData = null;
            for (int i = 0; i < 2; i++)
            {
                if (halves[i] == null) continue;
                Destroy(halves[i].GetComponent<MeshFilter>().sharedMesh);
                Destroy(halves[i]);
                halves[i] = null;
            }
            salt.Clear();
        }

        void Steer(float dt)
        {
            float wanted = bindings.Axis("steer") * KnifeSpeed;
            knifeVelocity += (wanted - knifeVelocity) * Mathf.Min(1f, dt * 5f);   // bezwładność wygładza szum sygnału
            knifeX = Mathf.Clamp(knifeX + knifeVelocity * dt, -KnifeRange, KnifeRange);
            if (Mathf.Abs(knifeX) >= KnifeRange) knifeVelocity = 0f;

            knifeHistory.Enqueue((Time.time, knifeX));
            while (knifeHistory.Count > 0 && knifeHistory.Peek().time < Time.time - LookBackSeconds) knifeHistory.Dequeue();
        }

        void UpdateAim(float dt)
        {
            // precel spada na deskę i lekko się odbija
            dropTime += dt;
            float fall = Mathf.Clamp01(dropTime / 0.45f);
            float bounce = dropTime > 0.45f ? 0.06f * Mathf.Exp(-(dropTime - 0.45f) * 9f) * Mathf.Abs(Mathf.Sin((dropTime - 0.45f) * 16f)) : 0f;
            pretzel.transform.localPosition = new Vector3(0f, 1.6f * (1f - fall * fall) + bounce, 0f);
            bool landed = dropTime > 0.6f;

            if (landed && !descending) timeLeft -= dt;
            if (timeLeft <= 0f) forced = true;   // koniec czasu: nóż spada tam, gdzie stoi
            hud.status.text = $"Precel {round}/{Rounds} · czas {Mathf.CeilToInt(Mathf.Max(0f, timeLeft))} s · punkty {totalPoints}";

            bool cutting = landed && (forced || bindings.Held("cut"));
            if (cutting)
            {
                if (!descending)
                {
                    descending = true;
                    // zaciśnięcie szczęki zakłóca EEG, więc liczy się pozycja sprzed chwili
                    if (knifeHistory.Count > 0) knifeX = knifeHistory.Peek().x;
                    knifeVelocity = 0f;
                    knifeHistory.Clear();
                    MarkerSender.Send("cut_start", knifeX);
                }
                knifeY -= HoverY / DescendSeconds * dt;
                if (knifeY <= 0f)
                {
                    knifeY = 0f;
                    Cut();
                }
            }
            else if (descending)
            {
                knifeY += HoverY / 0.4f * dt;   // puszczony sygnał: nóż wraca
                if (knifeY >= HoverY)
                {
                    knifeY = HoverY;
                    descending = false;
                }
            }
            else
            {
                Steer(dt);
            }
        }

        void Cut()
        {
            var capColor = new Color(0.97f, 0.90f, 0.72f).linear;
            MeshSlicer.Slice(pretzelData.mesh, new Vector3(knifeX, 0f, 0f), Vector3.right, capColor, out var leftMesh, out var rightMesh);
            massLeft = MeshSlicer.Volume(leftMesh) * Density;
            massRight = MeshSlicer.Volume(rightMesh) * Density;
            float total = massLeft + massRight;
            difference = total > 0f ? Mathf.Abs(massLeft - massRight) / total * 100f : 100f;
            points = Mathf.RoundToInt(100f * Mathf.Clamp01(1f - difference / 25f));
            totalPoints += points;
            totalDifference += difference;

            var meshes = new[] { leftMesh, rightMesh };
            for (int i = 0; i < 2; i++)
            {
                var pan = i == 0 ? scale.left : scale.right;
                var half = new GameObject(i == 0 ? "Połówka lewa" : "Połówka prawa");
                half.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                half.AddComponent<MeshRenderer>().sharedMaterial = doughMaterial;
                half.transform.SetParent(pan, true);
                halves[i] = half;
                halfFrom[i] = half.transform.localPosition;
                var bounds = meshes[i].bounds;
                halfTo[i] = new Vector3(-bounds.center.x * HalfScale, scale.PanY - bounds.min.y * HalfScale, -bounds.center.z * HalfScale);
            }
            foreach (var grain in salt) grain.SetParent(halves[grain.localPosition.x < knifeX ? 0 : 1].transform, true);
            Destroy(pretzel);
            pretzel = null;

            effects.PlayOneShot(chop);
            resultShown = false;
            descending = forced = false;
            Enter(Phase.Weigh);
            MarkerSender.Send("cut", knifeX);
        }

        void UpdateWeigh(float dt)
        {
            if (phaseTime > 0.25f) knifeY = Mathf.MoveTowards(knifeY, HoverY, HoverY / 0.4f * dt);

            // połówki lecą łukiem na szalki
            float u = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(FlightStart, FlightEnd, phaseTime));
            for (int i = 0; i < 2; i++)
            {
                var half = halves[i].transform;
                half.localPosition = Vector3.Lerp(halfFrom[i], halfTo[i], u) + Vector3.up * Mathf.Sin(u * Mathf.PI) * 0.5f;
                half.localScale = Vector3.one * Mathf.Lerp(1f, HalfScale, u);
            }

            if (!resultShown && phaseTime >= FlightEnd)
            {
                resultShown = true;
                scale.SetLoad(massLeft, massRight);
                bool miss = massLeft < 0.5f || massRight < 0.5f;
                effects.PlayOneShot(miss || difference > 12f ? buzz : bell);
                string verdict = miss ? "Pudło! Nóż minął precla."
                    : difference < 1f ? "Perfekt! Brezenmeister!"
                    : difference < 3f ? "Sauber! Prawie idealnie."
                    : difference < 8f ? "Passt scho. Ujdzie."
                    : "Oj. Ktoś dostanie mniejszą połowę.";
                hud.message.text = string.Format(Polish, "lewa {0:0.0} g   |   prawa {1:0.0} g\nróżnica {2:0.0}%  ·  +{3} pkt\n{4}",
                    massLeft, massRight, difference, points, verdict);
                hud.status.text = $"Precel {round}/{Rounds} · punkty {totalPoints}";
                MarkerSender.Send("result", difference);
            }

            if (phaseTime > FlightEnd + 1.2f && (bindings.Pressed("cut") || phaseTime > FlightEnd + 7f))
            {
                if (round < Rounds) StartRound();
                else ShowSummary();
            }
        }

        void ShowSummary()
        {
            ClearPretzel();
            scale.SetLoad(0f, 0f);
            int best = Mathf.Max(PlayerPrefs.GetInt("precel_best", 0), totalPoints);
            PlayerPrefs.SetInt("precel_best", best);
            string rank = totalPoints >= 450 ? "Brezenmeister (mistrz)"
                : totalPoints >= 350 ? "Geselle (czeladnik)"
                : totalPoints >= 200 ? "Lehrling (uczeń)"
                : "Zuagroaster (przyjezdny)";
            hud.status.text = "koniec zawodów";
            hud.message.text = string.Format(Polish,
                "Wynik: {0} / {1} pkt\nśrednia różnica masy: {2:0.0}%\ntytuł: {3}\nrekord: {4} pkt\n\nZaciśnij szczękę, aby zagrać jeszcze raz.",
                totalPoints, Rounds * 100, totalDifference / Rounds, rank, best);
            Enter(Phase.Summary);
            MarkerSender.Send("summary", totalPoints);
        }
    }
}
