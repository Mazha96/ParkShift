using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Composes the "GALAXY EXPRESS" — a stylized low-poly spaceship shuttle train — from
    /// flat-shaded procedural meshes: two articulated cars ("Car0", "Car1") with rounded-loft
    /// fuselage shells, engine nacelles with emissive cyan thruster cones (named "Thruster*"
    /// so TrainThrusterGlow can drive their burn with sim speed via MaterialPropertyBlock),
    /// port/starboard running lights (red/green), a side accent stripe, individual raised
    /// bucket seats, per-row restraint bars ("RestraintBar0..3", pivots authored OPEN so
    /// rest * Euler(95,0,0) = closed — the exact contract TrainRestraintDriver animates),
    /// wheel bogies restyled as mag-clamp skids with spinnable axles ("WheelAxle*"), a
    /// pointed nose cone + tinted cockpit canopy + swept winglets on the lead car, and a
    /// triple main-thruster cluster on the rear car.
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

        /// <summary>The derived accent/emissive materials for one train — created once per
        /// train (same pattern as the old glowMat; embedded in the scene on save) and shared
        /// by every renderer that uses them. Animated glow goes through MPB, never instances.</summary>
        struct Palette
        {
            public Material Body, Dark, Seat, Glow, Thruster, Canopy, NavRed, NavGreen;
        }

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
            var pal = new Palette
            {
                Body = bodyMat,
                Dark = darkMat,
                Seat = seatMat,
                Glow = MakeEmissive(lightMat, 2.2f),
                Thruster = MakeEmissive(lightMat, new Color(0.25f, 0.85f, 1f), 2.6f),
                Canopy = MakeEmissive(lightMat, new Color(0.5f, 0.8f, 1f), 1.3f),
                NavRed = MakeEmissive(lightMat, new Color(1f, 0.22f, 0.16f), 2.4f),
                NavGreen = MakeEmissive(lightMat, new Color(0.3f, 1f, 0.4f), 2.4f),
            };

            var anchors = new List<Transform>();
            var bars = new List<Transform>();
            for (int c = 0; c < CarCount; c++)
                BuildCar(root, c, c == 0, c == CarCount - 1, pal, anchors, bars);

            // Speed-driven thruster burn (reads the sim through the sibling TrainVisual —
            // pure presentation, no sim mutation).
            root.AddComponent<TrainThrusterGlow>();

            seatAnchors = anchors.ToArray();
            restraintBars = bars.ToArray();
            return root;
        }

        // ---------------- one car ----------------

        static void BuildCar(GameObject root, int index, bool isFront, bool isRear,
                             Palette pal, List<Transform> anchors, List<Transform> bars)
        {
            var car = new GameObject("Car" + index);
            car.transform.SetParent(root.transform, false);
            car.transform.localPosition = new Vector3(0f, 0f, -index * CarSpacing);

            // --- Fuselage shell: rounded 12-point cross-section (still hard-edged per facet)
            //     extruded along the car, plus nose/tail lofts. Same envelope as the old tub:
            //     max half-width 0.66 (was 0.62), top 0.62 (was 0.56) — within tolerance.
            Vector2[] hull =
            {
                new Vector2(0.30f, 0.02f),  new Vector2(0.58f, 0.10f),
                new Vector2(0.66f, 0.28f),  new Vector2(0.56f, 0.48f),
                new Vector2(0.50f, 0.58f),  new Vector2(0.20f, 0.62f),
                new Vector2(-0.20f, 0.62f), new Vector2(-0.50f, 0.58f),
                new Vector2(-0.56f, 0.48f), new Vector2(-0.66f, 0.28f),
                new Vector2(-0.58f, 0.10f), new Vector2(-0.30f, 0.02f),
            };
            var shellB = new FlatMeshBuilder();
            shellB.AddExtrusion(Vector3.zero, Quaternion.identity, hull, CarHalfLength, true, true);
            if (isFront)
            {
                // Nose cone: two-stage loft so the fuselage sweeps into a pointed cockpit tip.
                Vector2[] mid = ScaleProfile(hull, 0.62f, 0.52f, 0.30f);
                Vector2[] tip = ScaleProfile(hull, 0.10f, 0.10f, 0.32f);
                shellB.AddLoft(Vector3.zero, Quaternion.identity, hull, CarHalfLength,
                               mid, CarHalfLength + 0.45f, false, false);
                shellB.AddLoft(Vector3.zero, Quaternion.identity, mid, CarHalfLength + 0.45f,
                               tip, CarHalfLength + 0.85f, false, true);

                // Winglets: swept canard fins lofted from a thin diamond chord profile out of
                // the nose flanks. Tips reach x ~0.75 — the widest point of the whole train.
                Vector2[] finRoot =
                {
                    new Vector2(0.34f, 0f), new Vector2(0f, 0.035f),
                    new Vector2(-0.34f, 0f), new Vector2(0f, -0.035f),
                };
                Vector2[] finTip = ScaleProfile(finRoot, 0.45f, 0.5f, 0f);
                foreach (float side in new[] { -1f, 1f })
                    shellB.AddLoft(new Vector3(side * 0.50f, 0.30f, 0.55f),
                                   Quaternion.Euler(0f, side * 115f, 0f),
                                   finRoot, 0f, finTip, 0.30f, false, true);
            }
            if (isRear)
            {
                // Tail fairing: shorter, gentler taper off the back cap. Loft runs back-to-front
                // (lower z -> higher z) so normals stay outward.
                Vector2[] tail = ScaleProfile(hull, 0.72f, 0.72f, 0.30f);
                shellB.AddLoft(Vector3.zero, Quaternion.identity, tail, -CarHalfLength - 0.35f,
                               hull, -CarHalfLength, true, false);
            }
            MeshChild(car, "Shell", shellB.ToMesh("CarShell"), pal.Body);

            // --- Frame: chassis slab, cabin deck plate, bogie blocks, couplers (dark) ---
            var frameB = new FlatMeshBuilder();
            frameB.AddBox(new Vector3(0f, -0.02f, 0f), Quaternion.identity, new Vector3(1.0f, 0.14f, 2.2f));
            frameB.AddBox(new Vector3(0f, 0.575f, 0f), Quaternion.identity, new Vector3(0.88f, 0.05f, 2.24f));
            foreach (float bz in new[] { -0.8f, 0.8f })
                frameB.AddBox(new Vector3(0f, -0.06f, bz), Quaternion.identity, new Vector3(0.5f, 0.16f, 0.6f));
            if (!isRear)  // coupler bar reaching back toward the next car
                frameB.AddBox(new Vector3(0f, 0.10f, -CarHalfLength - 0.09f), Quaternion.identity,
                              new Vector3(0.14f, 0.10f, 0.34f));
            if (index > 0) // mating tongue on the following car
                frameB.AddBox(new Vector3(0f, 0.10f, CarHalfLength + 0.09f), Quaternion.identity,
                              new Vector3(0.14f, 0.10f, 0.34f));
            MeshChild(car, "Frame", frameB.ToMesh("CarFrame"), pal.Dark);

            // --- Engine nacelles: side pods hugging the hull shoulders, rear-biased so the
            //     nozzles clear the coupler gap (cone tip -1.17 vs next car nose at -1.40). ---
            var nacB = new FlatMeshBuilder();
            foreach (float side in new[] { -0.58f, 0.58f })
                nacB.AddPrism(new Vector3(side, 0.42f, -0.35f), Quaternion.identity, 0.13f, 0.55f, 8);
            MeshChild(car, "Nacelles", nacB.ToMesh("CarNacelles"), pal.Dark);

            if (pal.Thruster != null)
            {
                // Nacelle thruster cones (emissive; "Thruster" prefix = speed-driven glow).
                var thrB = new FlatMeshBuilder();
                Vector2[] nozzle = FlatMeshBuilder.NGon(0.13f, 8);
                Vector2[] nozzleTip = FlatMeshBuilder.NGon(0.055f, 8);
                foreach (float side in new[] { -0.58f, 0.58f })
                    thrB.AddLoft(new Vector3(side, 0.42f, 0f), Quaternion.identity,
                                 nozzleTip, -1.12f, nozzle, -0.90f, true, false);
                MeshChild(car, "ThrusterNacelles", thrB.ToMesh("NacelleThrusters"), pal.Thruster);

                // Accent stripe: emissive cyan strake along both flanks (static glow — only
                // "Thruster*" children get the speed-driven MPB treatment).
                var stripeB = new FlatMeshBuilder();
                foreach (float side in new[] { -1f, 1f })
                    stripeB.AddBox(new Vector3(side * 0.655f, 0.24f, 0f), Quaternion.identity,
                                   new Vector3(0.02f, 0.06f, 2.18f));
                MeshChild(car, "AccentStripe", stripeB.ToMesh("AccentStripe"), pal.Thruster);
            }

            // --- Running lights: tiny nav dots, port red / starboard green ---
            if (pal.NavRed != null && pal.NavGreen != null)
            {
                var portB = new FlatMeshBuilder();
                var starB = new FlatMeshBuilder();
                foreach (float lz in new[] { 0.9f, 0f, -0.9f })
                {
                    portB.AddBox(new Vector3(-0.645f, 0.33f, lz), Quaternion.identity,
                                 new Vector3(0.03f, 0.05f, 0.05f));
                    starB.AddBox(new Vector3(0.645f, 0.33f, lz), Quaternion.identity,
                                 new Vector3(0.03f, 0.05f, 0.05f));
                }
                MeshChild(car, "RunningLightsPort", portB.ToMesh("RunningLightsPort"), pal.NavRed);
                MeshChild(car, "RunningLightsStarboard", starB.ToMesh("RunningLightsStarboard"), pal.NavGreen);
            }

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
                bars.Add(BuildRestraintBar(car, globalRow, z, pal.Dark));
            }
            MeshChild(car, "Seats", seatB.ToMesh("CarSeats"), pal.Seat);

            // --- Wheel bogies: mag-clamp skids on spinnable axle pivots. Geometry and the
            //     "WheelAxle*" naming stay identical so TrainVisual's spin keeps working. ---
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
                    wheelB.AddPrism(new Vector3(wx, 0f, 0f), lateral, 0.07f, 0.075f, 8); // clamp hub
                }
                wheelB.AddBox(Vector3.zero, lateral, new Vector3(0.06f, 0.06f, 1.05f));   // axle rod
                MeshChild(axle, "Wheels", wheelB.ToMesh("BogieWheels"), pal.Dark);
            }

            // --- Lead car dressing: cockpit canopy, antenna, landing light ---
            if (isFront)
            {
                if (pal.Canopy != null)
                {
                    // Tinted canopy bubble raked forward over the nose (base buried in the hull
                    // top so it never floats; rear cap visible to the front-row riders).
                    Vector2[] canopy =
                    {
                        new Vector2(0.26f, 0.58f),  new Vector2(0.31f, 0.72f),
                        new Vector2(0.15f, 0.88f),  new Vector2(-0.15f, 0.88f),
                        new Vector2(-0.31f, 0.72f), new Vector2(-0.26f, 0.58f),
                    };
                    var canB = new FlatMeshBuilder();
                    canB.AddLoft(Vector3.zero, Quaternion.identity, canopy, 0.70f,
                                 ScaleProfile(canopy, 0.55f, 0.35f, 0.50f), 1.35f, true, true);
                    MeshChild(car, "Canopy", canB.ToMesh("Canopy"), pal.Canopy);
                }

                // Comm antenna on the port shoulder beside the canopy (clear of rider legs).
                var antB = new FlatMeshBuilder();
                antB.AddBox(new Vector3(-0.40f, 0.75f, 0.92f), Quaternion.identity,
                            new Vector3(0.025f, 0.34f, 0.025f));
                MeshChild(car, "Antenna", antB.ToMesh("Antenna"), pal.Dark);
                if (pal.NavRed != null)
                {
                    var antTipB = new FlatMeshBuilder();
                    antTipB.AddBox(new Vector3(-0.40f, 0.945f, 0.92f), Quaternion.identity,
                                   new Vector3(0.045f, 0.045f, 0.045f));
                    MeshChild(car, "AntennaBeacon", antTipB.ToMesh("AntennaBeacon"), pal.NavRed);
                }

                if (pal.Glow != null)
                {
                    var lightB = new FlatMeshBuilder();
                    lightB.AddPrism(new Vector3(0f, 0.30f, CarHalfLength + 0.57f), Quaternion.identity,
                                    0.07f, 0.06f, 8);
                    MeshChild(car, "LandingLight", lightB.ToMesh("LandingLight"), pal.Glow);
                }
            }

            // --- Rear car dressing: engine block + triple main-thruster cluster ---
            if (isRear)
            {
                var blockB = new FlatMeshBuilder();
                blockB.AddBox(new Vector3(0f, 0.30f, -CarHalfLength - 0.28f), Quaternion.identity,
                              new Vector3(0.92f, 0.44f, 0.26f));
                MeshChild(car, "EngineBlock", blockB.ToMesh("EngineBlock"), pal.Dark);

                if (pal.Thruster != null)
                {
                    var mainB = new FlatMeshBuilder();
                    mainB.AddLoft(new Vector3(0f, 0.34f, 0f), Quaternion.identity,
                                  FlatMeshBuilder.NGon(0.085f, 8), -CarHalfLength - 0.70f,
                                  FlatMeshBuilder.NGon(0.20f, 8), -CarHalfLength - 0.40f, true, false);
                    foreach (float side in new[] { -0.36f, 0.36f })
                        mainB.AddLoft(new Vector3(side, 0.22f, 0f), Quaternion.identity,
                                      FlatMeshBuilder.NGon(0.06f, 8), -CarHalfLength - 0.60f,
                                      FlatMeshBuilder.NGon(0.14f, 8), -CarHalfLength - 0.38f, true, false);
                    MeshChild(car, "ThrusterMain", mainB.ToMesh("MainThrusters"), pal.Thruster);
                }
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
        /// pivotY by scaleY. Used to taper the shell into nose/tail cones and fins to tips.</summary>
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

        /// <summary>Derives an emissive variant of a material glowing in its own base color.
        /// The instance is embedded in the scene on save, so no material asset is created.</summary>
        static Material MakeEmissive(Material src, float boost)
        {
            if (src == null) return null;
            Color baseColor = src.HasProperty("_Color") ? src.color : Color.white;
            return MakeEmissive(src, baseColor, boost);
        }

        /// <summary>Derives an emissive variant of a material tinted to an explicit color
        /// (thrusters, nav lights, canopy). One instance per train, shared by all its users;
        /// per-frame glow animation goes through MaterialPropertyBlock, never new instances.</summary>
        static Material MakeEmissive(Material src, Color tint, float boost)
        {
            if (src == null) return null;
            var m = new Material(src);
            m.name = src.name + "_Glow";
            if (m.HasProperty("_Color")) m.color = tint;
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", tint * boost);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return m;
        }
    }
}
// (GALAXY EXPRESS spaceship overhaul)
