using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ParkShift.Attraction;
using ParkShift.Interaction;
using ParkShift.Vehicles;

namespace ParkShift.EditorTools
{
    /// <summary>
    /// Scene overlap auditor for the procedurally built park. Scans the OPEN scene for:
    ///  (a) trigger-trigger overlaps between different interactable/label roots (the classic
    ///      "aiming at one control hits another" bug),
    ///  (b) solid-solid renderer-bounds AABB intersections between different top-level prop
    ///      roots where the penetration exceeds 5% of the smaller volume (parent-child /
    ///      compound assemblies under one root are skipped; resting-on-floor contacts with
    ///      less than 5cm of Y sink against floor/ground/platform objects are ignored),
    ///  (c) a train sweep: a 1.9w x 2.2h x 0.5-deep rider envelope is marched every 0.5m along
    ///      the TrackPath using the same parallel-transported FrameTable the rails are built
    ///      from, and every solid renderer it clips is reported with its arc distance. Track
    ///      and train geometry itself (Rails/Ties/Spine/Supports/BrakeFins/Train/StationDeck/
    ///      StationRails/DriveTires name prefixes) is excluded.
    /// Results go to the Console (counts + worst 20) and to SceneAuditReport.txt at the
    /// project root (full list). Some listed hits are INTENTIONAL gameplay props that live in
    /// the sweep on purpose (DroppedItem, TrackIntruder, CollisionMarker, JammedCarFlag,
    /// ValleyedBeacon) — they only appear while their fault holds the trains parked.
    /// Run from menu ParkShift -> Audit Scene Overlaps; Build All Scenes also runs it.
    /// </summary>
    public static class SceneAudit
    {
        // Renderers larger than this (bounds volume, m^3) are scene-scale shells (ground
        // plane, crater ridges, whole-course track meshes) whose AABBs intersect everything;
        // they are skipped in the solid-solid pass and counted in the report header.
        const float BigRendererVolume = 300f;
        const float SolidPenetrationRatio = 0.05f;  // 5% of the smaller volume
        const float FloorContactY = 0.05f;          // resting-contact tolerance
        const float SweepStep = 0.5f;               // meters of arc between envelope samples

        static readonly string[] SweepExcludePrefixes =
        {
            "Rails", "Ties", "Spine", "Supports", "BrakeFins", "Train",
            "StationDeck", "StationRails", "DriveTires",
        };

        class Finding
        {
            public string Category;
            public float Severity;
            public string Detail;
        }

        class SweepHit
        {
            public float Depth;
            public float ArcDistance;
        }

        // Oriented box for the SAT test (triggers are BoxColliders on rotated transforms, so a
        // world-AABB test would raise false alarms on tilted console volumes).
        class Obb
        {
            public Vector3 C;
            public Vector3[] Axes;
            public Vector3 H;
        }

        // Count of scene-shell renderers skipped by the solid-solid pass (report header).
        static int _bigSkipped;

        [MenuItem("ParkShift/Audit Scene Overlaps")]
        public static void RunAudit()
        {
            _bigSkipped = 0;
            var findings = new List<Finding>();
            AuditTriggerTriggers(findings);
            AuditSolidSolids(findings);
            AuditTrainSweep(findings);
            Report(findings);
        }

        // ---------------- (a) trigger vs trigger ----------------

        static Transform InteractRoot(Collider c)
        {
            var pi = c.GetComponentInParent<PanelInteractable>();
            if (pi != null) return pi.transform;
            var ll = c.GetComponentInParent<LookLabel>();
            if (ll != null) return ll.transform;
            return c.transform.root;
        }

        static void AuditTriggerTriggers(List<Finding> findings)
        {
            var all = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None)
                .Where(c => c.isTrigger && c.gameObject.activeInHierarchy).ToArray();
            for (int i = 0; i < all.Length; i++)
            {
                for (int j = i + 1; j < all.Length; j++)
                {
                    Transform ra = InteractRoot(all[i]);
                    Transform rb = InteractRoot(all[j]);
                    if (ra == rb) continue;
                    if (ra.IsChildOf(rb) || rb.IsChildOf(ra)) continue; // compound assembly
                    float pen;
                    if (!TriggerOverlap(all[i], all[j], out pen)) continue;
                    findings.Add(new Finding
                    {
                        Category = "TRIGGER-TRIGGER",
                        Severity = pen,
                        Detail = string.Format("{0} (on {1}) x {2} (on {3})  pen ~{4:0.000}m",
                            Path(ra), all[i].name, Path(rb), all[j].name, pen),
                    });
                }
            }
        }

        static bool TriggerOverlap(Collider a, Collider b, out float pen)
        {
            var ba = a as BoxCollider;
            var bb = b as BoxCollider;
            if (ba != null && bb != null)
                return ObbOverlap(FromBox(ba), FromBox(bb), out pen);
            // Non-box triggers: fall back to world AABBs.
            pen = 0f;
            if (!a.bounds.Intersects(b.bounds)) return false;
            Bounds ia = a.bounds, ib = b.bounds;
            float ox = Mathf.Min(ia.max.x, ib.max.x) - Mathf.Max(ia.min.x, ib.min.x);
            float oy = Mathf.Min(ia.max.y, ib.max.y) - Mathf.Max(ia.min.y, ib.min.y);
            float oz = Mathf.Min(ia.max.z, ib.max.z) - Mathf.Max(ia.min.z, ib.min.z);
            pen = Mathf.Min(ox, Mathf.Min(oy, oz));
            return pen > 0f;
        }

        static Obb FromBox(BoxCollider bc)
        {
            var t = bc.transform;
            var o = new Obb();
            o.C = t.TransformPoint(bc.center);
            o.Axes = new[] { t.right.normalized, t.up.normalized, t.forward.normalized };
            Vector3 s = t.lossyScale;
            o.H = new Vector3(Mathf.Abs(bc.size.x * s.x), Mathf.Abs(bc.size.y * s.y), Mathf.Abs(bc.size.z * s.z)) * 0.5f;
            return o;
        }

        /// <summary>15-axis separating-axis test; pen is the smallest overlap found (an
        /// approximate escape distance) when the boxes intersect.</summary>
        static bool ObbOverlap(Obb a, Obb b, out float pen)
        {
            var axes = new List<Vector3>(15);
            axes.AddRange(a.Axes);
            axes.AddRange(b.Axes);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    Vector3 c = Vector3.Cross(a.Axes[i], b.Axes[j]);
                    if (c.sqrMagnitude > 1e-6f) axes.Add(c.normalized);
                }
            Vector3 d = b.C - a.C;
            pen = float.MaxValue;
            for (int k = 0; k < axes.Count; k++)
            {
                Vector3 ax = axes[k];
                float ra = Mathf.Abs(Vector3.Dot(ax, a.Axes[0])) * a.H.x
                         + Mathf.Abs(Vector3.Dot(ax, a.Axes[1])) * a.H.y
                         + Mathf.Abs(Vector3.Dot(ax, a.Axes[2])) * a.H.z;
                float rb = Mathf.Abs(Vector3.Dot(ax, b.Axes[0])) * b.H.x
                         + Mathf.Abs(Vector3.Dot(ax, b.Axes[1])) * b.H.y
                         + Mathf.Abs(Vector3.Dot(ax, b.Axes[2])) * b.H.z;
                float overlap = ra + rb - Mathf.Abs(Vector3.Dot(ax, d));
                if (overlap <= 0f) return false;
                if (overlap < pen) pen = overlap;
            }
            return true;
        }

        // ---------------- (b) solid vs solid ----------------

        static bool IsFloorish(Renderer r)
        {
            string n = r.name.ToLowerInvariant();
            string rn = r.transform.root.name.ToLowerInvariant();
            return n.Contains("floor") || n.Contains("ground") || n.Contains("platform")
                || rn.Contains("floor") || rn.Contains("ground") || rn.Contains("platform");
        }

        static Renderer[] SolidRenderers()
        {
            return Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.enabled && r.gameObject.activeInHierarchy
                            && r.GetComponent<TextMesh>() == null
                            && !(r is ParticleSystemRenderer))
                .ToArray();
        }

        static float Volume(Bounds b) { return b.size.x * b.size.y * b.size.z; }

        static void AuditSolidSolids(List<Finding> findings)
        {
            var rends = SolidRenderers();
            var kept = new List<Renderer>(rends.Length);
            for (int i = 0; i < rends.Length; i++)
            {
                if (Volume(rends[i].bounds) > BigRendererVolume) { _bigSkipped++; continue; }
                kept.Add(rends[i]);
            }
            for (int i = 0; i < kept.Count; i++)
            {
                Bounds ba = kept[i].bounds;
                Transform ra = kept[i].transform.root;
                for (int j = i + 1; j < kept.Count; j++)
                {
                    Transform rb = kept[j].transform.root;
                    if (ra == rb) continue; // compound assembly / parent-child
                    Bounds bb = kept[j].bounds;
                    if (!ba.Intersects(bb)) continue;
                    float ox = Mathf.Min(ba.max.x, bb.max.x) - Mathf.Max(ba.min.x, bb.min.x);
                    float oy = Mathf.Min(ba.max.y, bb.max.y) - Mathf.Max(ba.min.y, bb.min.y);
                    float oz = Mathf.Min(ba.max.z, bb.max.z) - Mathf.Max(ba.min.z, bb.min.z);
                    if (ox <= 0f || oy <= 0f || oz <= 0f) continue;
                    // Resting contact: shallow Y sink into a floor/ground/platform object.
                    if (oy < FloorContactY && (IsFloorish(kept[i]) || IsFloorish(kept[j]))) continue;
                    float inter = ox * oy * oz;
                    float smaller = Mathf.Min(Volume(ba), Volume(bb));
                    if (smaller <= 1e-6f) continue;
                    float ratio = inter / smaller;
                    if (ratio <= SolidPenetrationRatio) continue;
                    findings.Add(new Finding
                    {
                        Category = "SOLID-SOLID",
                        Severity = ratio,
                        Detail = string.Format("{0}/{1} x {2}/{3}  overlap {4:0.000}x{5:0.000}x{6:0.000}m = {7:0}% of smaller",
                            ra.name, kept[i].name, rb.name, kept[j].name, ox, oy, oz, ratio * 100f),
                    });
                }
            }
        }

        // ---------------- (c) train sweep ----------------

        static bool SweepExcluded(Renderer r)
        {
            string n = r.name;
            string rn = r.transform.root.name;
            for (int i = 0; i < SweepExcludePrefixes.Length; i++)
                if (n.StartsWith(SweepExcludePrefixes[i]) || rn.StartsWith(SweepExcludePrefixes[i]))
                    return true;
            return false;
        }

        static void AuditTrainSweep(List<Finding> findings)
        {
            var path = Object.FindFirstObjectByType<TrackPath>();
            if (path == null)
            {
                findings.Add(new Finding { Category = "TRAIN-SWEEP", Severity = 0f, Detail = "No TrackPath in scene — sweep skipped." });
                return;
            }
            var frames = new TrackMeshBuilder.FrameTable(path);
            float len = frames.TotalLength;
            var rends = SolidRenderers().Where(r => !SweepExcluded(r)).ToArray();
            var worst = new Dictionary<Renderer, SweepHit>();

            for (float dist = 0f; dist < len; dist += SweepStep)
            {
                Vector3 pos;
                Quaternion rot;
                frames.FrameAt(dist, out pos, out rot);
                // Rider envelope box in the track frame; tested as its world AABB.
                Vector3 c = pos + rot * new Vector3(0f, 1.1f, 0f);
                Vector3 hx = rot * new Vector3(0.95f, 0f, 0f);
                Vector3 hy = rot * new Vector3(0f, 1.1f, 0f);
                Vector3 hz = rot * new Vector3(0f, 0f, 0.25f);
                Vector3 ext = new Vector3(
                    Mathf.Abs(hx.x) + Mathf.Abs(hy.x) + Mathf.Abs(hz.x),
                    Mathf.Abs(hx.y) + Mathf.Abs(hy.y) + Mathf.Abs(hz.y),
                    Mathf.Abs(hx.z) + Mathf.Abs(hy.z) + Mathf.Abs(hz.z));
                var box = new Bounds(c, ext * 2f);

                for (int i = 0; i < rends.Length; i++)
                {
                    Bounds rb = rends[i].bounds;
                    if (!box.Intersects(rb)) continue;
                    float ox = Mathf.Min(box.max.x, rb.max.x) - Mathf.Max(box.min.x, rb.min.x);
                    float oy = Mathf.Min(box.max.y, rb.max.y) - Mathf.Max(box.min.y, rb.min.y);
                    float oz = Mathf.Min(box.max.z, rb.max.z) - Mathf.Max(box.min.z, rb.min.z);
                    float depth = Mathf.Min(ox, Mathf.Min(oy, oz));
                    if (depth <= 0f) continue;
                    SweepHit hit;
                    if (!worst.TryGetValue(rends[i], out hit))
                    {
                        hit = new SweepHit { Depth = depth, ArcDistance = dist };
                        worst.Add(rends[i], hit);
                    }
                    else if (depth > hit.Depth)
                    {
                        hit.Depth = depth;
                        hit.ArcDistance = dist;
                    }
                }
            }
            foreach (var kv in worst)
            {
                findings.Add(new Finding
                {
                    Category = "TRAIN-SWEEP",
                    Severity = kv.Value.Depth,
                    Detail = string.Format("{0}/{1}  intrudes ~{2:0.000}m into the rider envelope at arc d={3:0.0}m (of {4:0.0}m)",
                        kv.Key.transform.root.name, kv.Key.name, kv.Value.Depth, kv.Value.ArcDistance, len),
                });
            }
        }

        // ---------------- reporting ----------------

        static string Path(Transform t)
        {
            string s = t.name;
            var p = t.parent;
            while (p != null) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }

        static void Report(List<Finding> findings)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var byCat = findings.GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.Count());
            var ordered = findings.OrderByDescending(f => f.Severity).ToList();

            var lines = new List<string>();
            lines.Add("ParkShift Scene Overlap Audit — scene '" + scene + "' — " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            lines.Add(string.Format("Findings: {0} total | trigger-trigger {1} | solid-solid {2} | train-sweep {3} | big scene-shell renderers skipped in solid pass: {4}",
                findings.Count,
                byCat.ContainsKey("TRIGGER-TRIGGER") ? byCat["TRIGGER-TRIGGER"] : 0,
                byCat.ContainsKey("SOLID-SOLID") ? byCat["SOLID-SOLID"] : 0,
                byCat.ContainsKey("TRAIN-SWEEP") ? byCat["TRAIN-SWEEP"] : 0,
                _bigSkipped));
            lines.Add("Known-intentional sweep residents: DroppedItem, TrackIntruder, CollisionMarker, JammedCarFlag, ValleyedBeacon (fault props; trains are parked while they matter).");
            lines.Add("");
            foreach (var grp in findings.GroupBy(f => f.Category))
            {
                lines.Add("== " + grp.Key + " (" + grp.Count() + ") ==");
                foreach (var f in grp.OrderByDescending(x => x.Severity))
                    lines.Add(string.Format("  [{0:0.000}] {1}", f.Severity, f.Detail));
                lines.Add("");
            }
            string file = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "SceneAuditReport.txt");
            System.IO.File.WriteAllLines(file, lines.ToArray());

            var log = new System.Text.StringBuilder();
            log.AppendLine(lines[0]);
            log.AppendLine(lines[1]);
            log.AppendLine("Worst " + Mathf.Min(20, ordered.Count) + ":");
            for (int i = 0; i < ordered.Count && i < 20; i++)
                log.AppendLine(string.Format("  [{0}] [{1:0.000}] {2}", ordered[i].Category, ordered[i].Severity, ordered[i].Detail));
            log.AppendLine("Full list: " + file);
            if (findings.Count == 0) Debug.Log("[SceneAudit] No overlaps found.\n" + log);
            else Debug.Log("[SceneAudit] " + findings.Count + " findings.\n" + log);
        }
    }
}
