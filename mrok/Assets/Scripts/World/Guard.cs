using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mrok.World
{
    // Strażnik chodzi po trasie patrolu. Słyszy kroki gracza: idzie w to miejsce, nasłuchuje
    // chwilę i wraca na trasę. Widzi tylko to, co ma przed sobą i niezbyt daleko: gdy zobaczy
    // gracza, goni go, a gdy straci go z oczu, sprawdza miejsce, w którym widział go ostatnio.
    // Skan zapisuje go w pozie, w jakiej akurat był: w pół kroku albo rozglądającego się.
    public class Guard : MonoBehaviour
    {
        public enum State { Patrol, Investigate, Listen, Chase }

        const float PatrolSpeed = 1.3f, AlertSpeed = 2.7f;
        const float StepLength = 0.8f;
        const float ListenSeconds = 3.5f, LoudListenSeconds = 7f;   // przy rzuconym kamieniu szuka dłużej
        const float SightRange = 10f, SightAngle = 55f;   // jest ciemno: widzi blisko i tylko przed sobą
        const float NoticeSeconds = 0.3f;                 // tyle musi patrzeć, zanim ruszy w pogoń

        public State state;
        public Vector2Int[] route;
        public AudioClip stepClip, alertClip;
        public Player prey;                      // gracz

        public Action<Guard, Vector3> Stepped;   // gdzie stanęła stopa

        List<Vector3> path = new List<Vector3>();
        int routeIndex, pathIndex;
        float sinceStep, listenLeft, listenFor, phase, stride, noticed;
        float? post;                             // wartownik: kąt, w który patrzy na posterunku
        bool leftFoot;
        AudioSource voice;
        Figure figure;

        public void Build(Vector2Int[] patrol, Dictionary<Collider, Surface> surfaces, AudioClip step, AudioClip alert, float? facing = null)
        {
            route = patrol;
            post = facing;
            if (facing.HasValue) transform.rotation = Quaternion.Euler(0f, facing.Value, 0f);
            stepClip = step;
            alertClip = alert;
            transform.position = Level.Center(route[0]);

            figure = new Figure(transform, surfaces, Surface.Guard, true);
            figure.Walk(0f, 0f, 0f);

            voice = gameObject.AddComponent<AudioSource>();
            voice.spatialBlend = 1f;
            voice.rolloffMode = AudioRolloffMode.Linear;
            voice.minDistance = 1.5f;
            voice.maxDistance = 24f;
            voice.playOnAwake = false;
            NextRoutePoint();
        }

        void NextRoutePoint()
        {
            state = State.Patrol;
            routeIndex = (routeIndex + 1) % route.Length;
            Go(Level.Center(route[routeIndex]));
        }

        void Go(Vector3 target)
        {
            path = Level.Path(transform.position, target);
            pathIndex = 0;
            // cel w tym samym polu: droga po polach jest pusta, więc dochodzi prosto do środka
            if (path.Count == 0 && Vector3.Distance(transform.position, target) > 0.05f) path.Add(target);
        }

        public bool Alerted => state == State.Investigate || state == State.Chase;

        // Wolna linia od oczu do głowy albo tułowia gracza, w stożku przed twarzą. Gracz w kucki
        // jest niższy, więc skrzynia, ławka czy stół zasłaniają go w całości.
        bool Sees()
        {
            if (prey == null) return false;
            Vector3 eyes = figure.head.position + Vector3.up * 0.14f, feet = prey.transform.position;
            Vector3 to = feet - eyes, facing = figure.head.forward;
            to.y = 0f;
            if (to.magnitude > SightRange || Vector3.Angle(new Vector3(facing.x, 0f, facing.z), to) > SightAngle) return false;
            return Clear(eyes, feet + Vector3.up * (prey.Height - 0.08f)) || Clear(eyes, feet + Vector3.up * prey.Height * 0.55f);
        }

        bool Clear(Vector3 eyes, Vector3 target)
        {
            Vector3 to = target - eyes;
            foreach (var hit in Physics.RaycastAll(eyes, to.normalized, to.magnitude))
                if (!hit.transform.IsChildOf(transform)) return false;   // własne ręce i daszek nie zasłaniają
            return true;
        }

        // loud: rzucony kamień. Wartownik nie schodzi z posterunku za samymi krokami.
        public void Hear(Vector3 noise, bool loud = false)
        {
            if (state == State.Chase) return;   // kto widzi, nie nasłuchuje
            if (post.HasValue && !loud) return;
            listenFor = loud ? LoudListenSeconds : ListenSeconds;
            if (state == State.Patrol) voice.PlayOneShot(alertClip, 0.8f);
            state = State.Investigate;
            Go(noise);
            path.Add(new Vector3(noise.x, 0f, noise.z));   // ostatni odcinek: dokładnie tam, skąd doszedł hałas
        }

        void Update()
        {
            if (voice == null || figure == null) return;   // po przeładowaniu skryptów gra buduje strażników od nowa
            float dt = Time.deltaTime;

            noticed = Sees() ? noticed + dt : 0f;
            if (noticed >= NoticeSeconds)
            {
                if (state != State.Chase) voice.PlayOneShot(alertClip, 1f);
                state = State.Chase;
                path.Clear();
                path.Add(new Vector3(prey.transform.position.x, 0f, prey.transform.position.z));   // prosto na gracza, skoro go widzi
                pathIndex = 0;
            }
            else if (state == State.Chase && noticed == 0f)
            {
                listenFor = ListenSeconds;
                state = State.Investigate;   // zgubił go: dobiega tam, gdzie widział go ostatnio
            }

            if (state == State.Listen)
            {
                // stoi i rozgląda się na boki
                listenLeft -= dt;
                stride = Mathf.MoveTowards(stride, 0f, 3f * dt);
                figure.Walk(phase, stride, 0f);
                float look = Mathf.Sin((listenFor - listenLeft) * 1.9f);
                figure.chest.localRotation = Quaternion.Euler(0f, look * 20f, 0f);
                figure.head.localRotation = Quaternion.Euler(0f, look * 25f, 0f);
                if (listenLeft <= 0f) NextRoutePoint();
                return;
            }

            if (pathIndex >= path.Count)
            {
                if (state != State.Patrol)
                {
                    state = State.Listen;
                    if (listenFor <= 0f) listenFor = ListenSeconds;   // po pogoni
                    listenLeft = listenFor;
                }
                else if (post.HasValue && Vector3.Distance(transform.position, Level.Center(route[0])) < 0.05f)
                {
                    // wartownik na posterunku: stoi i patrzy w swoją stronę
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0f, post.Value, 0f), 200f * dt);
                    stride = Mathf.MoveTowards(stride, 0f, 3f * dt);
                    figure.head.localRotation = Quaternion.identity;
                    figure.Walk(phase, stride, 0f);
                }
                else NextRoutePoint();
                return;
            }

            bool alert = Alerted;
            Vector3 before = transform.position;
            transform.position = Vector3.MoveTowards(before, path[pathIndex], (alert ? AlertSpeed : PatrolSpeed) * dt);
            if (transform.position == path[pathIndex]) pathIndex++;

            Vector3 moved = transform.position - before;
            if (moved.sqrMagnitude > 1e-8f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(moved), (alert ? 540f : 300f) * dt);
            phase += moved.magnitude / StepLength * Mathf.PI;
            stride = Mathf.MoveTowards(stride, alert ? 1f : 0.55f, 3f * dt);
            figure.head.localRotation = Quaternion.identity;
            figure.Walk(phase, stride, alert ? 9f : 0f);

            sinceStep += moved.magnitude;
            if (sinceStep >= StepLength)
            {
                sinceStep = 0f;
                leftFoot = !leftFoot;
                voice.pitch = UnityEngine.Random.Range(0.85f, 1.1f);
                voice.PlayOneShot(stepClip, alert ? 1f : 0.7f);
                Stepped?.Invoke(this, transform.position + transform.right * (leftFoot ? -0.11f : 0.11f));
            }
        }
    }
}
