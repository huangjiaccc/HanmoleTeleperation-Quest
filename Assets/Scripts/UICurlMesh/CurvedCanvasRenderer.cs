#if !IS_ANDROID
using UnityEngine;
using UnityEngine.UI;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;

[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Canvas))]
public class CurvedCanvasRenderer : MonoBehaviour
{
    [Header("Curve Settings (Canvas Units)")]
    [Min(0.01f)]
    [SerializeField] private float radius = 1000f;
    [SerializeField] private bool invert = true;
    [Range(1, 128)]
    [SerializeField] private int horizontalSegments = 64;

    [Header("RenderTexture")]
    [SerializeField] private int renderWidth = 0;
    [SerializeField] private int renderHeight = 0;
    [SerializeField] private Color clearColor = new Color(0f, 0f, 0f, 0f);

    [Header("Behavior")]
    [SerializeField] private bool includeInactive = true;
    [SerializeField] private bool applyOnEnable = true;
    [SerializeField] private bool disableChildCurveEffects = true;
    [SerializeField] private bool forceWorldSpaceCanvas = true;

    [Header("Interaction SDK (Ray)")]
    [SerializeField] private bool enableRayInteractorSurface = true;

    private const string CameraName = "UICurveCamera";
    private const string SurfaceName = "UICurveSurface";

    private Canvas targetCanvas;
    private GraphicRaycaster graphicRaycaster;
    private PointableCanvas pointableCanvas;
    private Camera uiCamera;
    private RenderTexture runtimeTexture;
    private MeshFilter surfaceFilter;
    private MeshRenderer surfaceRenderer;
    private MeshCollider surfaceCollider;
    private CurvedUiRaycaster surfaceRaycaster;
    private readonly System.Collections.Generic.List<UICurveMeshEffect> disabledCurveEffects = new System.Collections.Generic.List<UICurveMeshEffect>();
    private int cachedWidth;
    private int cachedHeight;

    private void OnEnable()
    {
        if (applyOnEnable)
        {
            Setup();
        }
    }

    private void OnValidate()
    {
        Setup();
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        RestoreChildCurveEffects();

        if (runtimeTexture != null)
        {
            runtimeTexture.Release();
            Destroy(runtimeTexture);
            runtimeTexture = null;
        }
    }

    private void Setup()
    {
        targetCanvas = GetComponent<Canvas>();
        if (targetCanvas == null)
        {
            return;
        }

        graphicRaycaster = GetComponent<GraphicRaycaster>();
        if (graphicRaycaster == null)
        {
            graphicRaycaster = gameObject.AddComponent<GraphicRaycaster>();
        }

        EnsurePointableCanvas();

        EnsureCamera();
        UpdateCameraCullingMask();
        EnsureRenderTexture();
        EnsureSurface();
        DisableChildCurveEffects();
        UpdateSurfaceMesh();
        UpdateSurfaceMaterial();

        if (forceWorldSpaceCanvas && targetCanvas.renderMode != RenderMode.WorldSpace)
        {
            targetCanvas.renderMode = RenderMode.WorldSpace;
        }

        if (targetCanvas.renderMode == RenderMode.WorldSpace)
        {
            targetCanvas.worldCamera = uiCamera;
        }
    }

    private void EnsureCamera()
    {
        if (uiCamera != null)
        {
            return;
        }

        Transform existing = transform.Find(CameraName);
        if (existing != null)
        {
            uiCamera = existing.GetComponent<Camera>();
        }

        if (uiCamera == null)
        {
            GameObject camObject = new GameObject(CameraName);
            camObject.transform.SetParent(transform, false);
            uiCamera = camObject.AddComponent<Camera>();
        }

        uiCamera.clearFlags = CameraClearFlags.SolidColor;
        uiCamera.backgroundColor = clearColor;
        uiCamera.orthographic = true;
        uiCamera.cullingMask = 1 << gameObject.layer;
        uiCamera.allowHDR = false;
        uiCamera.allowMSAA = false;
        uiCamera.nearClipPlane = 0.01f;
        uiCamera.farClipPlane = 100f;
        uiCamera.transform.localPosition = new Vector3(0f, 0f, -10f);
        uiCamera.transform.localRotation = Quaternion.identity;
    }

    private void UpdateCameraCullingMask()
    {
        if (uiCamera == null)
        {
            return;
        }

        int mask = 0;
        var graphics = GetComponentsInChildren<Graphic>(includeInactive);
        for (int i = 0; i < graphics.Length; i++)
        {
            var graphic = graphics[i];
            if (graphic != null)
            {
                mask |= 1 << graphic.gameObject.layer;
            }
        }

        if (mask == 0)
        {
            mask = 1 << gameObject.layer;
        }

        uiCamera.cullingMask = mask;
    }

    private void EnsureRenderTexture()
    {
        int width = renderWidth;
        int height = renderHeight;
        RectTransform rectTransform = targetCanvas.transform as RectTransform;
        if (width <= 0 && rectTransform != null)
        {
            width = Mathf.Max(1, Mathf.RoundToInt(rectTransform.rect.width));
        }
        if (height <= 0 && rectTransform != null)
        {
            height = Mathf.Max(1, Mathf.RoundToInt(rectTransform.rect.height));
        }

        if (width <= 0) { width = 1024; }
        if (height <= 0) { height = 1024; }

        cachedWidth = width;
        cachedHeight = height;

        if (runtimeTexture == null || runtimeTexture.width != width || runtimeTexture.height != height)
        {
            if (runtimeTexture != null)
            {
                runtimeTexture.Release();
                DestroyImmediate(runtimeTexture);
            }

            runtimeTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
            {
                name = $"{name}_UICurveRT",
                antiAliasing = 1,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            runtimeTexture.Create();
        }

        uiCamera.targetTexture = runtimeTexture;
        UpdateCameraFraming(width, height, rectTransform);
    }

    private void EnsureSurface()
    {
        if (surfaceFilter != null && surfaceRenderer != null && surfaceCollider != null && surfaceRaycaster != null)
        {
            return;
        }

        Transform existing = transform.Find(SurfaceName);
        if (existing == null)
        {
            GameObject surface = new GameObject(SurfaceName);
            surface.transform.SetParent(transform, false);
            existing = surface.transform;
        }

        surfaceFilter = existing.GetComponent<MeshFilter>();
        if (surfaceFilter == null)
        {
            surfaceFilter = existing.gameObject.AddComponent<MeshFilter>();
        }

        surfaceRenderer = existing.GetComponent<MeshRenderer>();
        if (surfaceRenderer == null)
        {
            surfaceRenderer = existing.gameObject.AddComponent<MeshRenderer>();
        }

        surfaceCollider = existing.GetComponent<MeshCollider>();
        if (surfaceCollider == null)
        {
            surfaceCollider = existing.gameObject.AddComponent<MeshCollider>();
        }

        surfaceRaycaster = existing.GetComponent<CurvedUiRaycaster>();
        if (surfaceRaycaster == null)
        {
            surfaceRaycaster = existing.gameObject.AddComponent<CurvedUiRaycaster>();
        }

        surfaceRaycaster.SetTargets(surfaceCollider, graphicRaycaster, uiCamera);
        EnsureRayInteractorSurface();
    }

    private void UpdateSurfaceMaterial()
    {
        if (surfaceRenderer == null)
        {
            return;
        }

        if (surfaceRenderer.sharedMaterial == null)
        {
            Shader shader = Shader.Find("Unlit/Transparent");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Texture");
            }

            surfaceRenderer.sharedMaterial = shader != null ? new Material(shader) : new Material(Shader.Find("Sprites/Default"));
        }

        if (runtimeTexture != null)
        {
            surfaceRenderer.sharedMaterial.mainTexture = runtimeTexture;
        }
    }

    private void UpdateSurfaceMesh()
    {
        if (surfaceFilter == null || targetCanvas == null)
        {
            return;
        }

        RectTransform rectTransform = targetCanvas.transform as RectTransform;
        if (rectTransform == null)
        {
            return;
        }

        int segments = Mathf.Clamp(horizontalSegments, 1, 128);
        float width = rectTransform.rect.width;
        float height = rectTransform.rect.height;
        float halfWidth = width * 0.5f;
        float halfHeight = height * 0.5f;
        float sign = invert ? -1f : 1f;
        float safeRadius = Mathf.Max(0.01f, radius);

        int vertCount = (segments + 1) * 2;
        Vector3[] vertices = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];
        int[] triangles = new int[segments * 6];

        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            float x = Mathf.Lerp(-halfWidth, halfWidth, t);
            float angle = x / safeRadius;
            float curvedX = Mathf.Sin(angle) * safeRadius;
            float curvedZ = (safeRadius * (1f - Mathf.Cos(angle))) * sign;

            int idx = i * 2;
            vertices[idx] = new Vector3(curvedX, -halfHeight, curvedZ);
            vertices[idx + 1] = new Vector3(curvedX, halfHeight, curvedZ);

            uvs[idx] = new Vector2(t, 0f);
            uvs[idx + 1] = new Vector2(t, 1f);

            if (i < segments)
            {
                int tri = i * 6;
                triangles[tri] = idx;
                triangles[tri + 1] = idx + 1;
                triangles[tri + 2] = idx + 3;
                triangles[tri + 3] = idx;
                triangles[tri + 4] = idx + 3;
                triangles[tri + 5] = idx + 2;
            }
        }

        Mesh mesh = surfaceFilter.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.name = $"{name}_CurvedMesh";
            surfaceFilter.sharedMesh = mesh;
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        surfaceCollider.sharedMesh = null;
        surfaceCollider.sharedMesh = mesh;
    }

    private void EnsureRayInteractorSurface()
    {
        if (!enableRayInteractorSurface || surfaceCollider == null)
        {
            return;
        }

        EnsurePointableCanvas();

        var colliderSurface = surfaceCollider.GetComponent<ColliderSurface>();
        if (colliderSurface == null)
        {
            colliderSurface = surfaceCollider.gameObject.AddComponent<ColliderSurface>();
        }
        colliderSurface.InjectCollider(surfaceCollider);

        var pointableBridge = surfaceCollider.GetComponent<CurvedCanvasPointableElement>();
        if (pointableBridge == null)
        {
            pointableBridge = surfaceCollider.gameObject.AddComponent<CurvedCanvasPointableElement>();
        }
        pointableBridge.Configure(surfaceCollider.transform, targetCanvas.transform, radius, invert, pointableCanvas);

        var rayInteractable = surfaceCollider.GetComponent<RayInteractable>();
        if (rayInteractable == null)
        {
            rayInteractable = surfaceCollider.gameObject.AddComponent<RayInteractable>();
        }
        rayInteractable.InjectSurface(colliderSurface);
        rayInteractable.InjectOptionalSelectSurface(colliderSurface);
        rayInteractable.InjectOptionalPointableElement(pointableBridge);
    }

    private void EnsurePointableCanvas()
    {
        if (!enableRayInteractorSurface || targetCanvas == null)
        {
            return;
        }

        if (pointableCanvas == null)
        {
            pointableCanvas = targetCanvas.GetComponent<PointableCanvas>();
        }

        if (pointableCanvas == null)
        {
            pointableCanvas = targetCanvas.gameObject.AddComponent<PointableCanvas>();
        }

        pointableCanvas.InjectCanvas(targetCanvas);
    }

    private void UpdateCameraFraming(int width, int height, RectTransform rectTransform)
    {
        if (uiCamera == null || height <= 0)
        {
            return;
        }
        float worldHeight = height;
        float worldWidth = width;
        if (rectTransform != null)
        {
            Vector3 scale = rectTransform.lossyScale;
            worldHeight = Mathf.Abs(height * scale.y);
            worldWidth = Mathf.Abs(width * scale.x);
        }

        if (worldHeight <= 0f)
        {
            worldHeight = height;
        }
        if (worldWidth <= 0f)
        {
            worldWidth = width;
        }

        uiCamera.orthographicSize = worldHeight * 0.5f;
        uiCamera.aspect = worldWidth > 0f ? worldWidth / worldHeight : 1f;
    }

    private void DisableChildCurveEffects()
    {
        if (!disableChildCurveEffects)
        {
            return;
        }

        disabledCurveEffects.Clear();
        var effects = GetComponentsInChildren<UICurveMeshEffect>(includeInactive);
        foreach (var effect in effects)
        {
            if (effect != null && effect.enabled)
            {
                effect.enabled = false;
                disabledCurveEffects.Add(effect);
            }
        }
    }

    private void RestoreChildCurveEffects()
    {
        for (int i = 0; i < disabledCurveEffects.Count; i++)
        {
            var effect = disabledCurveEffects[i];
            if (effect != null)
            {
                effect.enabled = true;
            }
        }
        disabledCurveEffects.Clear();
    }
}
#endif