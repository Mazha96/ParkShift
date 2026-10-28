using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ParkShift.Attraction;
using ParkShift.Guests;
using ParkShift.Interaction;
using ParkShift.Vehicles;
using PSEnv = ParkShift.Environment;

namespace ParkShift.EditorTools
{
    /// <summary>
    /// One-click scene construction from primitives. Run: menu ParkShift → Build All Scenes.
    /// Rebuildable at any time; scenes are throwaway artifacts, this script is the source of truth.
    /// Visual direction (GALAXY EXPRESS): bright illustrated cosmic twilight, painted light
    /// bands, geometry ink, a ringed planet and oversized candy-color park decorations.
    /// </summary>
    public static class SceneBuilder
    {
        static Material Mat(string name, Color c)
        {
            return Mat(name, c, null, 0.25f, 0f, 1f);
        }

        /// <summary>Creates/updates a material. If texSet is given, looks for
        /// Assets/Textures/{texSet}_albedo.png and {texSet}_normal.png — replace those
        /// files with downloaded CC0 packs (ambientCG etc.) under the same names and
        /// rebuild scenes to swap the whole look.</summary>
        static Material Mat(string name, Color c, string texSet, float smoothness, float metallic, float tiling)
        {
            string path = $"Assets/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find("ParkShift/CartoonSurface") ?? Shader.Find("Standard");
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            var toon = Shader.Find("ParkShift/CartoonSurface");
            if (toon != null) m.shader = toon;
            m.color = c;
            m.SetFloat("_Glossiness", smoothness);  // Standard
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness); // URP
            m.SetFloat("_Metallic", metallic);
            // Emission hygiene: rebuilds must be able to demote a formerly-glowing mat.
            m.DisableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);

            if (!string.IsNullOrEmpty(texSet))
            {
                var albedo = LoadTexture($"Assets/Textures/{texSet}_albedo.png", false);
                var normal = LoadTexture($"Assets/Textures/{texSet}_normal.png", true);
                m.mainTexture = albedo;
                m.mainTextureScale = Vector2.one * tiling;
                if (normal != null)
                {
                    m.SetTexture("_BumpMap", normal);
                    m.SetTextureScale("_BumpMap", Vector2.one * tiling);
                    m.EnableKeyword("_NORMALMAP");
                }
            }
            else
            {
                m.mainTexture = null;
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Flat material with an HDR emissive tint (neon trim, lamp bulbs, signage,
        /// crystals). Realtime-rendered emission only — flagged so it never contributes to GI
        /// and nothing gets baked.</summary>
        static Material GlowMat(string name, Color baseCol, Color emission, float intensity)
        {
            var m = Mat(name, baseCol);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission * intensity);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(m);
            return m;
        }

        const string DeweyPath = "Assets/Models/Dewey.fbx";

        static GameObject Prop(string file) =>
            AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/{file}");

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Instantiates a prop FBX under a wrapper GameObject. The wrapper takes all
        /// game-driven position/rotation; the FBX instance keeps its imported axis-compensation
        /// rotation as local state (single-root Blender FBXs carry ~-90X on their root node —
        /// overwriting it makes props lie down).</summary>
        static GameObject PlaceProp(GameObject prefab, string name, Vector3 pos, float yRot = 0f)
        {
            var wrapper = new GameObject(name);
            wrapper.transform.position = pos;
            wrapper.transform.rotation = Quaternion.Euler(0, yRot, 0);
            var inst = (GameObject)Object.Instantiate(prefab);
            inst.name = name + "_Model";
            inst.transform.SetParent(wrapper.transform, false); // preserves the prefab's import pose
            return wrapper;
        }

        static void SwapMaterial(GameObject go, string matNameContains, Material replacement)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                bool dirty = false;
                for (int k = 0; k < mats.Length; k++)
                    if (mats[k] != null && mats[k].name.Contains(matNameContains))
                    { mats[k] = replacement; dirty = true; }
                if (dirty) r.sharedMaterials = mats;
            }
        }

        /// <summary>Adds a raycast-interaction volume. The collider is a TRIGGER on purpose:
        /// the player interacts by looking at it (camera raycast), and a trigger is still hit by
        /// raycasts but never physically pushes the CharacterController — so standing/walking in
        /// front of an interactable can never shove the player. Pass <paramref name="center"/> to
        /// place the volume on the visible part when the mesh origin sits elsewhere (e.g. levers
        /// whose pivot is at the floor).</summary>
        static void AddInteractor(Transform part, PanelInteractable.Kind kind, Vector3 colliderSize, Vector3 center = default, bool glow = true)
        {
            if (part == null) return;
            var bc = part.gameObject.AddComponent<BoxCollider>();
            bc.size = colliderSize;
            bc.center = center;
            bc.isTrigger = true;
            part.gameObject.AddComponent<PanelInteractable>().PanelKind = kind;
            // Interactable affordance: subtle idle emission breathing + gaze highlight
            // (skipped for props that drive their own light, e.g. the dispatch push button).
            if (glow && part.GetComponent<InteractableGlow>() == null)
                part.gameObject.AddComponent<InteractableGlow>();
        }

        /// <summary>Configures the Dewey FBX (looping Walk/Idle/Sit clips) and returns the prefab.</summary>
        static GameObject SetupDeweyModel()
        {
            var importer = AssetImporter.GetAtPath(DeweyPath) as ModelImporter;
            if (importer == null) return null; // model not exported yet — capsule fallback
            string[] wanted = { "Walk", "Idle", "Sit" };
            var clips = importer.defaultClipAnimations;
            bool ok = importer.clipAnimations.Length == wanted.Length
                   && importer.clipAnimations.All(c => wanted.Contains(c.name) && c.loopTime);
            if (!ok)
            {
                foreach (var c in clips)
                {
                    foreach (var n in wanted)
                        if (c.name.Contains(n)) { c.name = n; break; }
                    c.loopTime = true;
                }
                importer.clipAnimations = clips;
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.importAnimation = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(DeweyPath);
        }

        /// <summary>Animator controller with Idle (default) / Walk / Sit states; code crossfades by name.</summary>
        static RuntimeAnimatorController BuildGuestAnimator()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Animations"))
                AssetDatabase.CreateFolder("Assets", "Animations");
            string path = "Assets/Animations/DeweyGuest.controller";
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (ctrl == null) ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);

            var sm = ctrl.layers[0].stateMachine;
            foreach (var child in sm.states.ToArray()) sm.RemoveState(child.state);

            var clips = AssetDatabase.LoadAllAssetsAtPath(DeweyPath)
                .OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).ToList();
            foreach (var name in new[] { "Idle", "Walk", "Sit" })
            {
                var clip = clips.FirstOrDefault(c => c.name == name)
                        ?? clips.FirstOrDefault(c => c.name.Contains(name));
                var st = sm.AddState(name);
                st.motion = clip;
                if (name == "Idle") sm.defaultState = st;
            }
            EditorUtility.SetDirty(ctrl);
            return ctrl;
        }

        static Texture2D LoadTexture(string path, bool asNormal)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return null; // texture not present — flat color fallback
            if (asNormal && imp.textureType != TextureImporterType.NormalMap)
            {
                imp.textureType = TextureImporterType.NormalMap;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static GameObject Box(string name, Vector3 pos, Vector3 scale, Material mat, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            if (parent) go.transform.SetParent(parent, true);
            if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Cyl(string name, Vector3 pos, Vector3 scale, Material mat, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            if (parent) go.transform.SetParent(parent, true);
            if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Ball(string name, Vector3 pos, float diameter, Material mat, Transform parent = null, bool keepCollider = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * diameter;
            if (parent) go.transform.SetParent(parent, true);
            if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Flat-shaded low-poly cone mesh (trees, hills, crystals). Verts are not
        /// shared between faces so RecalculateNormals yields hard facets. The mesh embeds in
        /// the scene file on save — same pattern TrackMeshBuilder already relies on.</summary>
        static Mesh ConeMesh(int segments, float radius, float height)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var apex = new Vector3(0, height, 0);
            for (int s = 0; s < segments; s++)
            {
                float a0 = (s / (float)segments) * Mathf.PI * 2f;
                float a1 = ((s + 1) / (float)segments) * Mathf.PI * 2f;
                var p0 = new Vector3(Mathf.Cos(a0) * radius, 0, Mathf.Sin(a0) * radius);
                var p1 = new Vector3(Mathf.Cos(a1) * radius, 0, Mathf.Sin(a1) * radius);
                int b = verts.Count;
                verts.Add(apex); verts.Add(p1); verts.Add(p0);          // side, outward
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                b = verts.Count;
                verts.Add(Vector3.zero); verts.Add(p0); verts.Add(p1);  // base cap, downward
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            }
            var mesh = new Mesh();
            mesh.name = $"Cone_{segments}_{radius:0.0}x{height:0.0}";
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static GameObject Cone(string name, Vector3 pos, float radius, float height, int segments, Material mat, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            if (parent) go.transform.SetParent(parent, true);
            go.AddComponent<MeshFilter>().sharedMesh = ConeMesh(segments, radius, height);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // ================= GALAXY EXPRESS coaster infrastructure helpers =================

        /// <summary>Straight flight of steps from a top landing down to ground (y=0), descending
        /// along <paramref name="rot"/>'s forward, with stringer handrails on both sides. Step
        /// boxes keep their primitive colliders so the player can actually climb them.</summary>
        static void BuildStairs(string name, Vector3 top, Quaternion rot, Transform parent, Material stepMat, Material railMat)
        {
            var root = new GameObject(name);
            root.transform.position = top;
            root.transform.SetParent(parent, true);
            float h = Mathf.Max(0f, top.y);
            if (h < 0.15f) return;
            int n = Mathf.Max(1, Mathf.CeilToInt(h / 0.3f));
            float rise = h / n;
            const float run = 0.34f;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = top + rot * new Vector3(0f, 0f, (i + 0.5f) * run) + Vector3.down * ((i + 0.5f) * rise);
                var step = Box($"{name}_Step{i:00}", c, new Vector3(1.0f, rise + 0.03f, run + 0.05f), stepMat, root.transform);
                step.transform.rotation = rot;
            }
            foreach (float sideX in new[] { -0.55f, 0.55f })
            {
                Vector3 a = top + rot * new Vector3(sideX, 1.0f, 0f);
                Vector3 b = top + rot * new Vector3(sideX, 1.0f, n * run) + Vector3.down * h;
                var rail = Box($"{name}_Rail{(sideX < 0 ? "L" : "R")}", (a + b) * 0.5f,
                               new Vector3(0.05f, 0.05f, Vector3.Distance(a, b) + 0.1f), railMat, root.transform);
                rail.transform.rotation = Quaternion.LookRotation(b - a);
            }
        }

        /// <summary>Maintenance catwalk hugging the spline between arc distances d0..d1 (the lift
        /// hill): deck plates with walk-on colliders, brackets back to the spine, outer handrail,
        /// and a stair flight from the lift base down to ground. Deck stays plumb (level
        /// side-to-side) even where the track pitches, like a real chain-lift walkway.</summary>
        static void BuildLiftCatwalk(TrackPath path, float d0, float d1, Transform parent,
                                     Material deckMat, Material railMat, Material postMat)
        {
            var root = new GameObject("LiftCatwalk");
            root.transform.SetParent(parent, false);
            float span = d1 - d0;
            if (span < 1f) return;
            int steps = Mathf.CeilToInt(span / 1.4f);
            float segLen = span / steps;
            Vector3 prevRailTop = Vector3.zero;
            for (int i = 0; i < steps; i++)
            {
                float d = d0 + (i + 0.5f) * segLen;
                path.FrameAtDistance(d, out var pos, out var rot, out _);
                Vector3 fwd = rot * Vector3.forward;
                Quaternion frame = Quaternion.LookRotation(fwd, Vector3.up);
                Vector3 side = frame * Vector3.left;   // west side — the walkway faces the park
                // de-overlap: deck offset 1.15 -> 1.4 — the old inner edge sat at 0.75m from the
                // rail centerline, exactly the train winglet half-width (zero clearance to the
                // 1.9m-wide sweep envelope). Inner edge now 1.0m (> 0.95 envelope half-width).
                Vector3 deckC = pos + side * 1.4f + Vector3.down * 0.35f;
                var deck = Box($"CatwalkDeck{i:00}", deckC, new Vector3(0.8f, 0.06f, segLen + 0.12f), deckMat, root.transform);
                deck.transform.rotation = frame;
                // de-overlap: bracket center 0.55 -> 0.62, length 1.0 -> 0.86 — the old inner end
                // (0.05 from centerline) stabbed through the track support columns (0.13 half-width).
                var bracket = Box($"CatwalkBracket{i:00}", pos + side * 0.62f + Vector3.down * 0.42f,
                                  new Vector3(0.86f, 0.07f, 0.12f), postMat, root.transform);
                bracket.transform.rotation = frame;
                Vector3 postC = deckC + side * 0.36f;
                var post = Box($"CatwalkPost{i:00}", postC + Vector3.up * 0.55f, new Vector3(0.06f, 1.1f, 0.06f), railMat, root.transform);
                post.transform.rotation = frame;
                Vector3 railTop = postC + Vector3.up * 1.08f;
                if (i > 0)
                {
                    var railSeg = Box($"CatwalkRail{i:00}", (prevRailTop + railTop) * 0.5f,
                                      new Vector3(0.05f, 0.05f, Vector3.Distance(prevRailTop, railTop) + 0.06f), railMat, root.transform);
                    railSeg.transform.rotation = Quaternion.LookRotation(railTop - prevRailTop);
                }
                prevRailTop = railTop;
            }
            // Stairs down from the lift base landing, descending to the walkway side (left of
            // travel) so the flight never crosses back over the pre-lift track.
            path.FrameAtDistance(d0, out var basePos, out var baseRot, out _);
            Vector3 baseFwd = baseRot * Vector3.forward;
            baseFwd.y = 0f;
            Quaternion baseYaw = Quaternion.LookRotation(baseFwd.normalized, Vector3.up);
            // de-overlap: stair-top offset -1.15 -> -1.4 to stay aligned with the widened deck.
            BuildStairs("LiftStairs", basePos + baseYaw * new Vector3(-1.4f, 0f, 0f) + Vector3.down * 0.35f,
                        baseYaw * Quaternion.Euler(0f, 270f, 0f), root.transform, deckMat, railMat);
        }

        /// <summary>Evac walkway beside the track at arc distance dCenter (sideSign +1 =
        /// right-hand side of travel, -1 = left): deck + handrails + neon edge strip + stairs to
        /// ground (or a single step when the shelf sits at grade) + a look-to-reveal nameplate
        /// matching the block number.</summary>
        static void BuildEvacPlatform(string name, TrackPath path, float dCenter, float length, Transform parent,
                                      Material deckMat, Material railMat, Material accentMat, string label,
                                      float sideSign, float sideOffset = 1.35f, float stepEndSign = 1f)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            path.FrameAtDistance(dCenter, out var pos, out var rot, out _);
            Vector3 fwd = rot * Vector3.forward;
            fwd.y = 0f;
            Quaternion yaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            Vector3 side = yaw * Vector3.right * Mathf.Sign(sideSign);
            // de-overlap: deck drop 0.25 -> 0.40 — the old deck top (rail -0.20) and neon edge
            // strip clipped the underside of the track cross-ties (tie bottoms at rail -0.225);
            // at 0.40 the deck top (rail -0.35) and strip top (rail -0.285) clear them.
            // de-overlap: lateral offset and the grade-step end are now parameters — B4 needs a
            // wider stand-off (banked approach) and its step on the rear end (front end faces
            // the station columns); B3 keeps the 1.35 offset and front step via the defaults.
            Vector3 deckC = pos + side * sideOffset + Vector3.down * 0.40f;
            root.transform.position = deckC;
            var deck = Box($"{name}_Deck", deckC, new Vector3(1.5f, 0.1f, length), deckMat, root.transform);
            deck.transform.rotation = yaw;
            for (int i = 0; i <= 4; i++)
            {
                var post = Box($"{name}_Post{i}", deckC + side * 0.68f + yaw * new Vector3(0f, 0f, -length * 0.5f + i * (length / 4f)) + Vector3.up * 0.5f,
                               new Vector3(0.06f, 1.0f, 0.06f), railMat, root.transform);
                post.transform.rotation = yaw;
            }
            var railTop = Box($"{name}_TopRail", deckC + side * 0.68f + Vector3.up * 1.0f, new Vector3(0.06f, 0.06f, length), railMat, root.transform);
            railTop.transform.rotation = yaw;
            var railMid = Box($"{name}_MidRail", deckC + side * 0.68f + Vector3.up * 0.6f, new Vector3(0.05f, 0.05f, length), railMat, root.transform);
            railMid.transform.rotation = yaw;
            var strip = Box($"{name}_EdgeStrip", deckC - side * 0.72f + Vector3.up * 0.08f, new Vector3(0.07f, 0.07f, length), accentMat, root.transform);
            strip.transform.rotation = yaw;
            if (deckC.y > 1.2f)
            {
                // de-overlap: stair flight shifted 0.35 to the deck's outer (away-from-track)
                // side — centered on the deck, the track-side stair stringer ran at 0.80m from
                // the rail centerline and its top 1.5m clipped the 0.95m sweep envelope where
                // the MCBR shelf still carries the train (B3, arc z~41..42). At +0.35 outboard
                // the inner stringer sits at 1.15m: clear, and still fully on the 1.5m deck.
                BuildStairs($"{name}_Stairs", deckC + side * 0.35f + yaw * new Vector3(0f, 0f, length * 0.5f), yaw, root.transform, deckMat, railMat);
            }
            else
            {
                var step = Box($"{name}_Step", deckC + yaw * new Vector3(0f, 0f, stepEndSign * (length * 0.5f + 0.35f)) + Vector3.down * (deckC.y * 0.5f),
                               new Vector3(1.0f, Mathf.Max(0.1f, deckC.y), 0.6f), deckMat, root.transform);
                step.transform.rotation = yaw;
            }
            int legs = Mathf.Max(2, Mathf.CeilToInt(length / 3.5f));
            for (int i = 0; i < legs; i++)
            {
                Vector3 lc = deckC + yaw * new Vector3(0f, 0f, -length * 0.5f + (i + 0.5f) * (length / legs));
                if (lc.y > 0.4f)
                {
                    var leg = Box($"{name}_Leg{i}", new Vector3(lc.x, lc.y * 0.5f, lc.z), new Vector3(0.16f, lc.y, 0.16f), railMat, root.transform);
                    leg.transform.rotation = yaw;
                }
            }
            Label(root, label, 1.6f);
        }

        /// <summary>Big vertical ring brace hugging the loop (segments around a circle in the
        /// world z-y plane, normal = x axis) with two ground legs + footers and a ring of
        /// pulsing neon orbs — the loop's "external support" since plumb columns can't drop
        /// from inverted spine. <paramref name="center"/> is the loop hub.</summary>
        static void BuildLoopRing(Vector3 center, float radius, int segments, Transform parent, Material mat, Material orbMat)
        {
            var root = new GameObject("LoopRing");
            root.transform.position = center;
            root.transform.SetParent(parent, true);
            float segLen = 2f * Mathf.PI * radius / segments;
            for (int i = 0; i < segments; i++)
            {
                float a = (i + 0.5f) / segments * Mathf.PI * 2f;
                Vector3 c = center + new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a)) * radius;
                var seg = Box($"RingSeg{i:00}", c, new Vector3(0.4f, 0.4f, segLen + 0.08f), mat, root.transform);
                seg.transform.rotation = Quaternion.LookRotation(new Vector3(0f, Mathf.Cos(a), -Mathf.Sin(a)), Vector3.right);
            }
            foreach (float s in new[] { -1f, 1f })
            {
                Vector3 anchor = center + new Vector3(0f, -0.72f, 0.72f * s) * radius;
                if (anchor.y > 0.2f)
                {
                    Box($"RingLeg{(s < 0 ? "S" : "N")}", new Vector3(anchor.x, anchor.y * 0.5f, anchor.z),
                        new Vector3(0.45f, anchor.y, 0.45f), mat, root.transform);
                    Box($"RingFoot{(s < 0 ? "S" : "N")}", new Vector3(anchor.x, 0.12f, anchor.z),
                        new Vector3(1.1f, 0.24f, 1.1f), mat, root.transform);
                }
            }
            var ringNeon = new GameObject("RingNeon");
            ringNeon.transform.SetParent(root.transform, false);
            for (int i = 0; i < 6; i++)
            {
                float a = i / 6f * Mathf.PI * 2f;
                Ball($"RingOrb{i}", center + new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a)) * (radius + 0.45f), 0.5f, orbMat, ringNeon.transform);
            }
            var pulse = ringNeon.AddComponent<PSEnv.NeonPulse>();
            pulse.EmissionColor = new Color(1f, 0.3f, 0.85f);
            pulse.Intensity = 2.2f; pulse.PulseAmount = 0.25f; pulse.Speed = 1.1f; pulse.Phase = 0.7f;
        }

        /// <summary>Neon ring gate around the track (ring plane normal = world x axis): a
        /// 10-segment box ring + slender ground legs, all colliderless decor, running one
        /// NeonPulse. Optional always-on marquee text floats above (both faces).</summary>
        static void BuildRingGate(string name, Vector3 center, float radius, Transform parent,
                                  Material mat, Color pulseColor, float phase, string marquee)
        {
            var root = new GameObject(name);
            root.transform.position = center;
            root.transform.SetParent(parent, true);
            const int segs = 10;
            float segLen = 2f * Mathf.PI * radius / segs;
            for (int i = 0; i < segs; i++)
            {
                float a = (i + 0.5f) / segs * Mathf.PI * 2f;
                var seg = Box($"{name}_Seg{i:00}", center + new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a)) * radius,
                              new Vector3(0.22f, 0.22f, segLen + 0.06f), mat, root.transform);
                seg.transform.rotation = Quaternion.LookRotation(new Vector3(0f, Mathf.Cos(a), -Mathf.Sin(a)), Vector3.right);
                Object.DestroyImmediate(seg.GetComponent<Collider>()); // decor — never blocks a look-ray
            }
            foreach (float s in new[] { -1f, 1f })
            {
                Vector3 anchor = center + new Vector3(0f, -0.7f, 0.7f * s) * radius;
                if (anchor.y > 0.3f)
                {
                    var leg = Box($"{name}_Leg{(s < 0 ? "S" : "N")}", new Vector3(anchor.x, anchor.y * 0.5f, anchor.z),
                                  new Vector3(0.18f, anchor.y, 0.18f), mat, root.transform);
                    Object.DestroyImmediate(leg.GetComponent<Collider>());
                }
            }
            var pulse = root.AddComponent<PSEnv.NeonPulse>();
            pulse.EmissionColor = pulseColor;
            pulse.Intensity = 2.3f; pulse.PulseAmount = 0.3f; pulse.Speed = 1.4f; pulse.Phase = phase;
            if (!string.IsNullOrEmpty(marquee))
            {
                foreach (float face in new[] { -1f, 1f })
                {
                    var tgo = new GameObject($"{name}_Marquee{(face < 0 ? "W" : "E")}");
                    tgo.transform.SetParent(root.transform, true);
                    tgo.transform.position = center + new Vector3(0.14f * face, radius + 0.9f, 0f);
                    tgo.transform.rotation = Quaternion.Euler(0f, face < 0 ? 270f : 90f, 0f);
                    var tm = tgo.AddComponent<TextMesh>();
                    tm.text = marquee;
                    tm.characterSize = 0.22f; tm.fontSize = 48; tm.fontStyle = FontStyle.Bold;
                    tm.anchor = TextAnchor.MiddleCenter;
                    tm.color = new Color(0.45f, 0.95f, 1f);
                }
            }
        }

        /// <summary>Shared deep-space night mood for every scene (GALAXY EXPRESS theme):
        /// gradient skybox material (custom ParkShift/GradientSkybox — near-black top,
        /// dark-indigo horizon, dense stars and a big ringed planet low over the course;
        /// Skybox/Procedural tinted dark as fallback), cool trilight ambient and deep
        /// indigo distance fog pushed far enough out that the lift/loop skyline reads
        /// from the booth. Realtime-only; nothing here bakes. Reuses the existing
        /// Assets/Materials/Skybox.mat asset.</summary>
        static Material BuildDuskEnvironment(Light sun)
        {
            const string skyPath = "Assets/Materials/Skybox.mat";
            var grad = Shader.Find("ParkShift/GradientSkybox");
            var shader = grad != null ? grad : Shader.Find("Skybox/Procedural");
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null)
            {
                sky = new Material(shader);
                AssetDatabase.CreateAsset(sky, skyPath);
            }
            sky.shader = shader;
            if (grad != null)
            {
                sky.SetColor("_TopColor", new Color(0.12f, 0.18f, 0.42f));
                sky.SetColor("_HorizonColor", new Color(0.94f, 0.53f, 0.44f));
                sky.SetColor("_BottomColor", new Color(0.32f, 0.30f, 0.48f));
                sky.SetFloat("_HorizonHeight", 0.0f);
                sky.SetFloat("_HorizonFalloff", 2.2f);
                sky.SetFloat("_StarDensity", 0.85f);
                sky.SetFloat("_StarBrightness", 2.6f);
                // Big ringed planet hanging low to the north-west — right over the coaster
                // skyline (lift/loop/camel-backs) as seen from the operator booth.
                sky.SetVector("_PlanetDir", new Vector4(-0.50f, 0.13f, 0.85f, 0f));
                sky.SetColor("_PlanetColor", new Color(0.62f, 0.70f, 0.86f));
                sky.SetFloat("_PlanetCos", 0.9915f);
                sky.SetColor("_HaloColor", new Color(0.35f, 0.55f, 0.95f));
                sky.SetColor("_RingColor", new Color(0.82f, 0.76f, 0.66f));
                sky.SetVector("_RingTilt", new Vector4(0.30f, 0.90f, 0.25f, 0f));
                sky.SetFloat("_RingInner", 1.4f);
                sky.SetFloat("_RingOuter", 2.3f);
                sky.SetFloat("_RingIntensity", 0.9f);
            }
            else
            {
                sky.SetFloat("_SunSize", 0.04f);
                sky.SetFloat("_AtmosphereThickness", 0.45f);
                sky.SetFloat("_Exposure", 0.8f);
                sky.SetColor("_SkyTint", new Color(0.18f, 0.20f, 0.42f));
                sky.SetColor("_GroundColor", new Color(0.05f, 0.05f, 0.10f));
            }
            EditorUtility.SetDirty(sky);

            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.50f, 0.60f, 0.80f);
            RenderSettings.ambientEquatorColor = new Color(0.43f, 0.34f, 0.52f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.20f, 0.30f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 85f;
            RenderSettings.fogEndDistance = 270f;
            RenderSettings.fogColor = new Color(0.34f, 0.36f, 0.61f);
            return sky;
        }

        /// <summary>Cool low moon/starlight key shared by both scenes — dim blue so the neon
        /// and warm station practicals carry the scene without it going unreadably dark.</summary>
        static Light MakeDuskSun()
        {
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(32f, -30f, 0);
            sun.intensity = 1.15f;
            sun.color = new Color(1f, 0.88f, 0.72f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            return sun;
        }

        /// <summary>World-space nameplate, look-to-reveal: hidden by default and faded in by the
        /// player's LabelLookSystem while the camera rests on the prop (the LookLabel on the prop
        /// root owns the fade + billboard; any child collider hit resolves to it). Props with no
        /// collider at all (pure indicators) get a snug trigger box fitted to their renderers so
        /// the look-ray has something to hit — fitted BEFORE the text mesh exists so the fit never
        /// includes the label's own bounds.</summary>
        static void Label(GameObject target, string text, float yOffset = 0.8f)
        {
            if (target.GetComponentInChildren<Collider>() == null)
            {
                var rends = target.GetComponentsInChildren<Renderer>();
                if (rends.Length > 0)
                {
                    var b = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                    var fit = target.AddComponent<BoxCollider>();
                    fit.isTrigger = true;   // label proxy only — never shoves the player
                    fit.center = target.transform.InverseTransformPoint(b.center);
                    var ls = target.transform.lossyScale;
                    fit.size = new Vector3(b.size.x / Mathf.Max(ls.x, 1e-4f),
                                           b.size.y / Mathf.Max(ls.y, 1e-4f),
                                           b.size.z / Mathf.Max(ls.z, 1e-4f));
                }
            }

            var go = new GameObject(target.name + "_Label");
            go.transform.SetParent(target.transform, false);
            go.transform.localPosition = Vector3.up * yOffset;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text; tm.characterSize = 0.12f; tm.fontSize = 48;
            tm.anchor = TextAnchor.MiddleCenter; tm.color = Color.white;

            var look = target.GetComponent<LookLabel>();
            if (look == null) look = target.AddComponent<LookLabel>();
            look.Text = tm;
            go.SetActive(false);   // hidden until the player looks at the prop
        }

        /// <summary>Floor-standing OCC (operations control center) operator pedestal — the shared
        /// family shape for the unload-side ride controls: base plinth with an emissive accent
        /// trim strip, angled stem, and a wedge console head with a ~35°-raked face carrying an
        /// accent face plate, a press cap and an emissive status dot. Dark hull material + one
        /// per-function accent color keeps the family consistent while the function reads at a
        /// glance. The PanelInteractable trigger volume hugs the console HEAD only, so
        /// neighbouring pedestals never steal the player's aim. <paramref name="faceYaw"/> is the
        /// world yaw the raked face looks toward; <paramref name="guarded"/> adds side rails and
        /// a shroud hood for guarded controls (restraint release). <paramref name="pressCap"/>
        /// returns the cap transform for a PropDrivers PressPulse (its local Z is the face
        /// normal, so the press squashes into the console). Only the plinth keeps a solid
        /// collider; every other part is visual or the head's trigger.</summary>
        static GameObject BuildOpsPedestal(string name, Vector3 pos, float faceYaw,
            Material hullMat, Material accentMat, PanelInteractable.Kind kind, string labelText,
            bool guarded, Transform parent, out Transform pressCap)
        {
            var root = new GameObject(name);
            root.transform.position = pos;
            root.transform.rotation = Quaternion.Euler(0f, faceYaw, 0f);
            if (parent != null) root.transform.SetParent(parent, true);
            Quaternion q = root.transform.rotation;
            System.Func<Vector3, Vector3> W = lp => pos + q * lp;   // pedestal-local -> world

            // Base plinth (the pedestal's only solid, walk-blocking collider) + accent trim strip.
            var plinth = Box($"{name}_Plinth", W(new Vector3(0f, 0.07f, 0f)), new Vector3(0.7f, 0.14f, 0.55f), hullMat, root.transform);
            plinth.transform.rotation = q;
            var trim = Box($"{name}_TrimStrip", W(new Vector3(0f, 0.155f, 0f)), new Vector3(0.72f, 0.03f, 0.57f), accentMat, root.transform);
            trim.transform.rotation = q;
            Object.DestroyImmediate(trim.GetComponent<Collider>());

            // Angled stem leaning toward the operator.
            var stem = Box($"{name}_Stem", W(new Vector3(0f, 0.55f, 0f)), new Vector3(0.3f, 0.8f, 0.24f), hullMat, root.transform);
            stem.transform.rotation = q * Quaternion.Euler(12f, 0f, 0f);
            Object.DestroyImmediate(stem.GetComponent<Collider>());

            // Wedge console head, face raked 35° up from horizontal toward the operator.
            Vector3 headC = new Vector3(0f, 1.02f, 0.08f);
            Vector3 fn = new Vector3(0f, Mathf.Cos(35f * Mathf.Deg2Rad), Mathf.Sin(35f * Mathf.Deg2Rad)); // face normal (local)
            var head = Box($"{name}_Head", W(headC), new Vector3(0.62f, 0.26f, 0.5f), hullMat, root.transform);
            head.transform.rotation = q * Quaternion.Euler(35f, 0f, 0f);
            Object.DestroyImmediate(head.GetComponent<Collider>());
            var plate = Box($"{name}_FacePlate", W(headC + fn * 0.14f), new Vector3(0.5f, 0.02f, 0.36f), accentMat, root.transform);
            plate.transform.rotation = q * Quaternion.Euler(35f, 0f, 0f);
            Object.DestroyImmediate(plate.GetComponent<Collider>());
            var cap = Box($"{name}_PressCap", W(headC + fn * 0.185f), new Vector3(0.18f, 0.18f, 0.06f), accentMat, root.transform);
            cap.transform.rotation = q * Quaternion.Euler(-55f, 0f, 0f); // local Z = face normal — PressPulse squashes into the face
            Object.DestroyImmediate(cap.GetComponent<Collider>());
            pressCap = cap.transform;

            // Emissive status dot on the head crown.
            Ball($"{name}_StatusDot", W(headC + new Vector3(0f, 0.17f, -0.14f)), 0.08f, accentMat, root.transform);

            if (guarded)
            {
                // Guarded look: side rails flanking the head + a shroud hood over the face's top edge.
                foreach (float sx in new[] { -0.37f, 0.37f })
                {
                    var rail = Box($"{name}_Guard{(sx < 0 ? "L" : "R")}", W(headC + new Vector3(sx, 0.02f, 0f)),
                                   new Vector3(0.05f, 0.34f, 0.56f), hullMat, root.transform);
                    rail.transform.rotation = q * Quaternion.Euler(35f, 0f, 0f);
                    Object.DestroyImmediate(rail.GetComponent<Collider>());
                }
                var hood = Box($"{name}_Shroud", W(headC + fn * 0.14f + new Vector3(0f, 0.15f, -0.21f)),
                               new Vector3(0.66f, 0.04f, 0.2f), hullMat, root.transform);
                hood.transform.rotation = q * Quaternion.Euler(35f, 0f, 0f);
                Object.DestroyImmediate(hood.GetComponent<Collider>());
            }

            // Interaction trigger on the HEAD only (head-local axes, so the volume tilts with
            // the rake and stays snug — no overlap with the neighbouring pedestals).
            AddInteractor(head.transform, kind, new Vector3(0.75f, 0.55f, 0.6f), new Vector3(0f, 0.06f, 0.02f));
            Label(root, labelText, 1.7f);
            return root;
        }

        [MenuItem("ParkShift/Build All Scenes")]
        public static void BuildAll()
        {
            BuildMainMenu();
            BuildPrototypeAttraction();
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene("Assets/Scenes/MainMenu.unity", true),
                new EditorBuildSettingsScene("Assets/Scenes/PrototypeAttraction.unity", true),
            };
            // Post-build overlap audit on the freshly saved attraction scene (it is still the
            // open scene here). Best-effort: an audit failure must never break a scene build.
            try { SceneAudit.RunAudit(); }
            catch (System.Exception e) { Debug.LogWarning("[SceneAudit] post-build audit failed: " + e.Message); }
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("ParkShift", "Scenes built.\nOpen Assets/Scenes/PrototypeAttraction.unity and press Play,\nor play MainMenu for the menu flow.", "OK");
        }

        static void BuildMainMenu()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("MainMenuUI").AddComponent<UI.MainMenuUI>();
            // Same dusk/space mood as the park so the menu backdrop isn't a flat color card.
            BuildDuskEnvironment(MakeDuskSun());
            var camGO = new GameObject("Camera");
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = new Color(0.06f, 0.05f, 0.15f); // fallback if skybox is missing
            // Aim at the horizon band so stars, gradient and the planet all land in frame.
            camGO.transform.rotation = Quaternion.Euler(-10f, -28f, 0);
            camGO.AddComponent<AudioListener>();
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/MainMenu.unity");
        }

        static void BuildPrototypeAttraction()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- Palette: flat solid-color Standard materials (low smoothness) for the
            // stylized Overcooked/Astroneer-with-a-cosmic-twist look. Existing material
            // assets keep their names/paths (other scripts may reference them) — they are
            // simply re-tinted flat. Pass a texSet string again to go back to textured PBR.
            var mFloor    = Mat("Floor",    new Color(0.24f, 0.22f, 0.35f), null, 0.12f, 0f, 1f); // indigo plaza
            var mPlatform = Mat("Platform", new Color(0.89f, 0.84f, 0.74f), null, 0.15f, 0f, 1f); // warm cream
            var mTrack    = Mat("Track",    new Color(0.58f, 0.30f, 0.85f), null, 0.30f, 0.15f, 1f); // cosmic purple
            var mTrainA   = Mat("TrainA",   new Color(0.98f, 0.52f, 0.12f), null, 0.45f, 0.10f, 1f); // hero orange
            var mTrainB   = Mat("TrainB",   new Color(0.12f, 0.62f, 0.95f), null, 0.45f, 0.10f, 1f); // hero blue
            var mPanel    = Mat("Panel",    new Color(0.13f, 0.62f, 0.45f), null, 0.25f, 0f, 1f);
            var mDanger   = Mat("Danger",   new Color(0.90f, 0.20f, 0.25f), null, 0.25f, 0f, 1f);
            var mGate     = Mat("Gate",     new Color(0.98f, 0.78f, 0.18f), null, 0.30f, 0f, 1f);  // sunny yellow
            var mGuest    = Mat("Guest",    new Color(0.82f, 0.80f, 0.88f));
            var mBooth    = Mat("Booth",    new Color(0.36f, 0.38f, 0.58f), null, 0.18f, 0f, 1f);  // dusk slate
            var mRail     = Mat("Rail",     new Color(0.75f, 0.78f, 0.92f), null, 0.55f, 0.60f, 1f); // pale steel
            var mTie      = Mat("Tie",      new Color(0.20f, 0.17f, 0.30f), null, 0.12f, 0f, 1f);  // dark violet
            var mSeat     = Mat("Seat",     new Color(0.16f, 0.15f, 0.22f), null, 0.30f, 0f, 1f);
            var mRoof     = Mat("Roof",     new Color(0.42f, 0.22f, 0.62f), null, 0.20f, 0f, 1f);  // grape canopy

            // Environment dressing palette (new assets, all Assets/Materials/Env_*)
            var mWhite    = Mat("Env_White",    new Color(0.93f, 0.92f, 0.96f), null, 0.20f, 0f, 1f);
            var mPathA    = Mat("Env_PathA",    new Color(0.95f, 0.44f, 0.32f), null, 0.15f, 0f, 1f); // coral
            var mPathB    = Mat("Env_PathB",    new Color(0.15f, 0.68f, 0.66f), null, 0.15f, 0f, 1f); // teal
            var mPlanter  = Mat("Env_Planter",  new Color(0.55f, 0.36f, 0.52f), null, 0.15f, 0f, 1f);
            var mSoil     = Mat("Env_Soil",     new Color(0.23f, 0.16f, 0.20f), null, 0.05f, 0f, 1f);
            var mTrunk    = Mat("Env_Trunk",    new Color(0.42f, 0.28f, 0.24f), null, 0.10f, 0f, 1f);
            var mFoliageA = Mat("Env_FoliageA", new Color(0.10f, 0.48f, 0.46f), null, 0.10f, 0f, 1f); // alien teal
            var mFoliageB = Mat("Env_FoliageB", new Color(0.38f, 0.26f, 0.60f), null, 0.10f, 0f, 1f); // nebula violet
            var mRock     = Mat("Env_Rock",     new Color(0.36f, 0.34f, 0.48f), null, 0.10f, 0f, 1f);
            var mHill     = Mat("Env_Hill",     new Color(0.09f, 0.08f, 0.16f), null, 0.05f, 0f, 1f); // crater ridge
            var mHillFar  = Mat("Env_HillFar",  new Color(0.13f, 0.12f, 0.22f), null, 0.05f, 0f, 1f);
            var mLampPole = Mat("Env_LampPole", new Color(0.20f, 0.20f, 0.28f), null, 0.30f, 0.20f, 1f);
            var mNeonCyan    = GlowMat("Env_NeonCyan",    new Color(0.10f, 0.35f, 0.38f), new Color(0.25f, 0.95f, 1f),   2.4f);
            var mNeonMagenta = GlowMat("Env_NeonMagenta", new Color(0.38f, 0.10f, 0.30f), new Color(1f, 0.30f, 0.85f),   2.4f);
            var mNeonPurple  = GlowMat("Env_NeonPurple",  new Color(0.22f, 0.12f, 0.42f), new Color(0.62f, 0.35f, 1f),   2.4f);
            var mLampGlow    = GlowMat("Env_LampGlow",    new Color(0.45f, 0.35f, 0.20f), new Color(1f, 0.75f, 0.45f),   2.0f);
            var mStringBulb  = GlowMat("Env_StringBulb",  new Color(0.45f, 0.38f, 0.18f), new Color(1f, 0.85f, 0.50f),   1.8f);
            var mCrystal     = GlowMat("Env_Crystal",     new Color(0.30f, 0.20f, 0.55f), new Color(0.65f, 0.40f, 1f),   1.5f);

            // Dewey guest model + animator (null-safe: capsule fallback if FBX is absent)
            var deweyPrefab = SetupDeweyModel();
            var deweyCtrl = deweyPrefab != null ? BuildGuestAnimator() : null;

            // Lighting — deep-space night: dim cool moonlight key with soft shadows, cold
            // trilight ambient, star-field skybox with a ringed planet, and indigo fog. Warm
            // practicals (canopy, booth, lamps) keep the playable station area readable.
            var sun = MakeDuskSun();
            BuildDuskEnvironment(sun);
            QualitySettings.shadowDistance = 70f;

            // Ground + station platform. The floor grew with the GALAXY EXPRESS footprint
            // (course reaches x≈-35 / z≈78); the plaza/queue south edge is unchanged.
            Box("Floor", new Vector3(0, -0.5f, 28), new Vector3(120, 1, 122), mFloor);
            Box("StationPlatform", new Vector3(0, 0.25f, 0), new Vector3(14, 0.5f, 3.5f), mPlatform);

            // ---- Access ramps: ground (0) <-> platform top (0.5), gentle ~13 degree plates ----
            // (lambdas instead of local functions throughout: the headless mcs 7.2 parser
            // used by CI chokes on local functions)
            System.Func<string, Vector3, Vector3, Vector3, GameObject> Ramp = (name, pos, scale, euler) =>
            {
                var r = Box(name, pos, scale, mPlatform);
                r.transform.rotation = Quaternion.Euler(euler);
                return r;
            };
            // Queue entry: proper station steps through the fence gap (visual), with an
            // invisible ramp collider so the CharacterController glides up smoothly.
            var stepsPrefab = Prop("StationSteps.fbx");
            if (stepsPrefab != null)
            {
                var steps = PlaceProp(stepsPrefab, "StationSteps", new Vector3(0, 0f, -2.245f));
                steps.transform.localScale = new Vector3(1f, 1f, 0.55f); // compress depth to clear rail row 0
                // standalone (not parented) so the wrapper's depth compression doesn't shrink it
                var rampCol = new GameObject("StepsRampCollider");
                rampCol.transform.position = new Vector3(0, 0.235f, -2.24f);
                rampCol.transform.rotation = Quaternion.Euler(-26f, 0, 0);
                var rcb = rampCol.AddComponent<BoxCollider>();
                rcb.size = new Vector3(1.6f, 0.1f, 1.18f);
            }
            else
            {
                Ramp("RampQueue", new Vector3(0, 0.24f, -2.78f), new Vector3(3.0f, 0.1f, 2.25f), new Vector3(-13.7f, 0, 0));
            }
            // Booth-side ramp (east end, rising west onto the platform)
            // de-overlap: both ramps narrowed z 3.0 -> 2.7 and recentered z 0 -> -0.15 — their
            // old north edge (z 1.5) reached under the track and the raised plate corner clipped
            // the cross-tie ends (ties span z 1.35..3.05 at y ~0.42). North edge now z 1.2.
            Ramp("RampBooth", new Vector3(8.15f, 0.24f, -0.15f), new Vector3(2.25f, 0.1f, 2.7f), new Vector3(0, 0, -13.7f));
            // Maintenance-side ramp (west end, rising east onto the platform)
            Ramp("RampMaint", new Vector3(-8.15f, 0.24f, -0.15f), new Vector3(2.25f, 0.1f, 2.7f), new Vector3(0, 0, 13.7f));

            // ---- Station architecture: grape canopy with sloped skirts on white columns,
            // exposed beams, pulsing neon edge trim and a double-sided marquee sign ----
            var stationRoot = new GameObject("StationDressing");
            Box("RoofSlab", new Vector3(0, 4.0f, 0.6f), new Vector3(15f, 0.25f, 6f), mRoof, stationRoot.transform);
            var skirtS = Box("RoofSkirtS", new Vector3(0, 3.9f, -2.95f), new Vector3(15.2f, 0.15f, 1.3f), mRoof, stationRoot.transform);
            skirtS.transform.rotation = Quaternion.Euler(-22f, 0, 0);
            var skirtN = Box("RoofSkirtN", new Vector3(0, 3.9f, 4.15f), new Vector3(15.2f, 0.15f, 1.3f), mRoof, stationRoot.transform);
            skirtN.transform.rotation = Quaternion.Euler(22f, 0, 0);
            // Exposed beams under the slab
            for (int bIdx = 0; bIdx < 5; bIdx++)
                Box($"RoofBeam{bIdx}", new Vector3(-6f + 3f * bIdx, 3.78f, 0.6f), new Vector3(0.18f, 0.18f, 5.6f), mWhite, stationRoot.transform);
            Box("RoofBeamSpine", new Vector3(0, 3.78f, 0.6f), new Vector3(14.6f, 0.16f, 0.16f), mWhite, stationRoot.transform);
            // Neon edge trim (one NeonPulse runs all four strips in sync)
            var roofNeon = new GameObject("RoofNeon");
            roofNeon.transform.SetParent(stationRoot.transform, false);
            Box("NeonEdgeS", new Vector3(0, 4.16f, -2.42f), new Vector3(15.2f, 0.09f, 0.09f), mNeonCyan, roofNeon.transform);
            Box("NeonEdgeN", new Vector3(0, 4.16f, 3.62f), new Vector3(15.2f, 0.09f, 0.09f), mNeonCyan, roofNeon.transform);
            Box("NeonEdgeW", new Vector3(-7.55f, 4.16f, 0.6f), new Vector3(0.09f, 0.09f, 6.1f), mNeonCyan, roofNeon.transform);
            Box("NeonEdgeE", new Vector3(7.55f, 4.16f, 0.6f), new Vector3(0.09f, 0.09f, 6.1f), mNeonCyan, roofNeon.transform);
            var roofPulse = roofNeon.AddComponent<PSEnv.NeonPulse>();
            roofPulse.EmissionColor = new Color(0.25f, 0.95f, 1f);
            roofPulse.Intensity = 2.4f; roofPulse.PulseAmount = 0.12f; roofPulse.Speed = 1.2f;
            // Columns: white shafts with grape base + capital
            // de-overlap: north column row moved z 3.1 -> 3.45 — at 3.1 the shafts sat 0.19m and
            // the bases 0.30m inside the train sweep envelope (z <= 3.15 at |y| 0.6..2.8); the
            // bases even overlapped the actual winglet swept width (train edge z 2.95). Bases
            // also slimmed 0.5 -> 0.44 so the south pair stops kissing fence post corners.
            // de-overlap: north-EAST column moved from x 6.8 to x 3.8 — the departure curve's
            // banking (BankAtDistance, clamps at 44 deg) starts rolling the train right at the
            // straight's end (x~6), and the rolled 1.9x2.2 rider envelope's top corner sweeps
            // out to z~4.5 by x 6.8: no column z under the roof line clears it. West of x~4.5
            // the track is still flat, so the NE shaft slides along the roof edge to x 3.8
            // (verified ~0.1m clearance against the banked sweep in the scratch model).
            foreach (var cc in new[] { new Vector3(-6.8f, 0f, -1.9f), new Vector3(6.8f, 0f, -1.9f),
                                       new Vector3(-6.8f, 0f, 3.45f), new Vector3(3.8f, 0f, 3.45f) })
            {
                float cx = cc.x, cz = cc.z;
                Cyl("RoofColumn", new Vector3(cx, 2f, cz), new Vector3(0.28f, 1.9f, 0.28f), mWhite, stationRoot.transform);
                Box("ColumnBase", new Vector3(cx, 0.4f, cz), new Vector3(0.44f, 0.8f, 0.44f), mRoof, stationRoot.transform);
                Box("ColumnCap", new Vector3(cx, 3.72f, cz), new Vector3(0.44f, 0.2f, 0.44f), mRoof, stationRoot.transform);
            }
            // ---- "GALAXY EXPRESS" marquee above the south (queue-side) roof edge ----
            var sign = new GameObject("StationSign");
            sign.transform.SetParent(stationRoot.transform, false);
            Box("SignBoard", new Vector3(0, 4.85f, -2.3f), new Vector3(8.6f, 1.15f, 0.22f), mBooth, sign.transform);
            Box("SignTrimTop", new Vector3(0, 5.5f, -2.3f), new Vector3(8.8f, 0.08f, 0.26f), mNeonMagenta, sign.transform);
            Box("SignTrimBot", new Vector3(0, 4.2f, -2.3f), new Vector3(8.8f, 0.08f, 0.26f), mNeonMagenta, sign.transform);
            Box("SignStrutL", new Vector3(-3.2f, 4.15f, -2.3f), new Vector3(0.12f, 0.55f, 0.12f), mWhite, sign.transform);
            Box("SignStrutR", new Vector3(3.2f, 4.15f, -2.3f), new Vector3(0.12f, 0.55f, 0.12f), mWhite, sign.transform);
            Ball("SignOrbL", new Vector3(-4.6f, 4.85f, -2.3f), 0.42f, mNeonMagenta, sign.transform);
            Ball("SignOrbR", new Vector3(4.6f, 4.85f, -2.3f), 0.42f, mNeonMagenta, sign.transform);
            System.Action<string, Vector3, float> SignText = (sName, sPos, sYRot) =>
            {
                var go = new GameObject(sName);
                go.transform.SetParent(sign.transform, true);
                go.transform.position = sPos;
                go.transform.rotation = Quaternion.Euler(0, sYRot, 0);
                var tm = go.AddComponent<TextMesh>();
                tm.text = "GALAXY EXPRESS";
                tm.characterSize = 0.16f; tm.fontSize = 50; tm.fontStyle = FontStyle.Bold;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = new Color(0.55f, 0.9f, 1f);
            };
            SignText("SignTextS", new Vector3(0, 4.85f, -2.45f), 0f);    // readable from the queue
            SignText("SignTextN", new Vector3(0, 4.85f, -2.15f), 180f);  // readable from the course
            var signPulse = sign.AddComponent<PSEnv.NeonPulse>();
            signPulse.EmissionColor = new Color(1f, 0.3f, 0.85f);
            signPulse.Intensity = 2.4f; signPulse.PulseAmount = 0.2f; signPulse.Speed = 0.9f; signPulse.Phase = 1.7f;
            // Warm canopy practical: deep-space key light is dim, so the platform work area
            // carries its own warm pool (matches the booth light; no shadows — cheap).
            var canopyLightGO = new GameObject("CanopyLight");
            canopyLightGO.transform.position = new Vector3(0, 3.4f, 0.6f);
            canopyLightGO.transform.SetParent(stationRoot.transform, true);
            var canopyLight = canopyLightGO.AddComponent<Light>();
            canopyLight.type = LightType.Point;
            canopyLight.range = 11f;
            canopyLight.intensity = 1.25f;
            canopyLight.color = new Color(1f, 0.8f, 0.58f);
            canopyLight.shadows = LightShadows.None;
            // Platform edge neon (helps night readability of the drop-off)
            Box("PlatformEdgeNeonS", new Vector3(0, 0.54f, -1.72f), new Vector3(14f, 0.06f, 0.06f), mNeonMagenta, stationRoot.transform);
            Box("PlatformEdgeNeonW", new Vector3(-6.97f, 0.54f, 0f), new Vector3(0.06f, 0.06f, 3.4f), mNeonMagenta, stationRoot.transform);
            Box("PlatformEdgeNeonE", new Vector3(6.97f, 0.54f, 0f), new Vector3(0.06f, 0.06f, 3.4f), mNeonMagenta, stationRoot.transform);
            // Platform back-edge fence (guests' side) with a center opening for the queue entry
            for (int f = 0; f < 8; f++)
            {
                float fx = -6.6f + f * 1.9f;
                if (Mathf.Abs(fx) < 1.6f) continue; // entry gap
                Box($"FencePost{f}", new Vector3(fx, 0.9f, -1.7f), new Vector3(0.08f, 0.8f, 0.08f), mWhite, stationRoot.transform);
            }
            foreach (float sideX in new[] { -4.3f, 4.3f })
            {
                Box($"FenceTopRail{(sideX < 0 ? "L" : "R")}", new Vector3(sideX, 1.28f, -1.7f), new Vector3(5.4f, 0.07f, 0.07f), mWhite, stationRoot.transform);
                Box($"FenceMidRail{(sideX < 0 ? "L" : "R")}", new Vector3(sideX, 0.95f, -1.7f), new Vector3(5.4f, 0.06f, 0.06f), mWhite, stationRoot.transform);
            }

            // ---- Track: GALAXY EXPRESS — 53-waypoint space coaster, 5 blocks. The station
            // straight (z=2.2, x -6..6) is byte-identical to the old layout so the platform,
            // gates, queue and booth all stay put: block 0 = UNLOAD (RV parks at x=-3) and
            // block 1 = LOAD (RV parks at x=+3). The course then runs: pre-lift turn → chain
            // LIFT HILL (block 2, crest ~22m — a held train rests on the chain, catwalk evac)
            // → first drop → VERTICAL LOOP (clothoid-ish: ~7.5m radius at the bottom, ~5.6m at
            // the top; entry/exit offset 3.6m in x so the bottom never self-intersects) →
            // rising banked turn → ZERO-G SECTOR (block 3: three floaty camel-backs) →
            // sweeping high banked turn → elevated MID-COURSE BRAKE RUN (flat + straight, evac
            // platform) → diving turn home (block 4) → ground-level FINAL BRAKE RUN into the
            // station. Block boundaries are data-driven (BlockStarts) and land only on
            // plausible stopping track: station straight, lift chain, and the two flat brake
            // shelves — never mid-loop or mid-hill. ----
            var trackGO = new GameObject("Track");
            var path = trackGO.AddComponent<TrackPath>();
            path.BlockCount = 5;
            path.SamplesPerSegment = 14;   // dense arc table — keeps loop framing smooth
            path.MaxBankDegrees = 44f;     // rising + high turns lean harder than the old 32°
            Vector3[] layout =
            {
                // 0-8 STATION straight (unchanged: Unload parks x=-3, Load parks x=+3)
                new Vector3(-6f, 0.6f, 2.2f), new Vector3(-4.5f, 0.6f, 2.2f), new Vector3(-3f, 0.6f, 2.2f),
                new Vector3(-1.5f, 0.6f, 2.2f), new Vector3(0f, 0.6f, 2.2f), new Vector3(1.5f, 0.6f, 2.2f),
                new Vector3(3f, 0.6f, 2.2f), new Vector3(4.5f, 0.6f, 2.2f), new Vector3(6f, 0.6f, 2.2f),
                // 9-16 BLOCK 2: pre-lift turn + chain lift (boundary at the crest).
                // WP 9 is an easing point a hair north of the straight's extension, on the turn
                // arc's tangent line: the exit tangent at WP 8 leaves at ~9° instead of the ~20°
                // the old (11, 0.9, 4.5) start forced, so the straight flows into the arc nearly
                // tangent-continuously. Measured on the centripetal spline this cuts the worst
                // wrong-way (reverse-curvature) rate at the station exit from -76.7 deg/m
                // (uniform CR + old WP 9 — the post-dispatch S-bend) to -7.1 deg/m, with <=2cm
                // lateral deviation left on the station straight itself.
                new Vector3(9f, 0.7f, 2.7f), new Vector3(14.5f, 1.5f, 7.5f), new Vector3(17f, 2.1f, 11f),
                new Vector3(18.4f, 2.6f, 13.5f),   // lift base — catwalk + stairs start here
                new Vector3(19f, 7f, 19.5f), new Vector3(19.4f, 12f, 25.5f),
                new Vector3(19.7f, 16.8f, 31.5f), new Vector3(19.9f, 20.6f, 37f),
                // 17 BLOCK 3 start: lift crest
                new Vector3(20f, 22.2f, 41f),
                // 18-20 first drop + pull-out valley
                new Vector3(19.8f, 14.5f, 45.5f), new Vector3(19.4f, 5f, 49f), new Vector3(19f, 1.7f, 52.5f),
                // 21-29 VERTICAL LOOP (x drifts 18.8 → 15.2 through the inversion)
                new Vector3(18.8f, 1.3f, 57f),     // entry
                new Vector3(18.4f, 3.2f, 63.5f),
                new Vector3(18f, 8.8f, 66.5f),     // climbing vertical
                new Vector3(17.5f, 13.6f, 64.5f),
                new Vector3(17f, 15.4f, 60.5f),    // top — fully inverted
                new Vector3(16.5f, 13.6f, 56.5f),
                new Vector3(16f, 8.8f, 54.5f),     // diving vertical
                new Vector3(15.6f, 3.4f, 56f),
                new Vector3(15.2f, 1.4f, 61f),     // exit
                // 30-32 rising banked turn onto the zero-g straight (now heading west)
                new Vector3(13.5f, 3.5f, 68f), new Vector3(10f, 5.5f, 73.5f), new Vector3(4.5f, 7f, 77f),
                // 33-38 ZERO-G SECTOR: three camel-backs (crest / valley / crest / valley / crest / exit)
                new Vector3(-1f, 10.8f, 77.5f), new Vector3(-6.5f, 5.2f, 77.5f),
                new Vector3(-12f, 10.2f, 77.5f), new Vector3(-17.5f, 5.2f, 77.5f),
                new Vector3(-23f, 9.6f, 77.5f), new Vector3(-28f, 5.4f, 77f),
                // 39-44 sweeping high banked turn into the elevated mid-course brake run
                new Vector3(-32.5f, 6.5f, 73f), new Vector3(-34.5f, 7f, 67f), new Vector3(-34.5f, 6.2f, 60.5f),
                new Vector3(-34f, 5.4f, 54f), new Vector3(-34f, 5.4f, 47f), new Vector3(-34f, 5.4f, 40f),
                // 45 BLOCK 4 start: mid-course brake run end
                new Vector3(-33.7f, 5.2f, 34f),
                // 46-52 diving turn home + ground-level final brake run into the station
                new Vector3(-31.5f, 3.8f, 27f), new Vector3(-26.5f, 2.6f, 21f), new Vector3(-20.5f, 1.7f, 16.5f),
                new Vector3(-16f, 1f, 11.5f), new Vector3(-14f, 0.7f, 6f),
                new Vector3(-12f, 0.6f, 2.2f), new Vector3(-8.5f, 0.6f, 2.2f),
            };
            for (int i = 0; i < layout.Length; i++)
            {
                var wp = new GameObject($"WP_{i:00}");
                wp.transform.SetParent(trackGO.transform);
                wp.transform.position = layout[i];
                path.Waypoints.Add(wp.transform);
            }

            // Data-driven block boundaries snapped to waypoints. Trick: while BlockStarts is
            // still empty and BlockCount equals the waypoint count, DistanceOf(i, 0) returns
            // exactly waypoint i's arc distance — normalized starts (and the evac/catwalk
            // anchor distances below) fall out without hand-integrating the spline.
            path.Rebuild();
            float trackLen = path.TotalLength;
            path.BlockCount = layout.Length;
            var wpDist = new float[layout.Length];
            for (int i = 0; i < layout.Length; i++) wpDist[i] = path.DistanceOf(i, 0f);
            path.BlockCount = 5;
            int[] boundaryWp = { 0, 4, 8, 17, 45 };
            string[] blockNames = { "UNLOAD", "LOAD", "LIFT HILL", "LOOP + ZERO-G", "BRAKE RUN" };
            path.BlockStarts = new List<float>();
            path.BlockLabels = new List<string>();
            // de-overlap: per-block marker offsets. The old uniform (0, 0.6, 1.4) offset is
            // perpendicular only on the station straight (track runs +x there); at the lift crest
            // (WP17, track heading +z) and the MCBR end (WP45, heading -z) it was ALONG the
            // track, planting the marker post inside the first-drop / brake-run train path.
            // Those two now offset sideways (+x, 1.6m > the 0.95m sweep half-width).
            // de-overlap: marker 2 (WP8, x 6) pushed z 1.4 -> 1.9 and marker 4 (WP45) 1.6 -> 1.85
            // — both stand where the track already banks (departure curve / MCBR exit dive) and
            // the ROLLED envelope reaches past the flat-track clearances (marker 2 was 0.21m in,
            // marker 4 grazed by 0.015m). Markers 0/1 sit on flat station track: unchanged.
            Vector3[] markerOffset =
            {
                new Vector3(0f, 0.6f, 1.4f), new Vector3(0f, 0.6f, 1.4f), new Vector3(0f, 0.6f, 1.9f),
                new Vector3(1.6f, 0.6f, 0f), new Vector3(1.85f, 0.6f, 0f),
            };
            for (int b = 0; b < boundaryWp.Length; b++)
            {
                path.BlockStarts.Add(wpDist[boundaryWp[b]] / trackLen);
                path.BlockLabels.Add(blockNames[b]);
                var post = Box($"BlockMarker_{b}", layout[boundaryWp[b]] + markerOffset[b],
                               new Vector3(0.12f, 1.2f, 0.12f), mTrack, trackGO.transform);
                Label(post, "BLOCK " + b + ": " + blockNames[b], 0.9f);
            }
            // Normalized in-block progress of waypoint i inside block b (for the fin spans).
            System.Func<int, int, float> BlockP = (b, i) =>
            {
                float s0 = wpDist[boundaryWp[b]];
                float s1 = b + 1 < boundaryWp.Length ? wpDist[boundaryWp[b + 1]] : trackLen;
                float s = wpDist[i];
                if (s < s0) s += trackLen;
                return Mathf.Clamp01((s - s0) / Mathf.Max(0.01f, s1 - s0));
            };

            // Full styled track mesh along the spline: twin octagonal rails, ties, center spine,
            // plumb support columns with footers (auto-skipped through the inversion — the loop
            // ring below braces it instead), neon brake fins on the two flat brake shelves only,
            // and station drive tires.
            var trackStyle = new TrackMeshBuilder.TrackStyle();
            trackStyle.AccentMaterial = mNeonCyan; // brake fins + drive tires pick up the neon accent
            trackStyle.SupportStep = 3.2f;         // taller layout — denser support forest
            trackStyle.FinSpans = new[]
            {
                new Vector3(3f, BlockP(3, 42) + 0.005f, BlockP(3, 45) - 0.005f), // mid-course brake shelf
                new Vector3(4f, BlockP(4, 50), 0.985f),                          // final brake run
            };
            TrackMeshBuilder.Build(path, mRail, mTie, trackStyle);

            // ---- Coaster infrastructure: lift catwalk, loop ring brace, zero-g ring gates,
            // evac platforms on both brake shelves, ground-level assembly point ----
            var coasterInfra = new GameObject("CoasterInfra");
            // de-overlap: catwalk start pushed 3.0m up the lift (wpDist[12] -> +3) — the first
            // two deck/bracket/post bays sat where the pre-lift turn still banks the train
            // (~44 -> ~14 deg over arc 30..33) and the rolled envelope's low winglet corner cut
            // into them by up to 0.23m. From +3.0 on, the lift runs straight and every bay
            // clears; the access stairs move up with the start and still reach grade.
            BuildLiftCatwalk(path, wpDist[12] + 3.0f, wpDist[17], coasterInfra.transform, mBooth, mWhite, mLampPole);
            BuildLoopRing(new Vector3(17f, 9.4f, 60.5f), 8.6f, 17, coasterInfra.transform, mWhite, mNeonMagenta);
            int[] gateWp = { 33, 35, 37 };
            for (int g = 0; g < gateWp.Length; g++)
                BuildRingGate($"ZeroGGate{g}", layout[gateWp[g]] + Vector3.up * 0.9f, 2.7f,
                              coasterInfra.transform, g % 2 == 0 ? mNeonCyan : mNeonMagenta,
                              g % 2 == 0 ? new Color(0.25f, 0.95f, 1f) : new Color(1f, 0.3f, 0.85f),
                              g * 1.3f, g == 0 ? "ZERO-G SECTOR" : null);
            // Evac platform names match the block numbers a held train actually stops in.
            BuildEvacPlatform("EvacPlatformB3", path, wpDist[43], 10f, coasterInfra.transform,
                              mBooth, mWhite, mNeonCyan, "EVAC PLATFORM B3", 1f);
            // de-overlap: B4 shelf moved to the LEFT (north) side of the final brake run — on the
            // right it landed in the maintenance-ramp yard (deck clipped RampMaint's upper plate,
            // its access step sat under the ramp, and it boxed in the OCC/exit-path area).
            // de-overlap: B4 slid east (+1.2 -> +3.0), shortened (6 -> 4) and pushed out
            // (side offset 1.35 -> 2.0) — the diving approach curve crosses x -14..-12 at
            // z 2.2..6 and stays banked (up to 44 deg) until ~x -9.5, so the old west half sat
            // on the incoming track corridor and the handrail run took the rolled envelope's
            // top corner (pen up to 0.62m). Grade step moved to the WEST (rear) end: the east
            // end now stops 0.13m short of the NW station column base. Verified clear incl.
            // bank in the scratch sweep model.
            BuildEvacPlatform("EvacPlatformB4", path, wpDist[51] + 3.0f, 4f, coasterInfra.transform,
                              mBooth, mWhite, mNeonCyan, "EVAC PLATFORM B4", -1f, 2.0f, -1f);
            // Ground-level muster area beside the maintenance bay, clear of the course.
            // de-overlap: whole cluster moved from z -0.5 to z 1.5 — the pad slab (z -2.3..1.3)
            // interpenetrated the MaintBayFloor slab (z -7.5..-0.5) over a 3.3 x 1.8m patch.
            var muster = new GameObject("EvacAssemblyPoint");
            muster.transform.position = new Vector3(-17f, 0f, 1.5f);
            muster.transform.SetParent(coasterInfra.transform, true);
            Box("AssemblyPad", new Vector3(-17f, 0.03f, 1.5f), new Vector3(4.6f, 0.06f, 3.6f), mGate, muster.transform);
            Box("AssemblyPadCore", new Vector3(-17f, 0.055f, 1.5f), new Vector3(3.8f, 0.06f, 2.8f), mBooth, muster.transform);
            Cyl("AssemblySignPole", new Vector3(-18.9f, 1.1f, 0.1f), new Vector3(0.08f, 1.1f, 0.08f), mLampPole, muster.transform);
            var musterSign = Box("AssemblySign", new Vector3(-18.9f, 2.35f, 0.1f), new Vector3(1.5f, 0.7f, 0.1f), mGate, muster.transform);
            Label(musterSign, "EVAC ASSEMBLY POINT", 0.6f);

            // ---- Trains (Blender CoasterTrain.fbx; procedural factory as fallback) ----
            var trainPrefab = Prop("CoasterTrain.fbx");
            System.Func<int, Material, TrainVisual> MakeTrain = (id, m) =>
            {
                GameObject body;
                Transform[] seats;
                if (trainPrefab != null)
                {
                    body = PlaceProp(trainPrefab, $"Train{id}", Vector3.zero);
                    SwapMaterial(body, "PS_TrainBody", m);
                    seats = new Transform[8];
                    for (int s = 0; s < 8; s++) seats[s] = FindDeep(body.transform, "Seat" + s);
                    var bars = new List<Transform>();
                    for (int b = 0; b < 4; b++)
                    {
                        var bar = FindDeep(body.transform, "RestraintBar" + b);
                        if (bar != null) bars.Add(bar);
                    }
                    var rd = body.AddComponent<TrainRestraintDriver>();
                    rd.TrainId = id; rd.Bars = bars.ToArray();
                }
                else
                {
                    body = TrainCarFactory.Create($"Train{id}", m, mSeat, mSeat, mGate, out seats);
                }
                var tv = body.AddComponent<TrainVisual>();
                tv.TrainId = id; tv.Path = path;
                tv.SeatAnchors = seats;
                // Per-train juice: speed-driven rumble loop, launch squash/whoosh, brake sparks,
                // restraint clunk + click flash. Runtime-added RVs get theirs from JuiceDirector.
                var tj = body.AddComponent<ParkShift.Juice.TrainJuice>();
                tj.TrainId = id;
                Label(body, "TRAIN " + (id == 0 ? "A" : "B"), 1.6f);
                return tv;
            };
            var trainA = MakeTrain(0, mTrainA);
            var trainB = MakeTrain(1, mTrainB);

            // Flagged-seat prop on train A (restraint fault marker)
            // Reusable low-poly red flag for fault markers (replaces the old red mDanger boxes).
            // Uses the Blender FaultFlag.fbx if present, else falls back to a red box. Any interactor
            // is added by the caller via AddInteractor (trigger-only, so it never shoves the player).
            System.Func<string, Vector3, float, float, GameObject> MakeFaultFlag = (flagName, worldPos, yRot, scale) =>
            {
                var flagPrefab = Prop("FaultFlag.fbx");
                GameObject go = flagPrefab != null
                    ? PlaceProp(flagPrefab, flagName, worldPos, yRot)
                    : Box(flagName, worldPos, new Vector3(0.4f, 0.6f, 0.4f), mDanger);
                if (scale != 1f) go.transform.localScale = Vector3.one * scale;
                return go;
            };

            // Flagged-seat marker on train A (red flag; restraint fault). Interactor = SeatInspect.
            var flagged = MakeFaultFlag("FlaggedSeatMarker", Vector3.zero, 0f, 0.5f);
            flagged.transform.SetParent(trainA.transform, false);
            flagged.transform.localPosition = new Vector3(0.2f, 0.9f, 0f);
            AddInteractor(flagged.transform, PanelInteractable.Kind.SeatInspect,
                new Vector3(0.6f, 1.2f, 0.6f), new Vector3(0f, 0.6f, 0f));
            Label(flagged, "ROW 3", 1.3f);

            // ---- Station gates (Blender swing-door units; sliding cubes as fallback) ----
            // de-overlap: gate line moved z 1.2 -> 1.05 — closed leaves/posts sat 0.03m from the
            // 0.95m-half-width sweep envelope and the post caps 0.015m INSIDE it, with the bank
            // onset near x 4.5+ dipping the rolled envelope further south. At z 1.05 everything
            // closed clears by >=0.15m (still on the platform, 0.33m south of the lap-bar tiles).
            var gateRoot = new GameObject("Gates");
            gateRoot.transform.position = new Vector3(0, 0.5f, 1.05f);
            var gatePrefab = Prop("StationGate.fbx");
            if (gatePrefab != null)
            {
                for (int g = 0; g < 4; g++)
                {
                    var unit = PlaceProp(gatePrefab, $"GateUnit{g}",
                        gateRoot.transform.position + new Vector3(-4.5f + 3f * g, 0f, 0f));
                    unit.transform.SetParent(gateRoot.transform, true);
                    var gd = unit.AddComponent<GateUnitDriver>();
                    gd.DoorL = FindDeep(unit.transform, "GateDoorL");
                    gd.DoorR = FindDeep(unit.transform, "GateDoorR");
                    // de-overlap: swing flipped SOUTH and trimmed to 80 deg (driver default was
                    // L-100/R+100). With the FBX leaf axes the default swung the 1.05m leaves
                    // NORTH past perpendicular — open door tips reached z ~2.2, through the
                    // parked train's flank (train side z 1.45). Flipped they open toward the
                    // guest side; 80 deg keeps the springy ease-out-back overshoot (~88 deg)
                    // clear of the podium heads and of guests waiting at the unit-center marks.
                    gd.OpenEulerL = new Vector3(0, 0, 80f);
                    gd.OpenEulerR = new Vector3(0, 0, -80f);
                }
                gateRoot.AddComponent<GateVisual>().GatePanels = new Transform[0]; // sim mirror handled by drivers
            }
            else
            {
                var gv = gateRoot.AddComponent<GateVisual>();
                var panels = new List<Transform>();
                for (int g = 0; g < 4; g++)
                {
                    var gp = Box($"GatePanel{g}", gateRoot.transform.position + new Vector3(-4.5f + 3f * g, 0.4f, 0),
                                 new Vector3(2.4f, 1.0f, 0.12f), mGate, gateRoot.transform);
                    panels.Add(gp.transform);
                }
                gv.GatePanels = panels.ToArray();
            }

            // ---- Control booth: open-front kiosk — half walls, white corner posts,
            // grape roof, cyan trim and a warm interior point light ----
            Box("BoothFloor", new Vector3(10, 0.05f, -3), new Vector3(6, 0.1f, 5), mBooth);
            Box("BoothWall", new Vector3(10, 1.2f, -5.4f), new Vector3(6, 2.4f, 0.2f), mBooth);
            // Keep the rear wall footprint, but open its north end onto the booth ramp.
            Box("BoothWallL", new Vector3(7.15f, 0.75f, -3.675f), new Vector3(0.18f, 1.5f, 3.65f), mBooth);
            Box("BoothWallR", new Vector3(12.85f, 0.75f, -3f), new Vector3(0.18f, 1.5f, 5f), mBooth);
            Box("BoothRoof", new Vector3(10, 2.75f, -3f), new Vector3(6.6f, 0.16f, 5.6f), mRoof);
            foreach (var bp in new[] { new Vector3(7.15f, 1.7f, -0.62f), new Vector3(12.85f, 1.7f, -0.62f),
                                       new Vector3(7.15f, 1.7f, -5.35f), new Vector3(12.85f, 1.7f, -5.35f) })
                Box("BoothPost", bp, new Vector3(0.22f, 2.2f, 0.22f), mWhite);
            Box("BoothNeon", new Vector3(10, 2.86f, -0.45f), new Vector3(6.7f, 0.08f, 0.08f), mNeonCyan);
            var boothLightGO = new GameObject("BoothLight");
            boothLightGO.transform.position = new Vector3(10, 2.35f, -3.4f);
            var boothLight = boothLightGO.AddComponent<Light>();
            boothLight.type = LightType.Point;
            boothLight.range = 7f;
            boothLight.intensity = 1.3f;
            boothLight.color = new Color(1f, 0.78f, 0.55f);
            boothLight.shadows = LightShadows.None;

            System.Action<string, PanelInteractable.Kind, Vector3, Material, string> Panel = (name, kind, pos, m, label) =>
            {
                var p = Box(name, pos, new Vector3(0.9f, 1.1f, 0.5f), m);
                p.AddComponent<PanelInteractable>().PanelKind = kind;
                p.AddComponent<InteractableGlow>();   // reads as interactable at a glance
                Label(p, label, 1.0f);
            };
            // Open-shift lever (green Blender prop; box fallback). Throws to "on" when the shift opens.
            var shiftLeverPrefab = Prop("OpenShiftLever.fbx");
            if (shiftLeverPrefab != null)
            {
                // The lever's mount PLATE normal runs along the model's X. Yaw 90° squares the plate
                // to the booth back wall (z=-5.4) with the HANDLE facing +Z toward the operator inside
                // the booth (the old yaw 270 left the handle facing the wall). The assembly is pulled
                // ~0.25m off the wall so the plate and the throw arc never clip the boards. The
                // ShiftLeverDriver pitch is authored about the pivot's LOCAL X, so the swing stays
                // model-relative — no sign flip needed after the 180° turn.
                var sl = PlaceProp(shiftLeverPrefab, "OpenShiftLever", new Vector3(8.5f, 0.5f, -4.8f), 90f);
                var pivot = FindDeep(sl.transform, "OpenShiftLever_Pivot");
                // Interaction volume on the lever wrapper, raised to cover the handle (~1.1-2.2m up).
                // Trigger-only: lets the player look-to-interact without the collider ever pushing them.
                AddInteractor(sl.transform, PanelInteractable.Kind.ShiftStart,
                    new Vector3(0.7f, 2.2f, 0.7f), new Vector3(0f, 1.1f, 0f));
                sl.AddComponent<ShiftLeverDriver>().Pivot = pivot;
                Label(sl, "SHIFT START", 2.0f);
            }
            else
            {
                // Pulled off the back wall (front face z=-5.3) like the FBX lever, so the box never clips.
                Panel("ShiftStartPanel", PanelInteractable.Kind.ShiftStart, new Vector3(8.5f, 0.7f, -5.0f), mPanel, "SHIFT START");
            }
            var consolePrefab = Prop("Console.fbx");
            if (consolePrefab != null)
            {
                var console = PlaceProp(consolePrefab, "DispatchConsole", new Vector3(10.1f, 0.1f, -3.4f));
                var lever = FindDeep(console.transform, "GateLever");
                var button = FindDeep(console.transform, "DispatchButton");
                // de-overlap: gate-lever trigger resized (0.3,0.3,0.55)->(0.26,0.18,0.44) and
                // recentred (0,0.06,0.15) — up the handle and toward the operator. The old box
                // was centered on the floor-level pivot and its OBB interpenetrated the HMI
                // screen trigger 0.29m (screen sits directly above/behind the lever slot), so
                // aiming at one could hit the other. Both boxes still cover their meshes.
                AddInteractor(lever, PanelInteractable.Kind.GateLever, new Vector3(0.26f, 0.18f, 0.44f), new Vector3(0f, 0.06f, 0.15f));
                // Fit the cap itself: a cube around its pivot also covered the HMI screen.
                var buttonMesh = button != null ? button.GetComponent<MeshFilter>() : null;
                var buttonBounds = buttonMesh != null && buttonMesh.sharedMesh != null
                    ? buttonMesh.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.14f);
                AddInteractor(button, PanelInteractable.Kind.DispatchButton, buttonBounds.size, buttonBounds.center);
                var cd = console.AddComponent<ConsoleDriver>();
                cd.Lever = lever; cd.Button = button;
                // HMI screen: look at the little screen + press E to open the clickable ride menu.
                // The mesh origin sits at the console root. Fit its actual slab bounds;
                // the former padded and offset box still reached the adjacent button cap.
                var screen = FindDeep(console.transform, "Screen");
                var screenMesh = screen != null ? screen.GetComponent<MeshFilter>() : null;
                var screenBounds = screenMesh != null && screenMesh.sharedMesh != null
                    ? screenMesh.sharedMesh.bounds
                    : new Bounds(new Vector3(-0.18f, -0.205f, 0.93f), new Vector3(0.42f, 0.092f, 0.25f));
                AddInteractor(screen, PanelInteractable.Kind.HmiScreen, screenBounds.size, screenBounds.center);
                Label(console, "GATES  |  DISPATCH  |  [E] HMI SCREEN", 1.5f);
            }
            else
            {
                Panel("GateLever", PanelInteractable.Kind.GateLever, new Vector3(9.4f, 0.7f, -3.4f), mGate, "GATES");
                Panel("DispatchPanel", PanelInteractable.Kind.DispatchButton, new Vector3(10.8f, 0.7f, -3.4f), mDanger, "DISPATCH");
            }
            Panel("AssistantPhone", PanelInteractable.Kind.AssistantPhone, new Vector3(12.2f, 0.7f, -3.4f), mPanel, "TECH PHONE (H)");

            // ---- Dispatch push button: chunky arcade mushroom on its own stand beside the console —
            // THE primary dispatch affordance (the console control keeps working; both funnel through
            // the same PanelInteractable -> OpVerb.Dispatch). DispatchButtonLight (PropDrivers) polls
            // Sim.CanDispatch read-only: bright green invite pulse when dispatch is legal, dim when
            // blocked, one flash + a cap press when the train actually leaves. ----
            var mDispatchGo = GlowMat("PS_DispatchGo", new Color(0.06f, 0.22f, 0.10f), new Color(0.25f, 1f, 0.35f), 2.2f);
            var dpbRoot = new GameObject("DispatchPushButton");
            dpbRoot.transform.position = new Vector3(11.2f, 0f, -3.0f);
            var dpbStand = Cyl("Stand", new Vector3(11.2f, 0.5f, -3.0f), new Vector3(0.18f, 0.5f, 0.18f), mBooth, dpbRoot.transform);
            Object.DestroyImmediate(dpbStand.GetComponent<Collider>());   // trigger interactor covers the whole button
            var dpbBezel = Cyl("Bezel", new Vector3(11.2f, 1.03f, -3.0f), new Vector3(0.4f, 0.05f, 0.4f), mSeat, dpbRoot.transform);
            Object.DestroyImmediate(dpbBezel.GetComponent<Collider>());
            var dpbCap = Ball("Cap", new Vector3(11.2f, 1.1f, -3.0f), 1f, mDispatchGo, dpbRoot.transform);
            dpbCap.transform.localScale = new Vector3(0.30f, 0.17f, 0.30f);   // squashed dome = mushroom cap
            // No InteractableGlow here — the button drives its own cap light (two emission writers would fight).
            AddInteractor(dpbRoot.transform, PanelInteractable.Kind.DispatchButton,
                new Vector3(0.55f, 0.6f, 0.55f), new Vector3(0f, 0.95f, 0f), glow: false);
            dpbRoot.AddComponent<DispatchButtonLight>().Cap = dpbCap.transform;
            Label(dpbRoot, "DISPATCH", 1.5f);

            // Zone-sensor reset button beside the dispatch console (BlockSensorGhost step 2:
            // refused until the operator has walked the course and verified the block on foot).
            // de-overlap: raised y 1.0 -> 1.32 (moved from (11.35, 1.0, -3.9)) — it sat almost
            // dead behind the dispatch push button from the operator's approach line, and the
            // aim ray from any natural standing spot (eye ~1.6, z -1.0..-2.2) clipped the
            // dispatch trigger (top y 1.25) before reaching it. At 1.32 the sightline passes
            // over the dispatch volume from every tested spot.
            var sensorReset = Box("SensorResetButton", new Vector3(11.35f, 1.32f, -3.9f), new Vector3(0.24f, 0.24f, 0.24f), mDanger);
            Object.DestroyImmediate(sensorReset.GetComponent<Collider>()); // trigger interactor replaces the primitive collider
            AddInteractor(sensorReset.transform, PanelInteractable.Kind.SensorReset, new Vector3(0.5f, 0.5f, 0.5f));
            Label(sensorReset, "SENSOR RESET", 0.5f);

            // Platform-side stations: attendant podiums (boxes as fallback)
            var podiumPrefab = Prop("Podium.fbx");
            if (podiumPrefab != null)
            {
                System.Action<string, PanelInteractable.Kind, Vector3, Material, string> Podium = (name, kind, pos, accent, label) =>
                {
                    var p = PlaceProp(podiumPrefab, name, pos, 180f); // face the platform interior
                    SwapMaterial(p, "PS_Blue", accent);
                    // Interaction volume raised to the podium head/screen (~0.95m up on the model, ~1.45m
                    // world on the platform) so the prompt sits at the working height, not at the base.
                    AddInteractor(p.transform, kind, new Vector3(0.7f, 0.8f, 0.7f), new Vector3(0f, 0.95f, 0f));
                    var btn = FindDeep(p.transform, "PodiumButton");
                    if (btn != null)
                    {
                        var pd = p.AddComponent<PodiumDriver>();
                        pd.Button = btn;
                        pd.PodiumMode = kind == PanelInteractable.Kind.LoadHelper
                            ? PodiumDriver.Mode.Load : PodiumDriver.Mode.Restraint;
                    }
                    Label(p, label, 1.35f);
                };
                Podium("LoadHelper", PanelInteractable.Kind.LoadHelper, new Vector3(-3, 0.5f, 0.2f), mPanel, "WAVE ABOARD");
                Podium("RestraintStation", PanelInteractable.Kind.RestraintCheck, new Vector3(3, 0.5f, 0.2f), mGate, "RESTRAINT CHECK");
            }
            else
            {
                Panel("LoadHelper", PanelInteractable.Kind.LoadHelper, new Vector3(-3, 0.7f, 0.2f), mPanel, "WAVE ABOARD");
                Panel("RestraintStation", PanelInteractable.Kind.RestraintCheck, new Vector3(3, 0.7f, 0.2f), mPanel, "RESTRAINT CHECK");
            }

            // ---- Maintenance bay ----
            Box("MaintBayFloor", new Vector3(-14, 0.05f, -4), new Vector3(8, 0.1f, 7), mBooth);
            GameObject lotoTag;
            var cabinetPrefab = Prop("DriveCabinet.fbx");
            if (cabinetPrefab != null)
            {
                var cab = PlaceProp(cabinetPrefab, "DriveCabinet", new Vector3(-15f, 0.1f, -6.2f));
                var breaker = FindDeep(cab.transform, "BreakerLever");
                var resetBtn = FindDeep(cab.transform, "ResetBtn");
                AddInteractor(breaker, PanelInteractable.Kind.LotoLever, new Vector3(0.3f, 0.3f, 0.55f));
                AddInteractor(resetBtn, PanelInteractable.Kind.MaintenancePanel, new Vector3(0.3f, 0.3f, 0.3f));
                var cabd = cab.AddComponent<CabinetDriver>();
                cabd.Door = FindDeep(cab.transform, "CabDoor");
                cabd.Breaker = breaker;
                cabd.ResetButton = resetBtn;
                Label(cab, "DRIVE CABINET — LOCKOUT + RESET", 2.1f);
                lotoTag = Box("LotoTag", new Vector3(-14.3f, 1.1f, -6.0f), new Vector3(0.25f, 0.35f, 0.05f), mGate);
            }
            else
            {
                Panel("MaintenancePanel", PanelInteractable.Kind.MaintenancePanel, new Vector3(-16, 0.7f, -6.2f), mPanel, "DRIVE RESET");
                var loto = Box("LotoLever", new Vector3(-12, 0.8f, -6.2f), new Vector3(0.5f, 1.6f, 0.5f), mDanger);
                loto.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.LotoLever;
                loto.AddComponent<InteractableGlow>();
                Label(loto, "LOCKOUT", 1.2f);
                lotoTag = Box("LotoTag", loto.transform.position + new Vector3(0.5f, 0.2f, 0), new Vector3(0.25f, 0.35f, 0.05f), mGate);
            }
            Label(lotoTag, "TAGGED OUT", 0.4f);

            // Bus-stage button at the drive cabinet (PowerBrownout: technician energizes the
            // three drive bus stages in order, letting each stabilize before the next).
            var busStage = Box("BusStageButton", new Vector3(-13.6f, 1.0f, -6.2f), new Vector3(0.24f, 0.24f, 0.24f), mGate);
            Object.DestroyImmediate(busStage.GetComponent<Collider>()); // trigger interactor replaces the primitive collider
            AddInteractor(busStage.transform, PanelInteractable.Kind.BrownoutStage, new Vector3(0.5f, 0.5f, 0.5f));
            Label(busStage, "BUS STAGE", 0.5f);
            // Undervolt warning light above the cabinet — lit only during the brownout
            // (toggled by FaultPropController below).
            var brownoutWarn = Box("BrownoutWarnLight", new Vector3(-13.6f, 1.7f, -6.2f), new Vector3(0.2f, 0.2f, 0.2f), mNeonMagenta);
            Object.DestroyImmediate(brownoutWarn.GetComponent<Collider>()); // pure indicator — no physics, no interaction
            Label(brownoutWarn, "BUS UNDERVOLT", 0.4f);

            // ---- Dropped item prop (near station edge) ----
            // NOTE (de-overlap pass): intentionally INSIDE the berth sweep envelope — the whole
            // fault is that a guest item blocks the train, and the sim refuses dispatch until
            // it's cleared, so no moving train ever reaches it. The audit will list it.
            var item = Box("DroppedItem", new Vector3(5.5f, 0.65f, 1.6f), new Vector3(0.35f, 0.35f, 0.35f), mGate);
            item.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.DroppedItem;
            item.AddComponent<InteractableGlow>();
            Label(item, "GUEST ITEM", 0.5f);

            // ---- Technician incident props ----
            // Guest who climbed onto the track (e-stop scenario)
            GameObject intruder;
            if (deweyPrefab != null)
            {
                intruder = (GameObject)Object.Instantiate(deweyPrefab);
                intruder.name = "TrackIntruder";
                intruder.transform.position = new Vector3(9f, 0.65f, 3.1f);
                intruder.transform.localScale = Vector3.one * 0.5f;
                var ianim = intruder.GetComponent<Animator>();
                if (ianim == null) ianim = intruder.AddComponent<Animator>();
                ianim.runtimeAnimatorController = deweyCtrl; // plays Idle by default
                var icol = intruder.AddComponent<CapsuleCollider>();
                icol.height = 2.6f; icol.radius = 0.7f; icol.center = Vector3.up * 1.3f;
            }
            else
            {
                intruder = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                intruder.name = "TrackIntruder";
                intruder.transform.position = new Vector3(9f, 1.1f, 3.1f);
                intruder.transform.localScale = new Vector3(0.5f, 0.8f, 0.5f);
                intruder.GetComponent<Renderer>().sharedMaterial = mDanger;
            }
            intruder.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.IntruderEscort;
            intruder.AddComponent<InteractableGlow>();
            Label(intruder, "GUEST ON TRACK!", deweyPrefab != null ? 2.9f : 1.3f);

            // Collision contact-point marker on the brake run approach (red flag). Interactor = CollisionInspect.
            var crash = MakeFaultFlag("CollisionMarker", new Vector3(-9.5f, 0.6f, 2.2f), 0f, 1f);
            AddInteractor(crash.transform, PanelInteractable.Kind.CollisionInspect,
                new Vector3(0.7f, 1.4f, 0.7f), new Vector3(0f, 0.7f, 0f));
            Label(crash, "CONTACT POINT", 1.5f);

            // Evac winch out on the course (valleyed-train recovery — a real hike for the tech:
            // it lives under the ZERO-G SECTOR valley now, the layout's natural valleying spot).
            // Red Blender lever (box fallback); throws + cranks while the tech works the recovery.
            // The lever is permanent equipment; the beacon above it only shows during the fault.
            var evacPrefab = Prop("EvacLever.fbx");
            if (evacPrefab != null)
            {
                var evac = PlaceProp(evacPrefab, "EvacLever", new Vector3(-6.5f, 0.5f, 74.5f), 180f);
                var pivot = FindDeep(evac.transform, "EvacLever_Pivot");
                // Interaction volume on the lever wrapper, raised to cover the handle. Trigger-only.
                AddInteractor(evac.transform, PanelInteractable.Kind.WinchPanel,
                    new Vector3(0.8f, 2.2f, 0.8f), new Vector3(0f, 1.1f, 0f));
                evac.AddComponent<EvacLeverDriver>().Pivot = pivot;
                Label(evac, "EVAC WINCH", 2.0f);
            }
            else
            {
                var winch = Box("EvacWinch", new Vector3(-6.5f, 0.7f, 74.5f), new Vector3(0.9f, 1.1f, 0.6f), mPanel);
                winch.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.WinchPanel;
                winch.AddComponent<InteractableGlow>();
                Label(winch, "EVAC WINCH", 1.0f);
            }
            // Stalled-train indicator out on the course (red flag; non-interactable, toggled by
            // FaultPropController) — parked at zero-g crest 2 where a valleyed train would hang.
            var valleyBeacon = MakeFaultFlag("ValleyedBeacon", new Vector3(-12f, 10.6f, 77.5f), 0f, 1.4f);
            Label(valleyBeacon, "TRAIN STALLED", 2.2f);

            // ---- Zone sensor post out by block 2 (BlockSensorGhost step 1: the operator hikes
            // to the lift-hill zone and visually confirms the block is clear before the reset). ----
            var zoneSensor = new GameObject("ZoneSensorPost");
            zoneSensor.transform.position = new Vector3(16.3f, 0f, 13f);
            Cyl("SensorPole", new Vector3(16.3f, 0.75f, 13f), new Vector3(0.09f, 0.75f, 0.09f), mLampPole, zoneSensor.transform);
            Box("SensorHead", new Vector3(16.3f, 1.6f, 13f), new Vector3(0.3f, 0.3f, 0.3f), mBooth, zoneSensor.transform);
            AddInteractor(zoneSensor.transform, PanelInteractable.Kind.BlockVerify,
                new Vector3(0.9f, 1.9f, 0.9f), new Vector3(0f, 0.95f, 0f));
            Label(zoneSensor, "ZONE SENSOR", 2.1f);
            // Phantom-occupancy beacon on the post — lit only while the ghost fault is active.
            var ghostBeacon = Ball("GhostSensorBeacon", new Vector3(16.3f, 2.05f, 13f), 0.32f, mNeonMagenta, zoneSensor.transform);

            // ---- Lap-bar check points along the Load berth edge (RestraintJammedCar: the HMI
            // names the flagged car; checking the wrong car is refused by the sim). ----
            var lapBarRoot = new GameObject("LapBarCheckPoints");
            var lapBarAnchors = new Transform[4];
            // de-overlap: check points slid west (x 1.65..4.35 -> 0.75..3.45), pulled to z 1.38
            // and flattened into floor tiles (y 0.56 h 0.12 -> y 0.52 h 0.04, top 0.54 vs the
            // platform's 0.5) — the old chunky pads poked 0.02m into the flat sweep envelope
            // (base y 0.6), and east of x~3.5 the departure banking dips the rolled winglet
            // corner to y~0.48/z~1.3 so ANY block there gets clipped. The trigger volumes still
            // reach up to the parked cars (intended); only the solid visuals slimmed/moved.
            for (int carIdx = 0; carIdx < 4; carIdx++)
            {
                float lx = 0.75f + 0.9f * carIdx; // spans the Load berth (train parks at x=+3)
                var pt = Box($"LapBarCheck{carIdx}", new Vector3(lx, 0.52f, 1.38f), new Vector3(0.3f, 0.04f, 0.3f), mGate, lapBarRoot.transform);
                Object.DestroyImmediate(pt.GetComponent<Collider>()); // trigger interactor replaces the primitive collider
                // Keep the usable volume in world metres despite the thin tile's scale.
                Vector3 scale = pt.transform.lossyScale;
                AddInteractor(pt.transform, PanelInteractable.Kind.LapBarUnjam,
                    new Vector3(0.55f / scale.x, 1f / scale.y, 0.55f / scale.z),
                    new Vector3(0f, 0.45f / scale.y, 0f));
                pt.GetComponent<PanelInteractable>().CarIndex = carIdx;
                Label(pt, "CAR " + (carIdx + 1), 0.55f);
                lapBarAnchors[carIdx] = pt.transform;
            }
            // Jam marker flag — FaultPropController snaps it to the flagged car's check point.
            // de-overlap: rest pose follows the moved check-point row (was (3, 0.56, 1.5)).
            // It sits inside the berth sweep on purpose: the flag only shows during
            // RestraintJammedCar, when the train is parked at Load — the audit will list it.
            var jamFlag = MakeFaultFlag("JammedCarFlag", new Vector3(3f, 0.52f, 1.38f), 0f, 0.5f);
            Label(jamFlag, "LAP BAR JAM", 1.3f);

            // Fault prop controller wiring
            var props = new GameObject("FaultProps").AddComponent<FaultPropController>();
            props.DroppedItemProp = item;
            props.FlaggedSeatProp = flagged;
            props.LotoTagProp = lotoTag;
            props.IntruderProp = intruder;
            props.CollisionProp = crash;
            props.ValleyedProp = valleyBeacon;
            props.GhostSensorProp = ghostBeacon;
            props.BrownoutCabinetProp = brownoutWarn;
            props.JammedCarProp = jamFlag;
            props.JammedCarAnchors = lapBarAnchors;

            // ---- Queue ----
            var queueRoot = new GameObject("Queue");
            var gvm = queueRoot.AddComponent<GuestVisualManager>();
            gvm.GuestMaterial = mGuest;
            gvm.GuestPrefab = deweyPrefab;
            gvm.GuestAnimController = deweyCtrl;
            gvm.Trains = new[] { trainA, trainB };
            var spawn = new GameObject("GuestSpawn"); spawn.transform.position = new Vector3(-2, 0f, -16);
            var exit = new GameObject("GuestExit"); exit.transform.position = new Vector3(14, 0f, 4);
            gvm.SpawnPoint = spawn.transform; gvm.ExitPoint = exit.transform;
            // zig-zag queue spots from gates backwards
            for (int q = 0; q < 40; q++)
            {
                int row = q / 8; int col = q % 8;
                float x = (row % 2 == 0) ? -4.2f + 1.2f * col : 4.2f - 1.2f * col;
                var spot = new GameObject($"QSpot{q:00}");
                spot.transform.SetParent(queueRoot.transform);
                spot.transform.position = new Vector3(x, 0f, -2.8f - 1.3f * row); // ground level — guest origins are at their feet
                gvm.QueueSpots.Add(spot.transform);
            }
            // Queue rails in serpentine pattern: each row leaves a gap at the turn end
            // (east on even rows, west on odd), matching the zig-zag of the queue spots.
            // Solid colliders so the player (and visually the guests) must snake through.
            var railPrefab = Prop("QueueRail.fbx");
            var railsRoot = new GameObject("QueueRails");
            for (int row = 0; row <= 4; row++)
            {
                bool gapEast = row % 2 == 0;
                for (int mIdx = 0; mIdx < 7; mIdx++)
                {
                    if (gapEast && mIdx == 6) continue;   // turn opening, east end
                    if (!gapEast && mIdx == 0) continue;  // turn opening, west end
                    Vector3 pos = new Vector3(-4.5f + 1.5f * mIdx, 0f, -3.45f - 1.3f * row);
                    GameObject seg;
                    if (railPrefab != null)
                    {
                        seg = PlaceProp(railPrefab, $"QueueRail_{row}_{mIdx}", pos);
                    }
                    else
                    {
                        seg = Box($"QueueRail_{row}_{mIdx}", pos + Vector3.up * 0.45f,
                                  new Vector3(1.5f, 0.08f, 0.08f), mPlatform);
                        seg.transform.position = pos + Vector3.up * 0.45f;
                    }
                    var rc = seg.AddComponent<BoxCollider>();
                    rc.center = new Vector3(0, 0.55f, 0);
                    rc.size = new Vector3(1.5f, 1.1f, 0.14f);
                    seg.transform.SetParent(railsRoot.transform, true);
                }
            }
            // queue entry waypoint: guests come in through the back-row gap
            var qEntry = new GameObject("QueueEntry");
            qEntry.transform.position = new Vector3(4.4f, 0.0f, -9.2f);
            qEntry.transform.SetParent(queueRoot.transform, true);
            gvm.QueueEntryPoint = qEntry.transform;

            // ---- Load / Unload station markers + controller ----
            // The station straight runs z=2.2, x=-6..6. Left half = Unload (RVs drop riders),
            // right half = Load (RVs board). These markers + controller are harmless in single-station
            // mode; they become live when a SimConfig with SeparateLoadUnload is assigned and the
            // TrackPath blocks 0 (Unload) / 1 (Load) are positioned on the station straight.
            System.Func<string, Vector3, GameObject> Marker = (mName, mPos) =>
            {
                var g = new GameObject(mName);
                g.transform.position = mPos;
                return g;
            };

            var stationCtl = new GameObject("StationController").AddComponent<StationController>();
            stationCtl.UnloadStopPoint = Marker("Station_Unload_StopPoint", new Vector3(-3f, 0.6f, 2.2f)).transform;
            stationCtl.LoadStopPoint   = Marker("Station_Load_StopPoint",   new Vector3( 3f, 0.6f, 2.2f)).transform;

            // de-overlap: exit/entry marks re-spaced onto the gate-unit centers and the
            // between-unit post gaps (unload -5/-3.4/-1.8 -> -4.5/-3.0/-1.5, load 1/2.6/4.2 ->
            // 1.5/3.0/4.5) — the old marks put waiting guests inside the 1.05m door swing arcs
            // (e.g. x 2.6 sat 0.5m from a hinge). Unit centers stay outside every leaf's swept
            // sector, verified against the flipped 80-deg south swing incl. overshoot.
            var unloadExits = new List<Transform>();
            for (int e = 0; e < 3; e++)
                unloadExits.Add(Marker($"Guest_Unload_ExitPoint_{e + 1:00}", new Vector3(-4.5f + e * 1.5f, 0f, 0.7f)).transform);
            var loadEntries = new List<Transform>();
            for (int e = 0; e < 3; e++)
                loadEntries.Add(Marker($"Guest_Load_EntryPoint_{e + 1:00}", new Vector3(1.5f + e * 1.5f, 0f, 0.7f)).transform);

            stationCtl.UnloadGuestExitPoints = unloadExits.ToArray();
            stationCtl.LoadGuestEntryPoints  = loadEntries.ToArray();
            // Unloaded riders step off at the Unload side and spread across the exit markers, so they
            // never walk through the boarding queue on the Load side.
            gvm.UnloadExitPoints = unloadExits;

            // ---- Unload door (operator-controlled gate guests leave through) ----
            var unloadDoorRoot = new GameObject("UnloadDoor");
            unloadDoorRoot.transform.position = new Vector3(-5.5f, 0f, 0.5f);
            Box("UnloadDoorFrameL", new Vector3(-6.45f, 1.4f, 0.5f), new Vector3(0.2f, 2.8f, 0.3f), mBooth, unloadDoorRoot.transform);
            Box("UnloadDoorFrameR", new Vector3(-4.55f, 1.4f, 0.5f), new Vector3(0.2f, 2.8f, 0.3f), mBooth, unloadDoorRoot.transform);
            Box("UnloadDoorHeader", new Vector3(-5.5f, 2.7f, 0.5f), new Vector3(2.1f, 0.25f, 0.3f), mBooth, unloadDoorRoot.transform);
            var unloadDoorPanel = Box("UnloadDoorPanel", new Vector3(-5.5f, 1.525f, 0.27f), new Vector3(1.7f, 2.1f, 0.12f), mGate, unloadDoorRoot.transform);
            var udd = unloadDoorRoot.AddComponent<UnloadDoorDriver>();
            udd.DoorPanel = unloadDoorPanel.transform;
            // Side-slide clears a standing crew member above the 0.5m platform without
            // raising the panel through the canopy. Driver sweeps both directions for crew.
            udd.OpenOffset = new Vector3(-2f, 0f, 0f);
            Label(unloadDoorRoot, "UNLOAD DOOR", 2.7f);

            // ---- Exit door (simple primitive; guests walk here then despawn) ----
            var exitDoorRoot = new GameObject("UnloadExitDoor");
            exitDoorRoot.transform.position = new Vector3(-10.5f, 0f, -3f);
            Box("ExitDoorFrameL", new Vector3(-11.4f, 1.1f, -3f), new Vector3(0.2f, 2.3f, 0.3f), mBooth, exitDoorRoot.transform);
            Box("ExitDoorFrameR", new Vector3(-9.6f, 1.1f, -3f), new Vector3(0.2f, 2.3f, 0.3f), mBooth, exitDoorRoot.transform);
            Box("ExitDoorHeader", new Vector3(-10.5f, 2.35f, -3f), new Vector3(2f, 0.25f, 0.3f), mBooth, exitDoorRoot.transform);
            Box("ExitDoorPanel", new Vector3(-10.5f, 1.05f, -3f), new Vector3(1.5f, 2f, 0.1f), mPanel, exitDoorRoot.transform);
            Label(exitDoorRoot, "EXIT", 2.8f);

            // Exit path: Unload side -> off the platform -> exit door (last node = despawn point).
            var exitPath = new List<Transform>
            {
                Marker("GuestExitPath_01", new Vector3(-5f,   0f,  0.0f)).transform,
                Marker("GuestExitPath_02", new Vector3(-7.5f, 0f, -1.2f)).transform,
                Marker("GuestExitPath_03", new Vector3(-9.5f, 0f, -2.6f)).transform,
                Marker("UnloadExitDoorPoint", new Vector3(-10.5f, 0f, -3f)).transform,
            };
            gvm.UnloadExitPath = exitPath;

            // ---- Unload-side OCC (operations control center) consoles: three themed operator
            // pedestals in a shallow arc off the platform's west edge, >=1.2m apart so each is
            // individually aimable from the natural operator spot on the platform (~x -6). The
            // old podium crammed all three buttons 0.25m apart — impossible to aim at one.
            // Order follows the unload flow and keeps the exact same sim verbs (zero logic
            // changes):
            //   amber  DOOR OPEN         -> Kind.UnloadDoorOpen
            //   red    RESTRAINT RELEASE -> Kind.ReleaseRestraints (guarded: rails + shroud)
            //   green  UNLOAD CONFIRM    -> Kind.UnloadDoorClose (closing the unload door IS
            //                               the release-to-load step, so it reads as confirm).
            // Positions stay clear of the unload door (x -6.45..-4.55 at z 0.5), the guest exit
            // markers (z 0.7) and the exit path (-5,0) -> (-7.5,-1.2) -> (-9.5,-2.6).
            // de-overlap: whole trio relocated west of the maintenance ramp. The old arc —
            // DoorOpen (-8.0,0,1.7), Restraint (-8.5,0,0.45), Confirm (-7.9,0,-0.6) — had
            // DoorOpen standing INSIDE the final-brake-run sweep (z 1.7 vs envelope edge 1.25;
            // head/trigger pen up to 0.63m against an arriving train) and the other two buried
            // in RampMaint's plate (x -9.25..-7.05, z -1.5..1.2; plinths clipped up to 0.24m).
            // New shallow arc sits on open ground just west of the ramp foot, keeps the
            // Door -> Restraint -> Confirm walk order (N -> W -> S), faces sweep 150/100/50 deg
            // toward the ramp base, every part+trigger clears the envelope by >=0.15m in z,
            // head-to-head walking gaps are 0.97/0.95m, triggers never touch (min 0.45m apart),
            // and the guest exit path keeps >=1.37m. Verified in the scratch sweep model.
            var mOccHull  = mSeat; // shared dark hull — no new near-black material needed
            var mOccAmber = GlowMat("PS_OccAmber", new Color(0.35f, 0.22f, 0.05f), new Color(1f, 0.62f, 0.10f), 2.2f);
            var mOccRed   = GlowMat("PS_OccRed",   new Color(0.30f, 0.06f, 0.06f), new Color(1f, 0.18f, 0.12f), 2.2f);
            var mOccGreen = GlowMat("PS_OccGreen", new Color(0.05f, 0.28f, 0.10f), new Color(0.25f, 1f, 0.35f), 2.2f);
            var occRoot = new GameObject("UnloadOpsConsoles");
            Transform occOpenCap, occReleaseCap, occCloseCap;
            BuildOpsPedestal("OccConsole_DoorOpen", new Vector3(-9.85f, 0f, 0.6f), 150f,
                mOccHull, mOccAmber, PanelInteractable.Kind.UnloadDoorOpen, "OCC — DOOR OPEN",
                false, occRoot.transform, out occOpenCap);
            BuildOpsPedestal("OccConsole_RestraintRelease", new Vector3(-11.6f, 0f, 0.2f), 100f,
                mOccHull, mOccRed, PanelInteractable.Kind.ReleaseRestraints, "OCC — RESTRAINT RELEASE",
                true, occRoot.transform, out occReleaseCap);
            BuildOpsPedestal("OccConsole_UnloadConfirm", new Vector3(-10.45f, 0f, -1.5f), 50f,
                mOccHull, mOccGreen, PanelInteractable.Kind.UnloadDoorClose, "OCC — UNLOAD CONFIRM",
                false, occRoot.transform, out occCloseCap);
            // Same driver + press feedback as before — only the button transforms moved.
            var upd = occRoot.AddComponent<UnloadPodiumDriver>();
            upd.OpenButton = occOpenCap; upd.CloseButton = occCloseCap; upd.ReleaseButton = occReleaseCap;

            // ================= PARK DRESSING (pure decor — no gameplay wiring) =================
            var parkRoot = new GameObject("ParkDressing");
            // Oversized balloon bouquets frame the plaza without entering the queue lanes.
            for (int side = 0; side < 2; side++)
            {
                var bouquet = new GameObject("CartoonBalloonBouquet" + side);
                bouquet.transform.SetParent(parkRoot.transform, false);
                bouquet.transform.position = new Vector3(side == 0 ? -7f : 8f, 0, -16f);
                Material[] balloonPaint = { mTrainA, mTrainB, mGate };
                for (int b = 0; b < 3; b++)
                {
                    Vector3 anchor = bouquet.transform.position + new Vector3((b - 1) * 0.65f, 0, 0);
                    float height = b == 1 ? 4.5f : 3.9f;
                    var tether = Box("BalloonTether", anchor + Vector3.up * height * 0.5f,
                        new Vector3(0.025f, height, 0.025f), mWhite, bouquet.transform);
                    Object.DestroyImmediate(tether.GetComponent<Collider>());
                    var balloon = Ball("PaintedBalloon", anchor + Vector3.up * height, 1.15f,
                        balloonPaint[b], bouquet.transform);
                    balloon.transform.localScale = new Vector3(1.05f, 1.4f, 1.05f);
                }
            }
            var rng = new System.Random(20260703); // fixed seed: rebuilds are deterministic
            System.Func<float, float, float> Rnd = (lo, hi) => lo + (float)rng.NextDouble() * (hi - lo);

            // ---- Plaza path: colored panels from the spawn plaza to the queue entrance ----
            var pathRoot = new GameObject("PlazaPath");
            pathRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] pathPts = { new Vector3(-2, 0, -18.5f), new Vector3(-2, 0, -11.5f), new Vector3(4.4f, 0, -9.8f) };
            int panelIdx = 0;
            for (int seg = 0; seg + 1 < pathPts.Length; seg++)
            {
                Vector3 a = pathPts[seg], b = pathPts[seg + 1];
                int n = Mathf.Max(1, Mathf.RoundToInt(Vector3.Distance(a, b) / 1.8f));
                for (int k = 0; k < n; k++)
                {
                    Vector3 pos = Vector3.Lerp(a, b, (k + 0.5f) / n) + new Vector3(0, 0.02f, 0);
                    Material pm = (panelIdx % 3 == 0) ? mPathA : (panelIdx % 3 == 1) ? mPathB : mWhite;
                    var panel = Box($"PathPanel{panelIdx:00}", pos, new Vector3(1.7f, 0.04f, 1.7f), pm, pathRoot.transform);
                    panel.transform.rotation = Quaternion.Euler(0, Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg, 0);
                    Object.DestroyImmediate(panel.GetComponent<Collider>()); // flat decor — no ledges, lean physics
                    panelIdx++;
                }
            }
            // Ride-entrance arch over the queue's back-row opening
            Box("ArchPostL", new Vector3(3.1f, 1.25f, -9.3f), new Vector3(0.16f, 2.5f, 0.16f), mWhite, parkRoot.transform);
            Box("ArchPostR", new Vector3(6.0f, 1.25f, -9.3f), new Vector3(0.16f, 2.5f, 0.16f), mWhite, parkRoot.transform);
            var archHeader = Box("ArchHeader", new Vector3(4.55f, 2.62f, -9.3f), new Vector3(3.3f, 0.35f, 0.2f), mRoof, parkRoot.transform);
            Box("ArchNeon", new Vector3(4.55f, 2.42f, -9.3f), new Vector3(3.34f, 0.07f, 0.24f), mNeonCyan, parkRoot.transform);
            Label(archHeader, "RIDE ENTRANCE", 0.55f);

            // ---- Trees: cylinder trunk + two stacked flat-shaded cones ----
            System.Func<string, Vector3, float, Material, Transform, GameObject> Tree = (tName, tPos, s, foliage, tParent) =>
            {
                var t = new GameObject(tName);
                t.transform.position = tPos;
                t.transform.SetParent(tParent, true);
                var trunk = Cyl("Trunk", tPos + new Vector3(0, 0.5f * s, 0), new Vector3(0.16f * s, 0.5f * s, 0.16f * s), mTrunk, t.transform);
                Object.DestroyImmediate(trunk.GetComponent<Collider>());
                Cone("Canopy0", tPos + new Vector3(0, 0.75f * s, 0), 0.85f * s, 1.5f * s, 7, foliage, t.transform);
                Cone("Canopy1", tPos + new Vector3(0, 1.65f * s, 0), 0.55f * s, 1.1f * s, 7, foliage, t.transform);
                return t;
            };
            System.Action<string, Vector3, float, Material> Planter = (pName, pPos, s, foliage) =>
            {
                var shell = Box(pName, pPos + new Vector3(0, 0.25f, 0), new Vector3(1.5f, 0.5f, 1.5f), mPlanter, parkRoot.transform);
                Box(pName + "_Soil", pPos + new Vector3(0, 0.47f, 0), new Vector3(1.3f, 0.12f, 1.3f), mSoil, shell.transform);
                Tree(pName + "_Tree", pPos + new Vector3(0, 0.5f, 0), s, foliage, shell.transform);
            };
            Planter("Planter00", new Vector3(-7f, 0, -9.5f), 1.0f, mFoliageA);
            Planter("Planter01", new Vector3(7.2f, 0, -7f), 0.9f, mFoliageB);
            Planter("Planter02", new Vector3(-7f, 0, -4.5f), 1.1f, mFoliageB);
            Planter("Planter03", new Vector3(-6.5f, 0, -13.5f), 1.0f, mFoliageA);
            Planter("Planter04", new Vector3(7f, 0, -13f), 1.1f, mFoliageA);
            Planter("Planter05", new Vector3(2.5f, 0, -19.8f), 0.9f, mFoliageB);
            Planter("Planter06", new Vector3(14f, 0, -2.5f), 1.0f, mFoliageA);
            Planter("Planter07", new Vector3(-12.3f, 0, -5.5f), 0.9f, mFoliageB);
            // Free-standing trees around the plaza and inside/around the track loop
            var groveRoot = new GameObject("Trees");
            groveRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] treeSpots =
            {
                new Vector3(-12f, 0, -14f), new Vector3(12.5f, 0, -13f), new Vector3(-18f, 0, -7f),
                new Vector3(16.5f, 0, -10f), new Vector3(10f, 0, -18.5f), new Vector3(-9f, 0, -19.5f),
                new Vector3(26f, 0, 15f), new Vector3(-21f, 0, 3f), new Vector3(-6f, 0, 10f),
                new Vector3(5f, 0, 13f), new Vector3(0f, 0, 26f), new Vector3(22f, 0, 26f),
                new Vector3(-28f, 0, 10f), new Vector3(-12f, 0, 26f), new Vector3(25f, 0, 8f),
            };
            for (int tIdx = 0; tIdx < treeSpots.Length; tIdx++)
                Tree($"Tree{tIdx:00}", treeSpots[tIdx], Rnd(0.9f, 1.5f),
                     tIdx % 2 == 0 ? mFoliageA : mFoliageB, groveRoot.transform);

            // ---- Rocks: rotated, squashed cubes in small clusters ----
            var rockRoot = new GameObject("Rocks");
            rockRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] rockSpots =
            {
                new Vector3(17f, 0, 6f), new Vector3(19f, 0, 12f), new Vector3(-19f, 0, 9f),
                new Vector3(-14f, 0, 20f), new Vector3(9f, 0, 24.5f), new Vector3(-2f, 0, 25f),
                new Vector3(14f, 0, -18f), new Vector3(-13f, 0, -18f), new Vector3(-21f, 0, -8f),
            };
            for (int rIdx = 0; rIdx < rockSpots.Length; rIdx++)
            {
                var big = Box($"Rock{rIdx:00}", rockSpots[rIdx] + new Vector3(0, 0.25f, 0),
                              new Vector3(Rnd(1.1f, 1.9f), Rnd(0.6f, 1f), Rnd(0.9f, 1.5f)), mRock, rockRoot.transform);
                big.transform.rotation = Quaternion.Euler(Rnd(-8f, 8f), Rnd(0f, 360f), Rnd(-8f, 8f));
                var small = Box($"Rock{rIdx:00}b", rockSpots[rIdx] + new Vector3(Rnd(0.7f, 1.1f), 0.15f, Rnd(-0.5f, 0.5f)),
                                new Vector3(Rnd(0.4f, 0.7f), Rnd(0.3f, 0.5f), Rnd(0.4f, 0.7f)), mRock, rockRoot.transform);
                small.transform.rotation = Quaternion.Euler(Rnd(-12f, 12f), Rnd(0f, 360f), Rnd(-12f, 12f));
            }

            // ---- Cosmic crystal clusters (emissive, slow pulse) ----
            System.Action<string, Vector3> CrystalCluster = (cName, cPos) =>
            {
                var c = new GameObject(cName);
                c.transform.position = cPos;
                c.transform.SetParent(parkRoot.transform, true);
                Cone("Spire0", cPos, 0.45f, 2.6f, 5, mCrystal, c.transform);
                var s1 = Cone("Spire1", cPos + new Vector3(0.5f, 0, 0.2f), 0.3f, 1.6f, 5, mCrystal, c.transform);
                s1.transform.rotation = Quaternion.Euler(0, 0, -12f);
                var s2 = Cone("Spire2", cPos + new Vector3(-0.4f, 0, -0.3f), 0.24f, 1.2f, 5, mCrystal, c.transform);
                s2.transform.rotation = Quaternion.Euler(8f, 0, 10f);
                var cp = c.AddComponent<PSEnv.NeonPulse>();
                cp.EmissionColor = new Color(0.65f, 0.4f, 1f);
                cp.Intensity = 1.3f; cp.PulseAmount = 0.3f; cp.Speed = 0.8f; cp.Phase = Rnd(0f, 6.28f);
            };
            CrystalCluster("Crystals00", new Vector3(18.5f, 0, 0.5f));
            // de-overlap: Crystals01 moved from (-20, 0, 14) — the diving turn home (WP48->49)
            // passes right through that spot at rail y 1.0..1.7, and the 2.6m main spire stood
            // 0.59m inside the sweep tube (env pen 0.37/0.40). New spot is 5.2m+ from any track.
            CrystalCluster("Crystals01", new Vector3(-23.5f, 0, 11f));
            CrystalCluster("Crystals02", new Vector3(10f, 0, 25f));
            CrystalCluster("Crystals03", new Vector3(-6.5f, 0, -20f));
            CrystalCluster("Crystals04", new Vector3(23.5f, 0, 57f));   // beside the loop
            CrystalCluster("Crystals05", new Vector3(-9f, 0, 71f));     // under the zero-g sector
            CrystalCluster("Crystals06", new Vector3(-29.5f, 0, 45f));  // at the MCBR footings

            // ---- Satellite dishes: tilted low-poly comms dishes scanning the night sky,
            // purple beacon tips blinking via NeonPulse ----
            System.Action<string, Vector3, float, float> Dish = (dName, dPos, dYaw, dPhase) =>
            {
                var dish = new GameObject(dName);
                dish.transform.position = dPos;
                dish.transform.SetParent(parkRoot.transform, true);
                Cyl("DishBase", dPos + new Vector3(0, 0.25f, 0), new Vector3(0.5f, 0.25f, 0.5f), mLampPole, dish.transform);
                Cyl("DishMast", dPos + new Vector3(0, 1.0f, 0), new Vector3(0.14f, 0.6f, 0.14f), mLampPole, dish.transform);
                Vector3 bowlC = dPos + new Vector3(0, 1.9f, 0);
                Vector3 n = Quaternion.Euler(52f, dYaw, 0f) * Vector3.up;
                var bowl = Cyl("DishBowl", bowlC, new Vector3(2.2f, 0.09f, 2.2f), mWhite, dish.transform);
                bowl.transform.rotation = Quaternion.Euler(52f, dYaw, 0f);
                Object.DestroyImmediate(bowl.GetComponent<Collider>());
                var feed = Cyl("DishFeed", bowlC + n * 0.55f, new Vector3(0.05f, 0.55f, 0.05f), mLampPole, dish.transform);
                feed.transform.rotation = Quaternion.Euler(52f, dYaw, 0f);
                Object.DestroyImmediate(feed.GetComponent<Collider>());
                Ball("DishTip", bowlC + n * 1.1f, 0.26f, mNeonPurple, dish.transform);
                var dp = dish.AddComponent<PSEnv.NeonPulse>();
                dp.EmissionColor = new Color(0.62f, 0.35f, 1f);
                dp.Intensity = 2.0f; dp.PulseAmount = 0.45f; dp.Speed = 2.6f; dp.Phase = dPhase;
            };
            Dish("Dish00", new Vector3(26f, 0, 36f), -40f, 0.4f);   // east of the lift hill
            Dish("Dish01", new Vector3(-26f, 0, 60f), 30f, 2.1f);   // inside the western turn
            Dish("Dish02", new Vector3(-20f, 0, -14f), 160f, 4.4f); // plaza south-west corner

            // ---- Lamp posts (warm bulbs; a couple carry real point lights) ----
            System.Func<string, Vector3, bool, GameObject> Lamp = (lName, lPos, realLight) =>
            {
                var lamp = new GameObject(lName);
                lamp.transform.position = lPos;
                lamp.transform.SetParent(parkRoot.transform, true);
                Cyl("Pole", lPos + new Vector3(0, 1.5f, 0), new Vector3(0.09f, 1.5f, 0.09f), mLampPole, lamp.transform);
                Box("Head", lPos + new Vector3(0, 3.12f, 0), new Vector3(0.34f, 0.12f, 0.34f), mLampPole, lamp.transform);
                Ball("Bulb", lPos + new Vector3(0, 2.92f, 0), 0.3f, mLampGlow, lamp.transform);
                var pulse = lamp.AddComponent<PSEnv.NeonPulse>();
                pulse.EmissionColor = new Color(1f, 0.75f, 0.45f);
                pulse.Intensity = 2f; pulse.PulseAmount = 0.08f; pulse.Speed = 2.2f; pulse.Phase = Rnd(0f, 6.28f);
                if (realLight)
                {
                    var lgo = new GameObject("PointLight");
                    lgo.transform.SetParent(lamp.transform, false);
                    lgo.transform.position = lPos + new Vector3(0, 2.7f, 0);
                    var l = lgo.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.range = 8f; l.intensity = 1.2f;
                    l.color = new Color(1f, 0.78f, 0.5f);
                    l.shadows = LightShadows.None;
                    pulse.LinkedLight = l; pulse.LightIntensity = 1.2f;
                }
                return lamp;
            };
            Lamp("Lamp00", new Vector3(-4.8f, 0, -11.2f), true);   // plaza, queue side
            Lamp("Lamp01", new Vector3(5.6f, 0, -10.6f), true);    // by the entrance arch
            Lamp("Lamp02", new Vector3(-2f, 0, -18.6f), false);
            Lamp("Lamp03", new Vector3(8f, 0, -14f), false);
            Lamp("Lamp04", new Vector3(-8f, 0, -15f), false);
            Lamp("Lamp05", new Vector3(13.5f, 0, -5f), false);     // outside the booth
            Lamp("Lamp06", new Vector3(-12.5f, 0, -1.5f), false);  // by the exit door path

            // ---- String lights zig-zagging over the queue ----
            var stringRoot = new GameObject("StringLights");
            stringRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] sPoles =
            {
                new Vector3(-5.9f, 0, -3.6f), new Vector3(5.9f, 0, -5.2f),
                new Vector3(-5.9f, 0, -6.8f), new Vector3(5.9f, 0, -8.4f),
            };
            const float poleTop = 2.8f;
            foreach (var sp in sPoles)
                Cyl("StringPole", sp + new Vector3(0, poleTop * 0.5f, 0), new Vector3(0.07f, poleTop * 0.5f, 0.07f), mLampPole, stringRoot.transform);
            for (int span = 0; span + 1 < sPoles.Length; span++)
            {
                Vector3 a = sPoles[span] + Vector3.up * poleTop;
                Vector3 b = sPoles[span + 1] + Vector3.up * poleTop;
                var wire = Box($"StringWire{span}", (a + b) * 0.5f, new Vector3(0.03f, 0.03f, Vector3.Distance(a, b)), mLampPole, stringRoot.transform);
                wire.transform.rotation = Quaternion.LookRotation(b - a);
                Object.DestroyImmediate(wire.GetComponent<Collider>());
                const int bulbs = 15;
                for (int k = 1; k < bulbs; k++)
                {
                    float t = k / (float)bulbs;
                    Vector3 bp2 = Vector3.Lerp(a, b, t) + Vector3.down * (0.45f * 4f * t * (1f - t) + 0.08f);
                    Ball($"StringBulb{span}_{k:00}", bp2, 0.16f, mStringBulb, stringRoot.transform);
                }
            }

            // ---- Maintenance bay dressing: back wall, hazard trim, work lamp ----
            Box("MaintWall", new Vector3(-14, 1.3f, -7.4f), new Vector3(8, 2.6f, 0.2f), mBooth);
            Box("MaintTrim", new Vector3(-14, 2.66f, -7.4f), new Vector3(8.2f, 0.1f, 0.26f), mNeonMagenta);
            Lamp("MaintLamp", new Vector3(-10.8f, 0, -6.6f), true);

            // ---- Horizon: dark crater ridges ringing the site so the skyline isn't empty.
            // Pushed out past the enlarged floor and recentered on the bigger footprint;
            // occasional twin peaks read as crater rims against the star field. ----
            var hillsRoot = new GameObject("CraterRidges");
            hillsRoot.transform.SetParent(parkRoot.transform, false);
            for (int hIdx = 0; hIdx < 16; hIdx++)
            {
                float ang = hIdx * (Mathf.PI * 2f / 16f) + Rnd(0f, 0.28f);
                float dist = 95f + Rnd(0f, 38f);
                var hp = new Vector3(Mathf.Cos(ang) * dist, -1.5f, Mathf.Sin(ang) * dist + 28f);
                Material hm = hIdx % 3 == 0 ? mHillFar : mHill;
                Cone($"CraterRidge{hIdx:00}", hp, Rnd(16f, 30f), Rnd(8f, 18f), 8, hm, hillsRoot.transform);
                if (hIdx % 4 == 0)
                    Cone($"CraterRim{hIdx:00}", hp + new Vector3(Rnd(10f, 16f), 0.4f, Rnd(-6f, 6f)),
                         Rnd(9f, 14f), Rnd(5f, 9f), 7, hm, hillsRoot.transform);
            }

            // ---- Managers, spawn, bootstrap ----
            var attractionMgr = new GameObject("AttractionManager").AddComponent<AttractionManager>();
            attractionMgr.SeparateLoadUnload = true;  // station split: block 0 = Unload, block 1 = Load
            attractionMgr.RvCount = 2;                 // one RV at Unload, one at Load (matches the 5-block track)
            attractionMgr.EscalatedShift = true;       // Wave-1 content: extended fault catalog, fault/arrival
                                                       // ramps + surge waves (inspector preset defaults).
                                                       // Applied in the Unity layer only — Core defaults untouched.
            attractionMgr.RvPrefab = trainPrefab;      // for runtime "Add RV" from the HMI (null-safe)
            attractionMgr.Track = path;
            attractionMgr.GuestVisuals = gvm;
            attractionMgr.AddedRvMaterial = mTrainB;
            // ---- Juice: procedural audio + particle FX + game-feel director (no asset files;
            // everything is synthesized/configured in code at runtime). All components degrade
            // gracefully if a reference is missing, so rebuilds stay safe. ----
            var juiceGO = new GameObject("Juice");
            var audioDir = juiceGO.AddComponent<ParkShift.Juice.AudioDirector>();
            juiceGO.AddComponent<ParkShift.Juice.JuiceFx>();
            var juiceDir = juiceGO.AddComponent<ParkShift.Juice.JuiceDirector>();
            var queueCenter = new GameObject("QueueCenter");
            queueCenter.transform.SetParent(juiceGO.transform, false);
            queueCenter.transform.position = new Vector3(0f, 1.2f, -5.6f);   // middle of the serpentine
            audioDir.QueueCenter = queueCenter.transform;                    // crowd walla + grumble loops
            audioDir.StationCenter = stationCtl.LoadStopPoint;               // fault klaxon
            juiceDir.QueueCenter = queueCenter.transform;                    // cheer origin
            juiceDir.StationCenter = stationCtl.LoadStopPoint;               // confetti + dispatch FX
            juiceDir.GateCenter = gateRoot.transform;                        // gate servo audio

            // Clickable ride HMI (opened from the console screen).
            new GameObject("HmiPanel").AddComponent<ParkShift.UI.HmiPanel>();
            new GameObject("NetworkBootstrap").AddComponent<Networking.NetworkBootstrap>();
            new GameObject("GameManager").AddComponent<GameManager>();
            var ps = new GameObject("PlayerSpawn"); ps.transform.position = new Vector3(10, 1.1f, -1.5f);

#if FISHNET
            ParkShift.Editor.LanSceneSetup.ConfigureCurrentScene();
#endif
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/PrototypeAttraction.unity");
        }
    }
}
// (wave-1 integration pass)
