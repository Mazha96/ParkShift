using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Generates a single banked track mesh (two rails + ties) along a TrackPath spline.
    /// Runtime-safe (no UnityEditor) so SceneBuilder and any future track editor can both use it.
    /// </summary>
    public static class TrackMeshBuilder
    {
        const float RailOffset = 0.55f;   // lateral distance from centerline to each rail center
        const float RailWidth = 0.14f;
        const float RailHeight = 0.16f;
        const float RingStep = 0.6f;      // meters between rail cross-sections
        const float TieStep = 1.15f;      // meters between ties
        static readonly Vector3 TieSize = new Vector3(1.7f, 0.1f, 0.32f);

        public static GameObject Build(TrackPath path, Material railMat, Material tieMat)
        {
            path.Rebuild();
            float len = path.TotalLength;

            var root = new GameObject("TrackMesh");
            root.transform.SetParent(path.transform, false);

            root.isStatic = true;
            CreateChild(root, "Rails", BuildRails(path, len), railMat);
            CreateChild(root, "Ties", BuildTies(path, len), tieMat);
            return root;
        }

        static void CreateChild(GameObject root, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static Mesh BuildRails(TrackPath path, float len)
        {
            int rings = Mathf.Max(8, Mathf.CeilToInt(len / RingStep));
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            // Two rails, each a closed quad tube with a 4-corner cross-section.
            for (int side = -1; side <= 1; side += 2)
            {
                int baseIndex = verts.Count;
                for (int r = 0; r < rings; r++)
                {
                    float d = (r / (float)rings) * len;
                    path.FrameAtDistance(d, out var pos, out var rot, out _);
                    Vector3 right = rot * Vector3.right;
                    Vector3 up = rot * Vector3.up;
                    Vector3 c = pos + right * (side * RailOffset);
                    float hw = RailWidth * 0.5f;
                    // corners: top-left, top-right, bottom-right, bottom-left
                    verts.Add(c - right * hw);
                    verts.Add(c + right * hw);
                    verts.Add(c + right * hw - up * RailHeight);
                    verts.Add(c - right * hw - up * RailHeight);
                    float u = d / 2f; // brushed-metal streaks run along the rail
                    uvs.Add(new Vector2(u, 0f));
                    uvs.Add(new Vector2(u, 0.33f));
                    uvs.Add(new Vector2(u, 0.66f));
                    uvs.Add(new Vector2(u, 1f));
                }
                for (int r = 0; r < rings; r++)
                {
                    int a = baseIndex + r * 4;
                    int b = baseIndex + ((r + 1) % rings) * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        int k2 = (k + 1) % 4;
                        tris.Add(a + k); tris.Add(b + k); tris.Add(b + k2);
                        tris.Add(a + k); tris.Add(b + k2); tris.Add(a + k2);
                    }
                }
            }

            var mesh = new Mesh { name = "Rails" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildTies(TrackPath path, float len)
        {
            int count = Mathf.Max(4, Mathf.CeilToInt(len / TieStep));
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            Vector2[] faceUV = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };

            // Unit cube corners (centered), faces as quads with duplicated verts for hard edges.
            Vector3[] c =
            {
                new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f),
                new Vector3(0.5f,-0.5f,0.5f),  new Vector3(-0.5f,-0.5f,0.5f),
                new Vector3(-0.5f,0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f),
                new Vector3(0.5f,0.5f,0.5f),   new Vector3(-0.5f,0.5f,0.5f),
            };
            int[][] faces =
            {
                new[]{4,5,6,7}, new[]{1,0,3,2},  // top, bottom
                new[]{0,1,5,4}, new[]{2,3,7,6},  // back, front
                new[]{3,0,4,7}, new[]{1,2,6,5},  // left, right
            };

            for (int i = 0; i < count; i++)
            {
                float d = (i / (float)count) * len;
                path.FrameAtDistance(d, out var pos, out var rot, out _);
                Vector3 center = pos + rot * (Vector3.down * (RailHeight + TieSize.y * 0.5f));
                foreach (var face in faces)
                {
                    int vi = verts.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        verts.Add(center + rot * Vector3.Scale(c[face[k]], TieSize));
                        uvs.Add(faceUV[k]);
                    }
                    tris.Add(vi); tris.Add(vi + 2); tris.Add(vi + 1);
                    tris.Add(vi); tris.Add(vi + 3); tris.Add(vi + 2);
                }
            }

            var mesh = new Mesh { name = "Ties" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
