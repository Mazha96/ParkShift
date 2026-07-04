using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Composes a stylized low-poly coaster train from flat-shaded procedural meshes:
    /// two articulated cars ("Car0", "Car1") with beveled body shells, individual raised
    /// bucket seats, per-row restraint bars ("RestraintBar0..3", pivots authored OPEN so
    /// rest * Euler(95,0,0) = closed — the exact contract TrainRestraintDriver animates),
    /// wheel bogies with spinnable axles ("WheelAxle*"), a nose cone with an emissive
    /// headlight on the front car and tail lamps on the rear car.
    /// Runtime-safe; used by SceneBuilder so scenes stay rebuildable.
    /// Train is long along +Z (travel direction). Returns 8 seat anchors (4 rows x 2).
    /// TrainVisual auto-discovers the Car/WheelAxle/RestraintBar children and articulates
    /// each car along the spline, spins the axles and (when no TrainRestraintDriver is
    /// present) animates the bars itself.
    /// </summary>
    public static class TrainCarFactory
    {
        /// <summary>Arc-length spacing between car pivots; TrainVisual.CarSpacing defaults to this.</summary>
        public const float CarSpacing = 2.55f;

        const float CarHalfLength = 1.15f;
        const int SeatRowsPerCar = 2;
        const int CarCount = 2;

        /// <summary>Legacy entry point (SceneBuilder calls this).</summary>
        public static GameObject Create(string name, Material bodyMat, Material darkMat,
                                        Material seatMat, Material lightMat,
                                        out Transform[] seatAnchors)
        {
            Transform[] bars;
            return Create(name, bodyMat, darkMat, seatMat, lightMat, out seatAnchors, out bars);
        }

        /// <summary>Full entry point: also returns the 4 restraint-bar pivots (row order),
        /// ready to hand to a TrainRestraintDriver. If no driver is attached, TrainVisual's
        /// built-in fallback animates the identically named pivots instead.</summary>
        public static GameObject Create(string name, Material bodyMat, Material darkMat,
                                        Material seatMat, Material lightMat,
                                        out Transform[] seatAnchors, out Transform[] restraintBars)
        {
            var root = new GameObject(name);
            Material glowMat = MakeEmissive(lightMat, 2.2f);

            var anchors = new List<Transform>();
            var bars = new List<Transform>();
            for (int c = 0; c < CarCount; c++)
                BuildCar(root, c, c == 0, c == CarCount - 1,
                         bodyMat, darkMat, seatMat, glowMat, anchors, bars);

            seatAnchors = anchors.ToArray();
            restraintBars = bars.ToArray();
            return root;
        }

        // ---------------- one car ----------------

        static void BuildCar(GameObject root, int index, bool isFront, bool isRear,
                             Material bodyMat, Material darkMat, Material seatMat, Material glowMat,
                             List<Transform> anchors, List<Transform> bars)
        {
            var car = new GameObject("Car" + index);
            car.transform.SetParent(root.transform, false);
            car.transform.localPosition = new Vector3(0f, 0f, -index * CarSpacing);

            // --- Body shell: beveled tub cross-section extruded along the car, plus nose/tail lofts ---
            Vector2[] shell =
            {
                new Vector2(0.45f, 0.06f),  new Vector2(0.62f, 0.18f),
                new Vector2(0.62f, 0.46f),  new Vector2(0.48f, 0.56f),
                new Vector2(-0.48f, 0.56f), new Vector2(-0.62f, 0.46f),
                new Vector2(-0.62f, 0.18f), new Vector2(-0.45f, 0.06f),
            };
            var shellB = new FlatMeshBuilder();
            shellB.AddExtrusion(Vector3.zero, Quaternion.identity, shell, CarHalfLength, true, true);
            if (isFront)
            {
                // Nose cone: loft the full profile down to a blunt tip past the front cap.
                Vector2[] tip = ScaleProfile(shell, 0.35f, 0.45f, 0.32f);
                shellB.AddLoft(Vector3.zero, Quaternion.identity, shell, CarHalfLength,
                               tip, CarHalfLength + 0.6f, false, true);
            }
            if (isRear)
            {
                // Tail fairing: shorter, gentler taper off the back cap. Loft runs back-to-front
                // (lower z -> higher z) so normals stay outward.
                Vector2[] tail = ScaleProfile(shell, 0.7f, 0.7f, 0.31f);
                shellB.AddLoft(Vector3.zero, Quaternion.identity, tail, -CarHalfLength - 0.35f,
                               shell, -CarHalfLength, true, false);
            }
            MeshChild(car, "Shell", shellB.ToMesh("CarShell"), bodyMat);

            // --- Frame: chassis slab, top deck plate, bogie blocks, couplers (dark) ---
            var frameB = new FlatMeshBuilder();
            frameB.AddBox(new Vector3(0f, -0.02f, 0f), Quaternion.identity, new Vector3(1.0f, 0.14f, 2.2f));
            frameB.AddBox(new Vector3(0f, 0.575f, 0f), Quaternion.identity, new Vector3(0.92f, 0.05f, 2.24f));
            foreach (float bz in new[] { -0.8f, 0.8f })
                frameB.AddBox(new Vector3(0f, -0.06f, bz), Quaternion.identity, new Vector3(0.5f, 0.16f, 0.6f));
            if (!isRear)  // coupler bar reaching back toward the next car
                frameB.AddBox(new Vector3(0f, 0.10f, -CarHalfLength - 0.09f), Quaternion.identity,
                              new Vector3(0.14f, 0.10f, 0.34f));
            if (index > 0) // mating tongue on the following car
                frameB.AddBox(new Vector3(0f, 0.10f, CarHalfLength + 0.09f), Quaternion.identity,
                              new Vector3(0.14f, 0.10f, 0.34f));
            MeshChild(car, "Frame", frameB.ToMesh("CarFrame"), darkMat);

            // --- Seats: 2 rows x 2 raised bucket seats, seat anchors above the cushions ---
            var seatB = new FlatMeshBuilder();
            float[] rowZ = { 0.35f, -0.5f };
            Quaternion lean = Quaternion.Euler(-8f, 0f, 0f);
            for (int row = 0; row < SeatRowsPerCar; row++)
            {
                float z = rowZ[row];
                int globalRow = index * SeatRowsPerCar + row;
                foreach (float sx in new[] { -0.28f, 0.28f })
                {
                    seatB.AddBox(new Vector3(sx, 0.62f, z), Quaternion.identity, new Vector3(0.42f, 0.12f, 0.44f));
                    seatB.AddBox(new Vector3(sx, 0.92f, z - 0.24f), lean, new Vector3(0.42f, 0.5f, 0.12f));
                    seatB.AddBox(new Vector3(sx, 1.22f, z - 0.28f), lean, new Vector3(0.28f, 0.16f, 0.14f));
                    foreach (float wx in new[] { -0.245f, 0.245f }) // side wings / center console
                        seatB.AddBox(new Vector3(sx + wx, 0.72f, z + 0.02f), Quaternion.identity,
                                     new Vector3(0.07f, 0.18f, 0.34f));

                    var anchor = new GameObject("Seat" + (globalRow * 2 + (sx < 0f ? 0 : 1)));
                    anchor.transform.SetParent(car.transform, false);
                    anchor.transform.localPosition = new Vector3(sx, 0.74f, z);
                    anchors.Add(anchor.transform);
                }
                bars.Add(BuildRestraintBar(car, globalRow, z, darkMat));
            }
            MeshChild(car, "Seats", seatB.ToMesh("CarSeats"), seatMat);

            // --- Wheel bogies: spinnable axle pivots, chunky octagonal wheels straddling the rails ---
            foreach (float bz in new[] { -0.8f, 0.8f })
            {
                var axle = new GameObject("WheelAxle" + index + (bz < 0f ? "R" : "F"));
                axle.transform.SetParent(car.transform, false);
                axle.transform.localPosition = new Vector3(0f, 0.03f, bz);
                var wheelB = new FlatMeshBuilder();
                Quaternion lateral = Quaternion.Euler(0f, 90f, 0f); // prism Z -> car X
                foreach (float wx in new[] { -0.58f, 0.58f })
                {
                    wheelB.AddPrism(new Vector3(wx, 0f, 0f), lateral, 0.16f, 0.055f, 8);
                    wheelB.AddPrism(new Vector3(wx, 0f, 0f), lateral, 0.07f, 0.075f, 8); // hub cap
                }
                wheelB.AddBox(Vector3.zero, lateral, new Vector3(0.06f, 0.06f, 1.05f));   // axle rod
                MeshChild(axle, "Wheels", wheelB.ToMesh("BogieWheels"), darkMat);
            }

            // --- Lights ---
            if (isFront && glowMat != null)
            {
                var lightB = new FlatMeshBuilder();
                lightB.AddPrism(new Vector3(0f, 0.36f, CarHalfLength + 0.58f), Quaternion.identity, 0.11f, 0.07f, 8);
                MeshChild(car, "Headlight", lightB.ToMesh("Headlight"), glowMat);
            }
            if (isRear && glowMat != null)
            {
                var lampB = new FlatMeshBuilder();
                foreach (float lx in new[] { -0.3f, 0.3f })
                    lampB.AddPrism(new Vector3(lx, 0.32f, -CarHalfLength - 0.32f), Quaternion.identity, 0.06f, 0.045f, 8);
                MeshChild(car, "TailLights", lampB.ToMesh("TailLights"), glowMat);
            }
        }

        /// <summary>One lap-bar assembly. The pivot's REST pose is bars OPEN (arms up);
        /// rotating rest * Euler(95, 0, 0) swings the pad down over the riders' laps —
        /// matching TrainRestraintDriver.ClosedEuler and TrainVisual's fallback.</summary>
        static Transform BuildRestraintBar(GameObject car, int globalRow, float rowZ, Material darkMat)
        {
            var pivot = new GameObject("RestraintBar" + globalRow);
            pivot.transform.SetParent(car.transform, false);
            pivot.transform.localPosition = new Vector3(0f, 0.98f, rowZ - 0.32f);

            var barB = new FlatMeshBuilder();
            foreach (float ax in new[] { -0.5f, 0.5f })   // side arms, reaching up in the open pose
                barB.AddBox(new Vector3(ax, 0.25f, 0f), Quaternion.identity, new Vector3(0.07f, 0.5f, 0.07f));
            barB.AddBox(new Vector3(0f, 0.5f, 0.04f), Quaternion.identity, new Vector3(1.08f, 0.09f, 0.13f)); // lap pad
            foreach (float gx in new[] { -0.28f, 0.28f }) // grab handles, one per rider
                barB.AddBox(new Vector3(gx, 0.5f, 0.13f), Quaternion.identity, new Vector3(0.06f, 0.06f, 0.16f));
            MeshChild(pivot, "BarMesh", barB.ToMesh("RestraintBar"), darkMat);
            return pivot.transform;
        }

        // ---------------- helpers ----------------

        /// <summary>Scales a profile toward a vertical pivot: x by scaleX, y compressed toward
        /// pivotY by scaleY. Used to taper the shell into nose/tail cones.</summary>
        static Vector2[] ScaleProfile(Vector2[] src, float scaleX, float scaleY, float pivotY)
        {
            var dst = new Vector2[src.Length];
            for (int i = 0; i < src.Length; i++)
                dst[i] = new Vector2(src[i].x * scaleX, pivotY + (src[i].y - pivotY) * scaleY);
            return dst;
        }

        static GameObject MeshChild(GameObject parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            if (mat != null) go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            else go.AddComponent<MeshRenderer>();
            return go;
        }

        /// <summary>Derives an emissive variant of a material (headlight/tail lamps). The instance
        /// is embedded in the scene on save, so no material asset is created.</summary>
        static Material MakeEmissive(Material src, float boost)
        {
            if (src == null) return null;
            var m = new Material(src);
            m.name = src.name + "_Glow";
            Color baseColor = m.HasProperty("_Color") ? m.color : Color.white;
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", baseColor * boost);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return m;
        }
    }
}
// (procedural coaster overhaul)
