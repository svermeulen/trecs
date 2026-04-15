using UnityEngine;

namespace Trecs.Samples
{
    public static class SampleUtil
    {
        public static Mesh ExtractMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            GameObject.Destroy(go);
            return mesh;
        }

        public static Material CreateMaterial(Color color)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
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
