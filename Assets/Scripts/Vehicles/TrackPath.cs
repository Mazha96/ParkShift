using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>Ordered waypoint loop. Blocks are equal contiguous slices of the waypoint list.
    /// Maps (blockIndex, progress01) -> world position/rotation. Spline-free on purpose.</summary>
    public class TrackPath : MonoBehaviour
    {
        public List<Transform> Waypoints = new List<Transform>();
        public int BlockCount = 4;

        public void Sample(int block, float progress, out Vector3 pos, out Quaternion rot)
        {
            int perBlock = Waypoints.Count / BlockCount;
            float f = (block * perBlock) + Mathf.Clamp01(progress) * perBlock;
            int i = Mathf.FloorToInt(f) % Waypoints.Count;
            int j = (i + 1) % Waypoints.Count;
            float t = f - Mathf.Floor(f);
            pos = Vector3.Lerp(Waypoints[i].position, Waypoints[j].position, t);
            Vector3 dir = Waypoints[j].position - Waypoints[i].position;
            rot = dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity;
        }
    }
}
