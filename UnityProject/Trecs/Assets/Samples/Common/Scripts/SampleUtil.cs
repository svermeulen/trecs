using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Trecs.Samples
{
    public static class SampleUtil
    {
        public static bool IsUrp => GraphicsSettings.currentRenderPipeline != null;

        public static Mesh ExtractMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            GameObject.Destroy(go);
            return mesh;
        }

        // Wraps GameObject.CreatePrimitive so primitives render correctly under URP.
        // Unity's Default-Material uses the Built-in Standard shader, which shows up
        // pink in URP builds — replace it with a URP/Lit material when URP is active.
        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            if (IsUrp)
                go.GetComponent<MeshRenderer>().sharedMaterial = CreateMaterial(Color.white);
            return go;
        }

        public static Material CreateMaterial(Color color)
        {
            var shaderName = IsUrp ? "Universal Render Pipeline/Lit" : "Standard";
            var material = new Material(Shader.Find(shaderName));
            material.color = color;
            material.enableInstancing = true;
            return material;
        }

        public static Material CreateUnlitIndirectMaterial(Color color)
        {
            var material = new Material(Shader.Find("Trecs/InstancedIndirectUnlit"));
            material.SetColor("_BaseColor", color);
            return material;
        }

        // Creates a cube mesh scaled to the given size (default matches carrot bounding box).
        public static Mesh CreateScaledCubeMesh(
            float sizeX = 0.2f,
            float sizeY = 0.4f,
            float sizeZ = 0.2f
        )
        {
            var baseMesh = ExtractMesh(PrimitiveType.Cube);
            var baseVerts = baseMesh.vertices;
            var scaledVerts = new Vector3[baseVerts.Length];
            for (int i = 0; i < baseVerts.Length; i++)
            {
                scaledVerts[i] = new Vector3(
                    baseVerts[i].x * sizeX,
                    baseVerts[i].y * sizeY,
                    baseVerts[i].z * sizeZ
                );
            }

            var mesh = new Mesh();
            mesh.name = "ScaledCube";
            mesh.vertices = scaledVerts;
            mesh.normals = baseMesh.normals;
            mesh.uv = baseMesh.uv;
            mesh.triangles = baseMesh.triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        // Generates a low-poly icosphere (1 subdivision = 80 triangles, 42 vertices).
        // Much cheaper than Unity's built-in sphere (~768 tris) while still looking round.
        public static Mesh CreateIcosphereMesh()
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;

            var verts = new List<Vector3>
            {
                new(-1, t, 0),
                new(1, t, 0),
                new(-1, -t, 0),
                new(1, -t, 0),
                new(0, -1, t),
                new(0, 1, t),
                new(0, -1, -t),
                new(0, 1, -t),
                new(t, 0, -1),
                new(t, 0, 1),
                new(-t, 0, -1),
                new(-t, 0, 1),
            };

            for (int i = 0; i < verts.Count; i++)
                verts[i] = verts[i].normalized;

            var tris = new List<int>
            {
                0,
                11,
                5,
                0,
                5,
                1,
                0,
                1,
                7,
                0,
                7,
                10,
                0,
                10,
                11,
                1,
                5,
                9,
                5,
                11,
                4,
                11,
                10,
                2,
                10,
                7,
                6,
                7,
                1,
                8,
                3,
                9,
                4,
                3,
                4,
                2,
                3,
                2,
                6,
                3,
                6,
                8,
                3,
                8,
                9,
                4,
                9,
                5,
                2,
                4,
                11,
                6,
                2,
                10,
                8,
                6,
                7,
                9,
                8,
                1,
            };

            // Subdivide once
            var midpointCache = new Dictionary<long, int>();

            int GetMidpoint(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (midpointCache.TryGetValue(key, out int cached))
                    return cached;

                var mid = ((verts[a] + verts[b]) * 0.5f).normalized;
                int idx = verts.Count;
                verts.Add(mid);
                midpointCache[key] = idx;
                return idx;
            }

            var newTris = new List<int>();
            for (int i = 0; i < tris.Count; i += 3)
            {
                int v0 = tris[i],
                    v1 = tris[i + 1],
                    v2 = tris[i + 2];
                int m01 = GetMidpoint(v0, v1);
                int m12 = GetMidpoint(v1, v2);
                int m20 = GetMidpoint(v2, v0);

                newTris.AddRange(new[] { v0, m01, m20 });
                newTris.AddRange(new[] { m01, v1, m12 });
                newTris.AddRange(new[] { m20, m12, v2 });
                newTris.AddRange(new[] { m01, m12, m20 });
            }

            var mesh = new Mesh();
            mesh.name = "Icosphere";
            mesh.SetVertices(verts);
            mesh.SetTriangles(newTris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Generates a 3D dart/boid shape — tapered body with pointed nose.
        // Forward direction is +Z. Slightly flattened vertically for an organic silhouette.
        public static Mesh CreateDartMesh(int segments = 6)
        {
            // Sized to roughly match the bunny mesh bounding box (~1.25 x 2.0 x 1.33)
            float length = 2.0f;
            float noseZ = length * 0.5f;
            float shoulderZ = length * 0.1f;
            float tailZ = -length * 0.5f;
            float shoulderRadius = 0.5f;
            float tailRadius = 0.2f;
            float verticalFlatten = 0.7f;

            // Vertices: nose(1) + shoulder ring(segments) + tail ring(segments) + tail center(1)
            int vertCount = 2 + segments * 2;
            var vertices = new Vector3[vertCount];
            var triangles = new int[(segments * 4) * 3]; // nose tris + shoulder-to-tail quads + tail cap

            // Shift mesh up so bottom sits at Y=0 (like the bunny model)
            float yOffset = shoulderRadius * verticalFlatten;

            // Nose tip
            vertices[0] = new Vector3(0, yOffset, noseZ);

            // Shoulder ring
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * Mathf.PI * i / segments;
                float x = Mathf.Cos(angle) * shoulderRadius;
                float y = Mathf.Sin(angle) * shoulderRadius * verticalFlatten;
                vertices[1 + i] = new Vector3(x, y + yOffset, shoulderZ);
            }

            // Tail ring
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * Mathf.PI * i / segments;
                float x = Mathf.Cos(angle) * tailRadius;
                float y = Mathf.Sin(angle) * tailRadius * verticalFlatten;
                vertices[1 + segments + i] = new Vector3(x, y + yOffset, tailZ);
            }

            // Tail center
            vertices[vertCount - 1] = new Vector3(0, yOffset, tailZ);

            int tri = 0;

            // Nose to shoulder triangles
            for (int i = 0; i < segments; i++)
            {
                int cur = 1 + i;
                int next = 1 + (i + 1) % segments;
                triangles[tri++] = 0;
                triangles[tri++] = cur;
                triangles[tri++] = next;
            }

            // Shoulder to tail quads (2 tris each)
            for (int i = 0; i < segments; i++)
            {
                int sCur = 1 + i;
                int sNext = 1 + (i + 1) % segments;
                int tCur = 1 + segments + i;
                int tNext = 1 + segments + (i + 1) % segments;

                triangles[tri++] = sCur;
                triangles[tri++] = tCur;
                triangles[tri++] = sNext;

                triangles[tri++] = sNext;
                triangles[tri++] = tCur;
                triangles[tri++] = tNext;
            }

            // Tail cap
            int tailCenter = vertCount - 1;
            for (int i = 0; i < segments; i++)
            {
                int cur = 1 + segments + i;
                int next = 1 + segments + (i + 1) % segments;
                triangles[tri++] = tailCenter;
                triangles[tri++] = next;
                triangles[tri++] = cur;
            }

            var mesh = new Mesh();
            mesh.name = "DartMesh";
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
