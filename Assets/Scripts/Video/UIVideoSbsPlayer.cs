/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using UnityEngine;
using UnityEngine.UI;

public class UIVideoSbsPlayer : MonoBehaviour
{
    public static UIVideoSbsPlayer instance;
    public enum StereoLayout
    {
        LeftRight,
        Mono
    }

    [Header("Static Texture")]
    public Texture StaticTexture;

    [Header("Target Renderer")]
    public Renderer TargetRenderer;
    public Renderer TargetRenderer2;
    [Tooltip("Optional texture property name for the renderer material. Leave empty to use mainTexture.")]
    public string TargetTextureProperty = "_MainTex";
    [Tooltip("Apply stereo rect/flip material properties to the renderer if they exist.")]
    public bool ApplyStereoToRenderer = true;

    [Header("Stereo")]
    public StereoLayout Layout = StereoLayout.LeftRight;
    [Tooltip("Skip center pixels to avoid visible seam when the source width/height is odd.")]
    [Range(0, 4)]
    public int CenterGapPixels = 0;

    [Header("Flip")]
    public bool FlipTextureHorizontally = true;
    public bool FlipTextureVertically = false;

    private Rect _srcRectLeft = new Rect(0f, 0f, 0.5f, 1f);
    private Rect _srcRectRight = new Rect(0.5f, 0f, 0.5f, 1f);
    private int _lastTexWidth = -1;
    private int _lastTexHeight = -1;
    private int _lastGapPixels = -1;
    private StereoLayout _lastLayout;
    private bool _lastFlipX;
    private bool _lastFlipY = true;
    private Texture _lastAppliedDisplayTexture;
    private Texture _lastAppliedRendererTexture;
    private MaterialPropertyBlock _targetRendererPropertyBlock;
    private MaterialPropertyBlock _targetRenderer2PropertyBlock;

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int SrcRectLeftId = Shader.PropertyToID("_SrcRectLeft");
    private static readonly int SrcRectRightId = Shader.PropertyToID("_SrcRectRight");
    private static readonly int FlipXId = Shader.PropertyToID("_FlipX");
    private static readonly int FlipYId = Shader.PropertyToID("_FlipY");
    private Material _targetRawImageRuntimeMaterial;
    private Material _leftRawImageRuntimeMaterial;
    private Material _rightRawImageRuntimeMaterial;

    private void Awake()
    {
        instance = this;
        EnsureRendererPropertyBlocks();

        UpdateStereoRects(GetStereoSourceTexture());
        ApplyMaterialRects();
        _lastFlipX = FlipTextureHorizontally;
        _lastFlipY = FlipTextureVertically;
        _lastAppliedDisplayTexture = StaticTexture;
    }

    private void Update()
    {
        Texture displayTexture = StaticTexture;
        bool textureChanged = displayTexture != _lastAppliedDisplayTexture;
        bool rectChanged = UpdateStereoRects(GetStereoSourceTexture());
        bool flipChanged = FlipTextureHorizontally != _lastFlipX || FlipTextureVertically != _lastFlipY;

        if (rectChanged || flipChanged || textureChanged)
        {
            ApplyMaterialRects();
            _lastFlipX = FlipTextureHorizontally;
            _lastFlipY = FlipTextureVertically;
        }

        if (displayTexture != null && textureChanged)
        {
            ApplyRendererTexture(displayTexture);
        }

        _lastAppliedDisplayTexture = displayTexture;
    }

    private void OnValidate()
    {
        UpdateStereoRects(GetStereoSourceTexture());
        ApplyMaterialRects();

        if (StaticTexture != null)
        {
            ApplyRendererTexture(StaticTexture);
        }
    }

    private void OnDestroy()
    {
        DestroyRuntimeMaterial(ref _targetRawImageRuntimeMaterial);
        DestroyRuntimeMaterial(ref _leftRawImageRuntimeMaterial);
        DestroyRuntimeMaterial(ref _rightRawImageRuntimeMaterial);
        if (instance == this)
        {
            instance = null;
        }
    }

    private bool UpdateStereoRects(Texture sourceTexture)
    {
        int texWidth = sourceTexture != null ? sourceTexture.width : -1;
        int texHeight = sourceTexture != null ? sourceTexture.height : -1;
        if (texWidth == _lastTexWidth &&
            texHeight == _lastTexHeight &&
            CenterGapPixels == _lastGapPixels &&
            Layout == _lastLayout)
        {
            return false;
        }

        _lastTexWidth = texWidth;
        _lastTexHeight = texHeight;
        _lastGapPixels = CenterGapPixels;
        _lastLayout = Layout;

        switch (Layout)
        {
            case StereoLayout.LeftRight:
                if (texWidth > 0)
                {
                    float gap = Mathf.Max(0, CenterGapPixels);
                    float half = texWidth * 0.5f;
                    float leftWidthPx = Mathf.Max(1f, Mathf.Floor(half) - gap);
                    float rightStartPx = Mathf.Min(texWidth - 1f, Mathf.Ceil(half) + gap);
                    float rightWidthPx = Mathf.Max(1f, texWidth - rightStartPx);
                    _srcRectLeft = new Rect(0f, 0f, leftWidthPx / texWidth, 1f);
                    _srcRectRight = new Rect(rightStartPx / texWidth, 0f, rightWidthPx / texWidth, 1f);
                }
                else
                {
                    _srcRectLeft = new Rect(0f, 0f, 0.5f, 1f);
                    _srcRectRight = new Rect(0.5f, 0f, 0.5f, 1f);
                }
                break;
            case StereoLayout.Mono:
            default:
                _srcRectLeft = new Rect(0f, 0f, 1f, 1f);
                _srcRectRight = new Rect(0f, 0f, 1f, 1f);
                break;
        }

        return true;
    }

    private Texture GetStereoSourceTexture()
    {
        if (StaticTexture != null)
        {
            return StaticTexture;
        }

        if (_lastAppliedRendererTexture != null)
        {
            return _lastAppliedRendererTexture;
        }

        if (TargetRenderer == null)
        {
            return null;
        }

        Material rendererMaterial = GetTargetRendererMaterial();
        if (rendererMaterial == null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(TargetTextureProperty) && rendererMaterial.HasProperty(TargetTextureProperty))
        {
            return rendererMaterial.GetTexture(TargetTextureProperty);
        }

        return rendererMaterial.mainTexture;

    }

    private void ApplyMaterialRects()
    {
        GetEffectiveSourceRects(out Rect leftRect, out Rect rightRect);
        GetEffectiveFlipFlags(out bool effectiveFlipX, out bool effectiveFlipY);
        ApplyRendererStereoProperties();
    }

    private void ApplyRendererTexture(Texture displayTexture)
    {
        _lastAppliedRendererTexture = displayTexture;

        if (TargetRenderer == null)
        {
            return;
        }

        Material rendererMaterial = GetTargetRendererMaterial();
        if (rendererMaterial == null)
        {
            return;
        }

        EnsureRendererPropertyBlocks();
        ApplyRendererTextureProperty(TargetRenderer, rendererMaterial, _targetRendererPropertyBlock, displayTexture);

        if (TargetRenderer2 == null)
        {
            return;
        }

        Material rendererMaterial2 = GetTargetRenderer2Material();
        if (rendererMaterial2 == null)
        {
            return;
        }

        ApplyRendererTextureProperty(TargetRenderer2, rendererMaterial2, _targetRenderer2PropertyBlock, displayTexture);
    }

    private void ApplyRendererStereoProperties()
    {
        if (!ApplyStereoToRenderer || TargetRenderer == null)
        {
            return;
        }

        Material rendererMaterial = GetTargetRendererMaterial();
        if (rendererMaterial == null)
        {
            return;
        }

        EnsureRendererPropertyBlocks();
        ApplyRendererStereoPropertyBlock(TargetRenderer, rendererMaterial, _targetRendererPropertyBlock);

        Material rendererMaterial2 = GetTargetRenderer2Material();
        if (rendererMaterial2 == null)
        {
            return;
        }

        ApplyRendererStereoPropertyBlock(TargetRenderer2, rendererMaterial2, _targetRenderer2PropertyBlock);
    }

    private Material GetTargetRendererMaterial()
    {
        if (TargetRenderer == null)
        {
            return null;
        }

        return TargetRenderer.sharedMaterial;
    }

    private Material GetTargetRenderer2Material()
    {
        if (TargetRenderer2 == null)
        {
            return null;
        }

        return TargetRenderer2.sharedMaterial;
    }

    private void EnsureRendererPropertyBlocks()
    {
        _targetRendererPropertyBlock ??= new MaterialPropertyBlock();
        _targetRenderer2PropertyBlock ??= new MaterialPropertyBlock();
    }

    private void ApplyRendererTextureProperty(Renderer renderer, Material material, MaterialPropertyBlock propertyBlock, Texture displayTexture)
    {
        if (renderer == null || material == null || propertyBlock == null)
        {
            return;
        }

        renderer.GetPropertyBlock(propertyBlock);
        if (!string.IsNullOrEmpty(TargetTextureProperty) && material.HasProperty(TargetTextureProperty))
        {
            propertyBlock.SetTexture(TargetTextureProperty, displayTexture);
        }
        else
        {
            propertyBlock.SetTexture(MainTexId, displayTexture);
        }
        renderer.SetPropertyBlock(propertyBlock);
    }

    private void ApplyRendererStereoPropertyBlock(Renderer renderer, Material material, MaterialPropertyBlock propertyBlock)
    {
        if (renderer == null || material == null || propertyBlock == null)
        {
            return;
        }

        GetEffectiveSourceRects(out Rect leftRect, out Rect rightRect);
        GetEffectiveFlipFlags(out bool effectiveFlipX, out bool effectiveFlipY);

        renderer.GetPropertyBlock(propertyBlock);

        if (material.HasProperty(SrcRectLeftId))
        {
            propertyBlock.SetVector(SrcRectLeftId, RectToVector(leftRect));
        }

        if (material.HasProperty(SrcRectRightId))
        {
            propertyBlock.SetVector(SrcRectRightId, RectToVector(rightRect));
        }

        if (material.HasProperty(FlipXId))
        {
            propertyBlock.SetFloat(FlipXId, effectiveFlipX ? 1f : 0f);
        }

        if (material.HasProperty(FlipYId))
        {
            propertyBlock.SetFloat(FlipYId, effectiveFlipY ? 1f : 0f);
        }

        renderer.SetPropertyBlock(propertyBlock);
    }

    private void GetEffectiveSourceRects(out Rect leftRect, out Rect rightRect)
    {
        leftRect = _srcRectLeft;
        rightRect = _srcRectRight;

        // For stereo split content, users typically want left/right eye exchange rather than
        // mirroring each eye image independently.
        if (Layout == StereoLayout.LeftRight && FlipTextureHorizontally)
        {
            (leftRect, rightRect) = (rightRect, leftRect);
        }
    }

    private void GetEffectiveFlipFlags(out bool flipX, out bool flipY)
    {
        flipX = FlipTextureHorizontally;
        flipY = FlipTextureVertically;

        if (Layout == StereoLayout.LeftRight && FlipTextureHorizontally)
        {
            flipX = false;
        }
    }

    private static Vector4 RectToVector(Rect rect)
    {
        return new Vector4(rect.x, rect.y, rect.width, rect.height);
    }

    private void EnsureRawImageMaterialSupportsStereoProps(RawImage rawImage, ref Material runtimeMaterial)
    {
        if (rawImage == null)
        {
            DestroyRuntimeMaterial(ref runtimeMaterial);
            return;
        }

        Material currentMaterial = rawImage.material;
        if (MaterialSupportsStereoFlip(currentMaterial))
        {
            if (runtimeMaterial != null && currentMaterial == runtimeMaterial)
            {
                return;
            }

            DestroyRuntimeMaterial(ref runtimeMaterial);
            return;
        }

        if (runtimeMaterial == null)
        {
            Shader shader = Shader.Find("Unlit/Multiview Stereo");
            if (shader == null)
            {
                return;
            }

            runtimeMaterial = new Material(shader)
            {
                name = rawImage.name + "_StereoFlipRuntime"
            };
        }

        rawImage.material = runtimeMaterial;
    }

    private static bool MaterialSupportsStereoFlip(Material material)
    {
        return material != null &&
               material.HasProperty(SrcRectLeftId) &&
               material.HasProperty(SrcRectRightId) &&
               material.HasProperty(FlipXId) &&
               material.HasProperty(FlipYId);
    }

    private static void DestroyRuntimeMaterial(ref Material material)
    {
        if (material == null)
        {
            return;
        }

        Object.Destroy(material);
        material = null;
    }
}
