using System.Collections.Generic;
using UnityEngine;
using ParkShift.Attraction;
using ParkShift.Vehicles;

namespace ParkShift.Guests
{
    /// <summary>
    /// Visual mirror of the aggregate GuestSim: animated Dewey guests (different hoodie
    /// colors) shuffle along queue waypoints to match QueueCount; boarded guests sit on
    /// train seats; finished riders walk out at the exit. Falls back to capsule guests
    /// if no prefab is assigned.
    /// </summary>
    public class GuestVisualManager : MonoBehaviour
    {
        public List<Transform> QueueSpots = new List<Transform>();
        public Transform SpawnPoint;
        public Transform QueueEntryPoint;   // gap in the back rail row where guests enter
        public Transform ExitPoint;
        [Tooltip("Where unloaded riders walk off (the Unload area). Round-robined so they spread out " +
                 "and never overlap the boarding queue. Falls back to ExitPoint if empty.")]
        public List<Transform> UnloadExitPoints = new List<Transform>();
        [Tooltip("Ordered waypoints unloaded guests walk along to the exit door (last node = exit / despawn). " +
                 "Falls back to a UnloadExitPoint if empty.")]
        public List<Transform> UnloadExitPath = new List<Transform>();

        [Header("Unload stand-up")]
        [Tooltip("Seconds a guest takes to rise from the seat before walking off.")]
        public float StandUpSeconds = 0.6f;
        [Tooltip("How far up the guest rises from the seated pose when standing (m).")]
        public float StandUpRise = 0.25f;
        [Tooltip("Delay added per guest so they stand and leave one after another (no clumping).")]
        public float StandUpStagger = 0.25f;
        public TrainVisual[] Trains;
        public Material GuestMaterial;            // capsule fallback only

        [Header("Dewey guests")]
        public GameObject GuestPrefab;            // Dewey.fbx, assigned by SceneBuilder
        public RuntimeAnimatorController GuestAnimController;
        public float GuestScale = 0.5f;
        public float MoveSpeed = 1.1f;
        [Tooltip("Meters/second the Walk clip represents at GuestScale (drives foot-sync).")]
        public float WalkClipSpeed = 0.26f;
        [Tooltip("Cap on walk animation speed-up — keeps Dewey's legs from blurring.")]
        public float MaxWalkAnimSpeed = 2.2f;
        public Vector3 SitOffset = new Vector3(0, -0.22f, 0);

        readonly List<GameObject> _queueGuests = new List<GameObject>();
        readonly List<int> _nodes = new List<int>();   // per-guest position on the serpentine path
        readonly Dictionary<int, List<GameObject>> _riders = new Dictionary<int, List<GameObject>>();

        // Path node k: 0..QueueSpots.Count-1 = the spots (0 = front of line);
        // Count = entry gap in the back rail row; Count+1 = spawn point.
        int EntryNode => QueueSpots.Count;
        int SpawnNode => QueueSpots.Count + 1;

        Vector3 NodePos(int k)
        {
            if (k < QueueSpots.Count) return QueueSpots[k].position;
            if (k == EntryNode && QueueEntryPoint) return QueueEntryPoint.position;
            return SpawnPoint ? SpawnPoint.position : Vector3.zero;
        }

        void Start()
        {
            var sim = AttractionManager.Instance.Sim;
            sim.Guests.OnGuestsBoarded += OnBoarded;
            foreach (var t in Trains) _riders[t.TrainId] = new List<GameObject>();
            // Single-block station: riders leave the moment the RV arrives (no separate unload step).
            // Split station: riders stay seated until the operator releases restraints (OnUnloadReleased).
            foreach (var t in sim.Trains)
                if (_riders.ContainsKey(t.Id))
                    t.OnArrivedStation += tr => { if (!sim.Config.SeparateLoadUnload) ReleaseRiders(tr.Id); };
            sim.OnUnloadReleased += tr => ReleaseRiders(tr.Id);
        }

        /// <summary>Hook a runtime-added RV (HMI "Add RV") into the guest system: track its riders and
        /// wire its arrival so it unloads like the others.</summary>
        public void RegisterTrain(TrainVisual tv)
        {
            if (tv == null) return;
            var arr = new TrainVisual[Trains.Length + 1];
            Trains.CopyTo(arr, 0);
            arr[Trains.Length] = tv;
            Trains = arr;
            if (!_riders.ContainsKey(tv.TrainId)) _riders[tv.TrainId] = new List<GameObject>();
            var sim = AttractionManager.Instance.Sim;
            if (tv.TrainId < sim.Trains.Length)
            {
                var st = sim.Trains[tv.TrainId];
                st.OnArrivedStation += tr => { if (!sim.Config.SeparateLoadUnload) ReleaseRiders(tr.Id); };
            }
        }

        void Update()
        {
            var sim = AttractionManager.Instance.Sim;
            while (_queueGuests.Count < sim.Guests.QueueCount) SpawnGuest();
            while (_queueGuests.Count > sim.Guests.QueueCount && _queueGuests.Count > 0)
            { Destroy(_queueGuests[0]); _queueGuests.RemoveAt(0); _nodes.RemoveAt(0); }

            for (int i = 0; i < _queueGuests.Count; i++)
            {
                // Walk the serpentine node chain toward this guest's place in line (node i).
                // Never beeline: the next waypoint is always the adjacent node, which stays
                // inside the rail lanes and turns through the gaps.
                int target = Mathf.Min(i, QueueSpots.Count - 1);
                int node = _nodes[i];
                Vector3 dest = node > target ? NodePos(node - 1) : NodePos(target);

                var g = _queueGuests[i].transform;
                Vector3 before = g.position;
                g.position = Vector3.MoveTowards(g.position, dest, MoveSpeed * Time.deltaTime);
                if (node > target && (g.position - NodePos(node - 1)).sqrMagnitude < 0.01f)
                    _nodes[i] = node - 1;

                Vector3 step = g.position - before;
                bool moving = step.sqrMagnitude > 1e-8f;
                if (moving)
                {
                    var flat = new Vector3(step.x, 0, step.z);
                    if (flat.sqrMagnitude > 1e-8f)
                        g.rotation = Quaternion.Slerp(g.rotation, Quaternion.LookRotation(flat.normalized),
                                                      10f * Time.deltaTime);
                }
                SetAnim(_queueGuests[i], moving ? "Walk" : "Idle",
                        moving ? Mathf.Clamp(MoveSpeed / Mathf.Max(WalkClipSpeed, 0.01f), 1f, MaxWalkAnimSpeed) : 1f);
            }

            ProcessExiters(Time.deltaTime);   // stand-up + walk-out for unloaded guests
        }

        void SpawnGuest()
        {
            Color hoodie = Color.HSVToRGB(Random.value, Random.Range(0.45f, 0.8f), Random.Range(0.7f, 1f));
            var g = GuestPrefab != null ? SpawnDewey(hoodie) : SpawnCapsule(hoodie);
            g.transform.position = SpawnPoint ? SpawnPoint.position : Vector3.zero;
            _queueGuests.Add(g);
            _nodes.Add(SpawnNode);
        }

        GameObject SpawnDewey(Color hoodie)
        {
            var g = Instantiate(GuestPrefab);
            g.name = "Guest_Dewey";
            g.transform.localScale = Vector3.one * GuestScale * Random.Range(0.92f, 1.08f);

            var anim = g.GetComponent<Animator>();
            if (anim == null) anim = g.AddComponent<Animator>();
            anim.runtimeAnimatorController = GuestAnimController;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            TintHoodie(g, hoodie);
            return g;
        }

        public static void TintHoodie(GameObject g, Color hoodie)
        {
            foreach (var r in g.GetComponentsInChildren<Renderer>())
                foreach (var m in r.materials)
                    if (m.name.StartsWith("HoodieBlue") || m.name.StartsWith("LightBlue"))
                        m.color = hoodie;
        }

        GameObject SpawnCapsule(Color shirt)
        {
            var g = new GameObject("Guest");
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(g.transform, false);
            body.transform.localScale = new Vector3(0.42f, 0.55f, 0.42f);
            body.transform.localPosition = Vector3.up * 0.55f; // guest origin is at the feet
            if (GuestMaterial) body.GetComponent<Renderer>().material = GuestMaterial;
            body.GetComponent<Renderer>().material.color = shirt;
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(head.GetComponent<Collider>());
            head.transform.SetParent(g.transform, false);
            head.transform.localScale = Vector3.one * 0.3f;
            head.transform.localPosition = Vector3.up * 1.28f;
            return g;
        }

        static void SetAnim(GameObject g, string state, float speed)
        {
            var anim = g.GetComponent<Animator>();
            if (anim == null || anim.runtimeAnimatorController == null) return;
            anim.speed = speed;
            var cur = anim.GetCurrentAnimatorStateInfo(0);
            if (!cur.IsName(state) && !anim.IsInTransition(0))
                anim.CrossFadeInFixedTime(state, 0.15f);
        }

        void OnBoarded(int n)
        {
            var sim = AttractionManager.Instance.Sim;
            var platform = sim.PlatformTrain();
            if (platform == null) return;
            var seats = Trains[platform.Id].SeatAnchors;
            var list = _riders[platform.Id];
            var tv = Trains[platform.Id];
            for (int k = 0; k < n && _queueGuests.Count > 0; k++)
            {
                var g = _queueGuests[0]; _queueGuests.RemoveAt(0); _nodes.RemoveAt(0);
                int seat = Mathf.Min(list.Count, seats.Length - 1);
                // world-space placement: seat anchors inside FBX props carry axis-compensation
                // rotations, so local-space identity/offsets would tip or shift the rider
                g.transform.SetParent(seats[seat], true);
                g.transform.position = seats[seat].position
                    + tv.transform.rotation * (GuestPrefab != null ? SitOffset : Vector3.up * 0.5f);
                g.transform.rotation = tv.transform.rotation; // face travel direction, stay upright
                SetAnim(g, "Sit", 1f);
                list.Add(g);
            }
        }

        int _exitRR;
        Transform PickUnloadExit()
        {
            if (UnloadExitPoints != null && UnloadExitPoints.Count > 0)
                return UnloadExitPoints[_exitRR++ % UnloadExitPoints.Count];
            return ExitPoint;
        }

        // ---- Unload guest flow: Seated -> StandingUp -> WalkingToExit -> Exited ----
        enum ExitState { StandingUp, WalkingToExit }
        class Exiter
        {
            public GameObject Go;
            public ExitState State;
            public float Timer;        // stand-up delay/duration remaining
            public int Node;           // index into the exit path
            public Vector3 SeatPos;    // pose to rise from
            public Vector3 StandPos;   // pose to rise to
        }
        readonly List<Exiter> _exiters = new List<Exiter>();

        /// <summary>Detach a returned RV's riders and start the staged stand-up + walk-out flow.</summary>
        void ReleaseRiders(int trainId)
        {
            if (!_riders.ContainsKey(trainId)) return;
            float stagger = 0f;
            foreach (var g in _riders[trainId])
            {
                if (g == null) continue;
                g.transform.SetParent(null);
                Vector3 seat = g.transform.position;
                _exiters.Add(new Exiter
                {
                    Go = g,
                    State = ExitState.StandingUp,
                    Timer = StandUpSeconds + stagger,            // stagger so they rise one after another
                    Node = 0,
                    SeatPos = seat,
                    StandPos = seat + Vector3.up * StandUpRise,
                });
                SetAnim(g, "Idle", 1f);
                stagger += StandUpStagger;
            }
            _riders[trainId].Clear();
        }

        Vector3 ExitNodePos(int node)
        {
            if (UnloadExitPath != null && node < UnloadExitPath.Count && UnloadExitPath[node] != null)
                return UnloadExitPath[node].position;
            var fallback = PickUnloadExit();
            return fallback ? fallback.position : Vector3.zero;
        }
        int ExitNodeCount => (UnloadExitPath != null && UnloadExitPath.Count > 0) ? UnloadExitPath.Count : 1;

        void ProcessExiters(float dt)
        {
            for (int i = _exiters.Count - 1; i >= 0; i--)
            {
                var e = _exiters[i];
                if (e.Go == null) { _exiters.RemoveAt(i); continue; }
                var tr = e.Go.transform;

                if (e.State == ExitState.StandingUp)
                {
                    // Rise smoothly from the seated pose to standing, then start walking.
                    e.Timer -= dt;
                    float k = Mathf.Clamp01(1f - e.Timer / Mathf.Max(0.0001f, StandUpSeconds));
                    tr.position = Vector3.Lerp(e.SeatPos, e.StandPos, Mathf.SmoothStep(0f, 1f, k));
                    if (e.Timer <= 0f) { e.State = ExitState.WalkingToExit; SetAnim(e.Go, "Walk", 1.3f); }
                    continue;
                }

                // WalkingToExit: follow the exit-path nodes to the door, then despawn.
                Vector3 dest = ExitNodePos(e.Node);
                Vector3 before = tr.position;
                tr.position = Vector3.MoveTowards(tr.position, dest, MoveSpeed * dt);
                Vector3 step = tr.position - before;
                var flat = new Vector3(step.x, 0, step.z);
                if (flat.sqrMagnitude > 1e-8f)
                    tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.LookRotation(flat.normalized), 10f * dt);

                if ((tr.position - dest).sqrMagnitude < 0.04f)
                {
                    e.Node++;
                    if (e.Node >= ExitNodeCount) { Destroy(e.Go); _exiters.RemoveAt(i); } // reached the exit door
                }
            }
        }
    }
}
