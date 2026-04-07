// ImprovedMaterialFixer.cs
using UnityEditor;
using UnityEngine;
using Debug = AppLog;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.Rendering;
using UnityEditor.PackageManager;

public class ImprovedMaterialFixer : EditorWindow
{
    private Vector2 scrollPos;
    private List<string> problemAssets = new List<string>();
    private List<string> fixedAssets = new List<string>();
    private bool includePackages = false;

    [MenuItem("Tools/高级材质修复器")]
    public static void ShowWindow()
    {
        GetWindow<ImprovedMaterialFixer>("高级材质修复器");
    }

    void OnGUI()
    {
        GUILayout.Label("材质关键字修复工具 v2.0", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("智能修复材质关键字冲突问题", MessageType.Info);

        includePackages = EditorGUILayout.Toggle("包含Packages文件夹", includePackages);

        if (GUILayout.Button("扫描项目材质", GUILayout.Height(40)))
        {
            ScanAllMaterials();
        }

        if (GUILayout.Button("智能修复所有材质", GUILayout.Height(40)))
        {
            SmartFixAllMaterials();
        }

        if (GUILayout.Button("仅修复URP材质", GUILayout.Height(30)))
        {
            FixOnlyURPMaterials();
        }

        EditorGUILayout.Space();

        // 显示结果
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(200));

        if (problemAssets.Count > 0)
        {
            GUILayout.Label($"发现 {problemAssets.Count} 个问题材质：", EditorStyles.boldLabel);
            foreach (var asset in problemAssets)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(asset, GUILayout.Width(400));
                if (GUILayout.Button("修复", GUILayout.Width(60)))
                {
                    FixSingleMaterial(asset);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        if (fixedAssets.Count > 0)
        {
            GUILayout.Label($"已修复 {fixedAssets.Count} 个材质：", EditorStyles.boldLabel);
            foreach (var asset in fixedAssets)
            {
                EditorGUILayout.LabelField(asset);
            }
        }

        EditorGUILayout.EndScrollView();

        if (GUILayout.Button("清除缓存并重载", GUILayout.Height(30)))
        {
            ClearCacheAndReload();
        }
    }

    void ScanAllMaterials()
    {
        problemAssets.Clear();
        fixedAssets.Clear();

        // 查找所有材质文件
        string[] allMaterials = AssetDatabase.FindAssets("t:Material")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .Where(path => includePackages || !path.StartsWith("Packages/"))
            .ToArray();

        Debug.Log($"开始扫描 {allMaterials.Length} 个材质文件...");

        int problemCount = 0;

        foreach (string path in allMaterials)
        {
            // 跳过特殊文件
            if (ShouldSkipFile(path))
                continue;

            try
            {
                // 尝试加载材质
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (material == null)
                {
                    // 可能是特殊文件或损坏文件
                    if (IsProbablyProblemMaterial(path))
                    {
                        problemAssets.Add(path);
                        problemCount++;
                    }
                    continue;
                }

                // 检查是否是内置管线材质
                if (IsBuiltInMaterial(material))
                {
                    problemAssets.Add($"[Built-in] {path}");
                    problemCount++;
                }
                // 检查是否有损坏的关键字
                else if (HasCorruptedKeywords(material))
                {
                    problemAssets.Add($"[Corrupted] {path}");
                    problemCount++;
                }
            }
            catch (System.Exception e)
            {
                // 捕获加载失败的文件
                problemAssets.Add($"[Error] {path} - {e.Message}");
                problemCount++;
            }
        }

        Debug.Log($"扫描完成！发现 {problemCount} 个问题材质");
    }

    bool ShouldSkipFile(string path)
    {
        // 跳过特殊文件
        string fileName = Path.GetFileName(path);
        string lowerPath = path.ToLower();

        // 跳过ShaderGraph文件（它们有.mat扩展名但实际上是shader）
        if (path.Contains(".shadergraph"))
            return true;

        // 跳过Arnold相关文件
        if (path.Contains("ArnoldStandardSurface"))
            return true;

        // 跳过Unity内置的测试/示例材质
        if (path.Contains("Test") || path.Contains("Example") || path.Contains("Sample"))
            return true;

        // 跳过Editor文件夹中的材质
        if (path.Contains("/Editor/") || path.Contains("/Editor Default Resources/"))
            return true;

        return false;
    }

    bool IsProbablyProblemMaterial(string path)
    {
        // 通过文件路径判断可能是问题材质
        if (path.Contains("Standard") || path.Contains("Legacy") || path.Contains("Meta/"))
            return true;

        // 检查文件大小（空文件或损坏文件）
        try
        {
            FileInfo fileInfo = new FileInfo(path);
            if (fileInfo.Length < 100) // 小于100字节可能是损坏文件
                return true;
        }
        catch
        {
            return true;
        }

        return false;
    }

    bool IsBuiltInMaterial(Material material)
    {
        string shaderName = material.shader.name;

        return shaderName.Contains("Standard") ||
               shaderName.Contains("Legacy Shaders") ||
               shaderName.Contains("Meta/") ||
               shaderName.Contains("Internal-");
    }

    bool HasCorruptedKeywords(Material material)
    {
        try
        {
            // 尝试访问关键字属性，如果抛出异常则说明损坏
            var keywords = material.shaderKeywords;
            return false;
        }
        catch
        {
            return true;
        }
    }

    void SmartFixAllMaterials()
    {
        if (problemAssets.Count == 0)
        {
            ScanAllMaterials();
        }

        int fixedCount = 0;
        int skippedCount = 0;

        foreach (string assetInfo in problemAssets.ToArray())
        {
            string path = ExtractPathFromAssetInfo(assetInfo);

            if (ShouldSkipFile(path))
            {
                skippedCount++;
                continue;
            }

            try
            {
                if (FixMaterialSafely(path))
                {
                    fixedAssets.Add(path);
                    problemAssets.Remove(assetInfo);
                    fixedCount++;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"修复失败 {path}: {e.Message}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"修复完成！修复: {fixedCount}, 跳过: {skippedCount}");
    }

    bool FixMaterialSafely(string path)
    {
        // 方法1：尝试直接加载
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (material == null)
        {
            // 方法2：使用AssetDatabase API重新导入
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                // 方法3：文件可能损坏，尝试重建
                return RebuildMaterialFile(path);
            }
        }

        // 重置关键字
        material.shaderKeywords = null;

        // 如果是内置材质，尝试转换为URP
        if (IsBuiltInMaterial(material))
        {
            ConvertToURP(material);
        }

        // 保存
        EditorUtility.SetDirty(material);
        return true;
    }

    bool RebuildMaterialFile(string path)
    {
        Debug.LogWarning($"尝试重建材质文件: {path}");

        // 创建新的URP材质
        Shader urpShader = Shader.Find("Universal Render Pipeline/Lit");
        if (urpShader == null)
            urpShader = Shader.Find("Standard");

        Material newMaterial = new Material(urpShader);
        newMaterial.name = Path.GetFileNameWithoutExtension(path);

        // 删除旧文件
        if (File.Exists(path))
        {
            AssetDatabase.DeleteAsset(path);
        }

        // 保存新文件
        AssetDatabase.CreateAsset(newMaterial, path);

        return true;
    }

    void ConvertToURP(Material material)
    {
        string oldShaderName = material.shader.name;

        // 查找对应的URP着色器
        Shader urpShader = FindURPShader(oldShaderName);

        if (urpShader != null && urpShader != material.shader)
        {
            // 保存原始属性
            var properties = SaveMaterialProperties(material);

            // 更换着色器
            material.shader = urpShader;

            // 恢复属性
            RestoreMaterialProperties(material, properties, urpShader.name);

            Debug.Log($"已将 {oldShaderName} 转换为 {urpShader.name}");
        }
    }

    Shader FindURPShader(string oldShaderName)
    {
        // 映射表
        if (oldShaderName.Contains("Standard") || oldShaderName.Contains("Meta/Lit"))
            return Shader.Find("Universal Render Pipeline/Lit");

        if (oldShaderName.Contains("Unlit"))
            return Shader.Find("Universal Render Pipeline/Unlit");

        if (oldShaderName.Contains("Particles"))
            return Shader.Find("Universal Render Pipeline/Particles/Lit");

        // 默认返回URP Lit
        return Shader.Find("Universal Render Pipeline/Lit");
    }

    Dictionary<string, object> SaveMaterialProperties(Material material)
    {
        var properties = new Dictionary<string, object>();

        // 保存常见属性
        SavePropertyIfExists(material, "_Color", properties);
        SavePropertyIfExists(material, "_MainTex", properties);
        SavePropertyIfExists(material, "_MainTex_ST", properties);
        SavePropertyIfExists(material, "_Metallic", properties);
        SavePropertyIfExists(material, "_Glossiness", properties);
        SavePropertyIfExists(material, "_BumpMap", properties);
        SavePropertyIfExists(material, "_EmissionColor", properties);
        SavePropertyIfExists(material, "_EmissionMap", properties);

        return properties;
    }

    void SavePropertyIfExists(Material material, string name, Dictionary<string, object> dict)
    {
        if (material.HasProperty(name))
        {
            var property = material.GetType().GetProperty(name);
            if (property != null)
            {
                dict[name] = property.GetValue(material);
            }
            else
            {
                // 根据类型猜测
                if (name.EndsWith("_ST"))
                    dict[name] = material.GetTextureScale(name.Replace("_ST", ""));
                else if (name.Contains("Color"))
                    dict[name] = material.GetColor(name);
                else if (name.Contains("Tex") || name.Contains("Map"))
                    dict[name] = material.GetTexture(name);
                else
                    dict[name] = material.GetFloat(name);
            }
        }
    }

    void RestoreMaterialProperties(Material material, Dictionary<string, object> properties, string newShaderName)
    {
        foreach (var kvp in properties)
        {
            string name = kvp.Key;
            object value = kvp.Value;

            // 将内置属性名映射到URP属性名
            string urpName = MapPropertyName(name, newShaderName);

            try
            {
                if (value is Color colorValue)
                {
                    if (material.HasProperty(urpName))
                        material.SetColor(urpName, colorValue);
                }
                else if (value is Texture textureValue)
                {
                    if (material.HasProperty(urpName))
                        material.SetTexture(urpName, textureValue);
                }
                else if (value is float floatValue)
                {
                    if (material.HasProperty(urpName))
                        material.SetFloat(urpName, floatValue);
                }
                else if (value is Vector4 vectorValue)
                {
                    if (material.HasProperty(urpName))
                        material.SetVector(urpName, vectorValue);
                }
            }
            catch
            {
                // 忽略恢复失败的属性
            }
        }
    }

    string MapPropertyName(string oldName, string newShaderName)
    {
        // 内置 -> URP 属性名映射
        var mapping = new Dictionary<string, string>
        {
            { "_Color", "_BaseColor" },
            { "_MainTex", "_BaseMap" },
            { "_Metallic", "_Metallic" },
            { "_Glossiness", "_Smoothness" },
            { "_BumpMap", "_BumpMap" },
            { "_EmissionColor", "_EmissionColor" },
            { "_EmissionMap", "_EmissionMap" }
        };

        return mapping.ContainsKey(oldName) ? mapping[oldName] : oldName;
    }

    string ExtractPathFromAssetInfo(string assetInfo)
    {
        // 从 [Type] path 格式中提取纯路径
        if (assetInfo.StartsWith("[") && assetInfo.Contains("] "))
        {
            return assetInfo.Substring(assetInfo.IndexOf("] ") + 2);
        }
        return assetInfo;
    }

    void FixOnlyURPMaterials()
    {
        string[] allMaterials = AssetDatabase.FindAssets("t:Material")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .Where(path => !ShouldSkipFile(path))
            .ToArray();

        int fixedCount = 0;

        foreach (string path in allMaterials)
        {
            try
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material != null && material.shader.name.Contains("Universal Render Pipeline"))
                {
                    material.shaderKeywords = null;
                    EditorUtility.SetDirty(material);
                    fixedCount++;
                }
            }
            catch
            {
                // 忽略错误
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"已重置 {fixedCount} 个URP材质的关键字");
    }

    void FixSingleMaterial(string assetInfo)
    {
        string path = ExtractPathFromAssetInfo(assetInfo);

        if (FixMaterialSafely(path))
        {
            fixedAssets.Add(path);
            problemAssets.Remove(assetInfo);
            Debug.Log($"已修复: {path}");
        }
    }

    void ClearCacheAndReload()
    {
        // 清除Library缓存
        if (Directory.Exists("Library"))
        {
            Directory.Delete("Library", true);
        }

        // 清除Temp缓存
        if (Directory.Exists("Temp"))
        {
            Directory.Delete("Temp", true);
        }

        // 重新导入所有材质
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        Debug.Log("缓存已清除，正在重载...");
    }
}