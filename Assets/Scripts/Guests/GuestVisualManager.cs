using System.Collections.Generic;
using UnityEngine;
using ParkShift.Attraction;
using ParkShift.Vehicles;

namespace ParkShift.Guests
{
    /// <summary>
    /// Visual mirror of the aggregate GuestSim: capsules shuffle along queue waypoints to match
    /// QueueCount; boarded guests teleport onto train seats; finished riders despawn at the exit.
    /// </summary>
    public class GuestVisualManager : MonoBehaviour
    {
        public List<Transform> QueueSpots = new List<Transform>();
        public Transform SpawnPoint;
        public Transform ExitPoint;
        public TrainVisual[] Trains;
        public Material GuestMaterial;

        readonly List<GameObject> _queueGuests = new List<GameObject>();
        readonly Dictionary<int, List<GameObject>> _riders = new Dictionary<int, List<GameObject>>();

        void Start()
        {
            var sim = AttractionManager.Instance.Sim;
            sim.Guests.OnGuestsBoarded += OnBoarded;
            foreach (var t in Trains) _riders[t.TrainId] = new List<GameObject>();
            sim.Trains[0].OnArrivedStation += tr => OnTrainReturned(tr.Id);
            sim.Trains[1].OnArrivedStation += tr => OnTrainReturned(tr.Id);
        }

        void Update()
        {
            var sim = AttractionManager.Instance.Sim;
            while (_queueGuests.Count < sim.Guests.QueueCount) SpawnGuest();
            while (_queueGuests.Count > sim.Guests.QueueCount && _queueGuests.Count > 0)
            { Destroy(_queueGuests[0]); _queueGuests.RemoveAt(0); }

            for (int i = 0; i < _queueGuests.Count; i++)
            {
                var spot = QueueSpots[Mathf.Min(i, QueueSpots.Count - 1)].position;
                var g = _queueGuests[i].transform;
                g.position = Vector3.MoveTowards(g.position, spot, 2.5f * Time.deltaTime);
            }
        }

        void SpawnGuest()
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            g.name = "Guest";
            Destroy(g.GetComponent<Collider>()); // visuals only; no physics cost
            g.transform.localScale = new Vector3(0.5f, 0.8f, 0.5f);
            g.transform.position = SpawnPoint ? SpawnPoint.position : Vector3.zero;
            if (GuestMaterial) g.GetComponent<Renderer>().material = GuestMaterial;
            g.GetComponent<Renderer>().material.color =
                Color.HSVToRGB(Random.value, 0.55f, 0.95f);
            _queueGuests.Add(g);
        }

        void OnBoarded(int n)
        {
            // Move n front-of-line guests onto the platform train's seats
            var sim = AttractionManager.Instance.Sim;
            var platform = sim.PlatformTrain();
            if (platform == null) return;
            var seats = Trains[platform.Id].SeatAnchors;
            var list = _riders[platform.Id];
            for (int k = 0; k < n && _queueGuests.Count > 0; k++)
            {
                var g = _queueGuests[0]; _queueGuests.RemoveAt(0);
                int seat = Mathf.Min(list.Count, seats.Length - 1);
                g.transform.SetParent(seats[seat], false);
                g.transform.localPosition = Vector3.up * 0.5f;
                list.Add(g);
            }
        }

        void OnTrainReturned(int trainId)
        {
            foreach (var g in _riders[trainId])
            {
                g.transform.SetParent(null);
                if (ExitPoint) g.transform.position = ExitPoint.position
                    + new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f));
                Destroy(g, 2f); // wander-out placeholder: linger then despawn
            }
            _riders[trainId].Clear();
        }
    }
}
