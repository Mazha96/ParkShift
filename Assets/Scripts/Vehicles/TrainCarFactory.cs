using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Composes a coaster train (chassis, tub body, nose, seats, bolsters, wheels, headlight)
    /// from primitives. Runtime-safe; used by SceneBuilder so scenes stay rebuildable.
    /// Train is long along +Z (its travel direction). Returns 8 seat anchors (4 rows x 2).
    /// </summary>
    public static class TrainCarFactory
    {
        public static GameObject Create(string name, Material bodyMat, Material darkMat,
                                        Material seatMat, Material lightMat,
                                        out Transform[] seatAnchors)
        {
            var root = new GameObject(name);

            // Chassis + body tub
            Part(root, "Chassis", PrimitiveType.Cube, new Vector3(0, 0.16f, 0), new Vector3(1.3f, 0.2f, 3.6f), darkMat);
            Part(root, "Body", PrimitiveType.Cube, new Vector3(0, 0.42f, 0), new Vector3(1.2f, 0.38f, 3.4f), bodyMat);

            // Side bolsters
            Part(root, "BolsterL", PrimitiveType.Cube, new Vector3(-0.62f, 0.62f, 0), new Vector3(0.12f, 0.3f, 3.4f), bodyMat);
            Part(root, "BolsterR", PrimitiveType.Cube, new Vector3(0.62f, 0.62f, 0), new Vector3(0.12f, 0.3f, 3.4f), bodyMat);

            // Nose (raked front) + tail
            var nose = Part(root, "Nose", PrimitiveType.Cube, new Vector3(0, 0.45f, 1.95f), new Vector3(1.15f, 0.32f, 0.8f), bodyMat);
            nose.transform.localRotation = Quaternion.Euler(-18f, 0, 0);
            var tail = Part(root, "Tail", PrimitiveType.Cube, new Vector3(0, 0.45f, -1.92f), new Vector3(1.15f, 0.3f, 0.5f), bodyMat);
            tail.transform.localRotation = Quaternion.Euler(14f, 0, 0);

            // Headlight
            Part(root, "Headlight", PrimitiveType.Sphere, new Vector3(0, 0.5f, 2.32f), Vector3.one * 0.22f, lightMat);

            // Seats: 4 rows x 2, bench + backrest, anchors above benches
            var anchors = new List<Transform>();
            for (int row = 0; row < 4; row++)
            {
                float z = 1.15f - row * 0.78f;
                Part(root, $"Bench{row}", PrimitiveType.Cube, new Vector3(0, 0.62f, z), new Vector3(1.0f, 0.16f, 0.5f), seatMat);
                Part(root, $"Backrest{row}", PrimitiveType.Cube, new Vector3(0, 0.88f, z - 0.3f), new Vector3(1.0f, 0.42f, 0.12f), seatMat);
                for (int col = 0; col < 2; col++)
                {
                    var anchor = new GameObject($"Seat{row * 2 + col}");
                    anchor.transform.SetParent(root.transform, false);
                    anchor.transform.localPosition = new Vector3(col == 0 ? -0.28f : 0.28f, 0.74f, z);
                    anchors.Add(anchor.transform);
                }
            }

            // Wheel bogies: 3 axles, cylinders rotated to roll on Z travel
            for (int a = 0; a < 3; a++)
            {
                float z = -1.25f + a * 1.25f;
                foreach (float x in new[] { -0.58f, 0.58f })
                {
                    var w = Part(root, $"Wheel{a}{(x < 0 ? "L" : "R")}", PrimitiveType.Cylinder,
                                 new Vector3(x, 0.08f, z), new Vector3(0.34f, 0.05f, 0.34f), darkMat);
                    w.transform.localRotation = Quaternion.Euler(0, 0, 90f);
                }
            }

            seatAnchors = anchors.ToArray();
            return root;
        }

        static GameObject Part(GameObject parent, string name, PrimitiveType type,
                               Vector3 localPos, Vector3 localScale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
            // Child colliders aren't needed (interaction targets get their own); drop physics cost.
            var col = go.GetComponent<Collider>();
            if (col)
            {
                if (Application.isPlaying) Object.Destroy(col);
                else Object.DestroyImmediate(col);
            }
            return go;
        }
    }
}
