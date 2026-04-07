using UnityEngine;
/// <summary>
/// 代码生成球体mesh
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ProceduralSphere_FBXStyle : MonoBehaviour
{
    [Header("Sphere Settings")]
    [Range(3, 256)]
    public int latitudeSegments = 48;

    [Range(3, 256)]
    public int longitudeSegments = 96;

    public float radius = 1f;

    public bool generateOnStart = false;

    void Start()
    {
        if (generateOnStart)
            GenerateSphere();
    }

    [ContextMenu("Generate Sphere")]
    public void GenerateSphere()
    {
        Mesh mesh = new Mesh();
        mesh.name = "Procedural Sphere FBX Style";

        int vertCount = (latitudeSegments + 1) * (longitudeSegments + 1);
        Vector3[] vertices = new Vector3[vertCount];
        Vector3[] normals = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];

        int[] triangles = new int[latitudeSegments * longitudeSegments * 6];

        int v = 0;
        int t = 0;

        for (int lat = 0; lat <= latitudeSegments; lat++)
        {
            float a1 = Mathf.PI * lat / latitudeSegments;
            float sin1 = Mathf.Sin(a1);
            float cos1 = Mathf.Cos(a1);

            for (int lon = 0; lon <= longitudeSegments; lon++)
            {
                float a2 = 2 * Mathf.PI * lon / longitudeSegments;
                float sin2 = Mathf.Sin(a2);
                float cos2 = Mathf.Cos(a2);

                Vector3 pos = new Vector3(
                    sin1 * cos2,
                    cos1,
                    sin1 * sin2
                ) * radius;

                vertices[v] = pos;

                // FBX 标准：法线朝外
                normals[v] = pos.normalized;

                uvs[v] = new Vector2(
                    (float)lon / longitudeSegments,
                    1f - (float)lat / latitudeSegments
                );

                if (lat < latitudeSegments && lon < longitudeSegments)
                {
                    int current = v;
                    int next = v + longitudeSegments + 1;

                    // 顺时针 (Unity 正面)
                    triangles[t++] = current;
                    triangles[t++] = current + 1;
                    triangles[t++] = next;

                    triangles[t++] = current + 1;
                    triangles[t++] = next + 1;
                    triangles[t++] = next;
                }

                v++;
            }
        }

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;

        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().mesh = mesh;

        Debug.Log($"FBX Style Sphere Generated\nVertices: {vertices.Length}\nTriangles: {triangles.Length / 3}");
    }
}