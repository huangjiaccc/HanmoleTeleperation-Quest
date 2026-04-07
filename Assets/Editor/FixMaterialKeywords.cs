// FixMaterialKeywords.cs
using UnityEditor;
using UnityEngine;
using Debug = AppLog;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.Rendering;

public class FixMaterialKeywords : EditorWindow
{
    [MenuItem("Tools/Fix Material Keywords")]
    public static void ShowWindow()
    {
        GetWindow<FixMaterialKeywords>("材质关键字修复");
    }

    private static bool fixAllMaterials = true;
    private static bool backupBeforeFix = true;
    private static string searchFilter = "t:Material";

    void OnGUI()
    {
        GUILayout.Label("材质关键字修复工具", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("修复从内置渲染管线切换到URP导致的材质关键字冲突", MessageType.Info);

        backupBeforeFix = EditorGUILayout.Toggle("修复前备份", backupBeforeFix);
        fixAllMaterials = EditorGUILayout.Toggle("修复所有材质", fixAllMaterials);

        if (!fixAllMaterials)
        {
            searchFilter = EditorGUILayout.TextField("搜索筛选", searchFilter);
        }

        if (GUILayout.Button("扫描并修复材质", GUILayout.Height(40)))
        {
            ScanAndFixMaterials();
        }

        if (GUILayout.Button("仅修复选中材质", GUILayout.Height(30)))
        {
            FixSelectedMaterials();
        }

        if (GUILayout.Button("重置URP材质关键字", GUILayout.Height(30)))
        {
            ResetURPMaterialKeywords();
        }

        if (GUILayout.Button("检查内置管线材质", GUILayout.Height(30)))
        {
            CheckBuiltInMaterials();
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "常见问题：\n" +
            "1. 材质使用了错误的着色器\n" +
            "2. 关键字状态损坏\n" +
            "3. 渲染管线设置不正确",
            MessageType.Warning);
    }

    static void ScanAndFixMaterials()
    {
        string[] materialGuids = AssetDatabase.FindAssets(fixAllMaterials ? "t:Material" : searchFilter);

        int fixedCount = 0;
        int errorCount = 0;

        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material != null)
            {
                try
                {
                    if (FixMaterial(material, path))
                    {
                        fixedCount++;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"修复材质失败: {path}\n{e.Message}");
                    errorCount++;
                }
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("修复完成",
            $"修复完成！\n修复: {fixedCount} 个材质\n失败: {errorCount} 个",
            "确定");
    }

    static bool FixMaterial(Material material, string path)
    {
        string shaderName = material.shader.name;

        // 检查是否是内置管线材质
        if (shaderName.Contains("Standard") ||
            shaderName.Contains("Legacy Shaders") ||
            shaderName.Contains("Meta/"))
        {
            Debug.LogWarning($"发现内置管线材质: {path} ({shaderName})");

            // 备份原始材质
            if (backupBeforeFix)
            {
                string backupPath = path.Replace(".mat", "_Backup.mat");
                AssetDatabase.CopyAsset(path, backupPath);
            }

            // 方案A：转换为URP材质
            if (TryConvertToURP(material, path))
            {
                Debug.Log($"已转换为URP材质: {path}");
                return true;
            }

            // 方案B：重置关键字
            ResetMaterialKeywords(material);

            EditorUtility.SetDirty(material);
            return true;
        }
        else if (shaderName.Contains("Universal Render Pipeline"))
        {
            // URP材质，但可能有损坏的关键字
            ResetMaterialKeywords(material);
            EditorUtility.SetDirty(material);
            return true;
        }

        return false;
    }

    static bool TryConvertToURP(Material material, string path)
    {
        // 常见的着色器映射
        Dictionary<string, string> shaderMappings = new Dictionary<string, string>()
        {
            // 内置Standard -> URP Lit
            { "Standard", "Universal Render Pipeline/Lit" },
            { "Standard (Specular setup)", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Diffuse", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Bumped Diffuse", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Specular", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Bumped Specular", "Universal Render Pipeline/Lit" },
            { "Meta/Lit", "Universal Render Pipeline/Lit" },
            
            // 透明材质
            { "Standard (Transparent)", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Transparent/Diffuse", "Universal Render Pipeline/Simple Lit" },
            { "Legacy Shaders/Transparent/Cutout/Diffuse", "Universal Render Pipeline/Lit" },
            
            // 自发光
            { "Standard (Emission)", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Self-Illumin/Diffuse", "Universal Render Pipeline/Lit" },
        };

        string currentShader = material.shader.name;

        if (shaderMappings.ContainsKey(currentShader))
        {
            Shader urpShader = Shader.Find(shaderMappings[currentShader]);
            if (urpShader != null)
            {
                material.shader = urpShader;

                // 转移常见属性
                TransferMaterialProperties(material, currentShader, urpShader.name);

                // 重置关键字
                ResetMaterialKeywords(material);

                return true;
            }
        }

        return false;
    }

    static void TransferMaterialProperties(Material material, string oldShader, string newShader)
    {
        // 保存原始属性值
        Color mainColor = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
        Texture mainTex = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        float smoothness = material.HasProperty("_Glossiness") ? material.GetFloat("_Glossiness") : 0.5f;
        float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0.0f;

        // 设置到URP属性
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", mainColor);
        else if (material.HasProperty("_BaseMap"))
            material.SetColor("_BaseMap", mainColor);

        if (material.HasProperty("_BaseMap") && mainTex != null)
            material.SetTexture("_BaseMap", mainTex);

        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", smoothness);

        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", metallic);
    }

    static void ResetMaterialKeywords(Material material)
    {
        // 清除所有关键字
        material.shaderKeywords = new string[0];

        // 禁用所有局部关键字
#if UNITY_2021_2_OR_NEWER
        var localKeywords = material.enabledKeywords;
        foreach (var keyword in localKeywords)
        {
            material.DisableKeyword(keyword);
        }
#endif

        // 强制重新编译材质
        EditorUtility.SetDirty(material);
    }

    static void FixSelectedMaterials()
    {
        Material[] materials = Selection.GetFiltered<Material>(SelectionMode.Assets);

        foreach (Material material in materials)
        {
            string path = AssetDatabase.GetAssetPath(material);
            FixMaterial(material, path);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"修复了 {materials.Length} 个选中材质");
    }

    static void ResetURPMaterialKeywords()
    {
        // 重置所有URP材质的关键字
        string[] materialGuids = AssetDatabase.FindAssets("t:Material");

        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material != null && material.shader.name.Contains("Universal Render Pipeline"))
            {
                ResetMaterialKeywords(material);
                EditorUtility.SetDirty(material);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log("已重置所有URP材质关键字");
    }

    static void CheckBuiltInMaterials()
    {
        List<string> builtInMaterials = new List<string>();

        string[] materialGuids = AssetDatabase.FindAssets("t:Material");

        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material != null &&
                (material.shader.name.Contains("Standard") ||
                 material.shader.name.Contains("Legacy") ||
                 material.shader.name.Contains("Meta/")))
            {
                builtInMaterials.Add($"{path} ({material.shader.name})");
            }
        }

        if (builtInMaterials.Count > 0)
        {
            Debug.LogWarning($"发现 {builtInMaterials.Count} 个内置管线材质：");
            foreach (string mat in builtInMaterials)
            {
                Debug.LogWarning($"  {mat}");
            }
        }
        else
        {
            Debug.Log("未发现内置管线材质");
        }
    }
}