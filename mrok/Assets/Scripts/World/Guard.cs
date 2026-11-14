using System.Collections.Generic;
using UnityEngine;

namespace Mrok.World
{
    // Strażnik nie widzi w ciemności, ale słyszy. Chodzi po trasie patrolu; gdy usłyszy hałas
    // (kroki gracza, skan), idzie w to miejsce, nasłuchuje chwilę i wraca na trasę.
    public class Guard : MonoBehaviour
    {
        public enum State { Patrol, Investigate, Listen }

        const float PatrolSpeed = 1.3f, AlertSpeed = 2.7f;
        const float StepLength = 0.8f;
        const float ListenSeconds = 3.5f;

        public State state;
        public Vector2Int[] route;
        public AudioClip stepClip, alertClip;

        List<Vector3> path = new List<Vector3>();
        int routeIndex, pathIndex;
        float sinceStep, listenLeft;
        AudioSource voice;

        public void Build(Vector2Int[] patrol, Dictionary<Collider, Surface> surfaces, AudioClip step, AudioClip alert)
        {
            route = patrol;
            stepClip = step;
            alertClip = alert;
            transform.position = Level.Center(route[0]);

            // sylwetka z dwóch brył: tułów i głowa, żeby na skanie było widać człowieka
            var body = gameObject.AddComponent<CapsuleCollider>();
            body.center = new Vector3(0f, 0.8f, 0f);
            body.radius = 0.32f;
            body.height = 1.6f;
            var head = gameObject.AddComponent<SphereCollider>();
            head.center = new Vector3(0f, 1.72f, 0f);
            head.radius = 0.2f;
            surfaces[body] = Surface.Guard;
            surfaces[head] = Surface.Guard;

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
        }

        public void Hear(Vector3 noise)
        {
            if (state == State.Patrol) voice.PlayOneShot(alertClip, 0.8f);
            state = State.Investigate;
            Go(noise);
            path.Add(new Vector3(noise.x, 0f, noise.z));   // ostatni odcinek: dokładnie tam, skąd doszedł hałas
        }

        void Update()
        {
            if (voice == null) return;   // po przeładowaniu skryptów gra buduje strażników od nowa
            if (state == State.Listen)
            {
                listenLeft -= Time.deltaTime;
                if (listenLeft <= 0f) NextRoutePoint();
                return;
            }

            if (pathIndex >= path.Count)
            {
                if (state == State.Investigate)
                {
                    state = State.Listen;
                    listenLeft = ListenSeconds;
                }
                else NextRoutePoint();
                return;
            }

            float speed = state == State.Investigate ? AlertSpeed : PatrolSpeed;
            Vector3 before = transform.position;
            transform.position = Vector3.MoveTowards(before, path[pathIndex], speed * Time.deltaTime);
            if (transform.position == path[pathIndex]) pathIndex++;

            sinceStep += Vector3.Distance(before, transform.position);
            if (sinceStep >= StepLength)
            {
                sinceStep = 0f;
                voice.pitch = Random.Range(0.85f, 1.1f);
                voice.PlayOneShot(stepClip, state == State.Investigate ? 1f : 0.7f);
            }
        }
    }
}
