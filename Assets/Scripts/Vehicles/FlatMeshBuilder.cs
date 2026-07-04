using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Append-style mesh builder for the stylized low-poly look (Overcooked/Astroneer):
    /// every face gets its OWN vertices (nothing shared between faces), so
    /// RecalculateNormals() produces hard, flat-shaded facets — chunky by construction.
    /// Shared by TrackMeshBuilder and TrainCarFactory. Runtime-safe (no UnityEditor).
    ///
    /// Winding convention (Unity, left-handed): a triangle (a, b, c) is front-facing with
    /// normal = cross(b - a, c - a). AddQuad(a, b, c, d) fans as (a,b,c) + (a,c,d).
    /// Loft/prism profiles are 2D points ordered counter-clockwise in local X/Y
    /// (viewed looking down local -Z), extruded along local +Z.
    /// </summary>
    public class FlatMeshBuilder
    {
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Vector2> _uvs = new List<Vector2>();
        readonly List<int> _tris = new List<int>();

        public int VertexCount { get { return _verts.Count; } }

        // ---------------- primitives ----------------

        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector2 uvA, Vector2 uvB, Vector2 uvC)
        {
            int i = _verts.Count;
            _verts.Add(a); _verts.Add(b); _verts.Add(c);
            _uvs.Add(uvA); _uvs.Add(uvB); _uvs.Add(uvC);
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
        }

        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            AddTriangle(a, b, c, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 1f));
        }

        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                            Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
        {
            int i = _verts.Count;
            _verts.Add(a); _verts.Add(b); _verts.Add(c); _verts.Add(d);
            _uvs.Add(uvA); _uvs.Add(uvB); _uvs.Add(uvC); _uvs.Add(uvD);
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
            _tris.Add(i); _tris.Add(i + 2); _tris.Add(i + 3);
        }

        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            AddQuad(a, b, c, d,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));
        }

        // ---------------- box ----------------

        // Unit-cube corners (centered): 0..3 bottom ring, 4..7 top ring.
        static readonly Vector3[] BoxCorners =
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, 0.5f),   new Vector3(-0.5f, -0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, -0.5f),  new Vector3(0.5f, 0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, 0.5f),    new Vector3(-0.5f, 0.5f, 0.5f),
        };
        // Faces as quads (a,b,c,d) with outward normals under this class's winding rule.
        static readonly int[][] BoxFaces =
        {
            new[] { 4, 7, 6, 5 },  // top    (+Y)
            new[] { 0, 1, 2, 3 },  // bottom (-Y)
            new[] { 3, 2, 6, 7 },  // front  (+Z)
            new[] { 1, 0, 4, 5 },  // back   (-Z)
            new[] { 2, 1, 5, 6 },  // right  (+X)
            new[] { 0, 3, 7, 4 },  // left   (-X)
        };

        /// <summary>Adds an oriented box (all 6 faces hard-edged).</summary>
        public void AddBox(Vector3 center, Quaternion rot, Vector3 size)
        {
            var w = new Vector3[8];
            for (int i = 0; i < 8; i++)
                w[i] = center + rot * Vector3.Scale(BoxCorners[i], size);
            for (int f = 0; f < BoxFaces.Length; f++)
            {
                int[] face = BoxFaces[f];
                AddQuad(w[face[0]], w[face[1]], w[face[2]], w[face[3]]);
            }
        }

        // ---------------- loft / extrusion / prism ----------------

        /// <summary>
        /// Lofts profile A (at local z = zA) to profile B (at local z = zB &gt; zA), both CCW
        /// and of equal length, in the frame (origin, rot). Optional flat end caps
        /// (profiles must be convex for the caps' centroid fans to be correct).
        /// </summary>
        public void AddLoft(Vector3 origin, Quaternion rot, Vector2[] profA, float zA,
                            Vector2[] profB, float zB, bool capA, bool capB)
        {
            int n = profA.Length;
            var A = new Vector3[n];
            var B = new Vector3[n];
            Vector2 cenA2 = Vector2.zero, cenB2 = Vector2.zero;
            for (int k = 0; k < n; k++)
            {
                A[k] = origin + rot * new Vector3(profA[k].x, profA[k].y, zA);
                B[k] = origin + rot * new Vector3(profB[k].x, profB[k].y, zB);
                cenA2 += profA[k];
                cenB2 += profB[k];
            }
            cenA2 /= n; cenB2 /= n;
            Vector3 cenA = origin + rot * new Vector3(cenA2.x, cenA2.y, zA);
            Vector3 cenB = origin + rot * new Vector3(cenB2.x, cenB2.y, zB);

            for (int k = 0; k < n; k++)
            {
                int k2 = (k + 1) % n;
                float v0 = k / (float)n, v1 = (k + 1) / (float)n;
                AddQuad(A[k], A[k2], B[k2], B[k],
                    new Vector2(0f, v0), new Vector2(0f, v1), new Vector2(1f, v1), new Vector2(1f, v0));
                if (capB) AddTriangle(cenB, B[k], B[k2]);
                if (capA) AddTriangle(cenA, A[k2], A[k]);
            }
        }

        /// <summary>Straight extrusion of one profile from -halfLength to +halfLength along local Z.</summary>
        public void AddExtrusion(Vector3 center, Quaternion rot, Vector2[] profile,
                                 float halfLength, bool capStart, bool capEnd)
        {
            AddLoft(center, rot, profile, -halfLength, profile, halfLength, capStart, capEnd);
        }

        /// <summary>Regular n-gon prism along local Z with flat caps. The first flat sits on top
        /// (angle offset by half a step) so octagons read as chunky rails, not diamonds.</summary>
        public void AddPrism(Vector3 center, Quaternion rot, float radius, float halfLength, int sides)
        {
            AddExtrusion(center, rot, NGon(radius, sides), halfLength, true, true);
        }

        /// <summary>CCW n-gon profile, rotated half a step so a flat faces up.</summary>
        public static Vector2[] NGon(float radius, int sides)
        {
            var p = new Vector2[sides];
            for (int k = 0; k < sides; k++)
            {
                float ang = (k + 0.5f) / sides * Mathf.PI * 2f;
                p[k] = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius;
            }
            return p;
        }

        // ---------------- output ----------------

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(_verts);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateNormals();   // per-face verts => hard flat normals
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
