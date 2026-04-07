using UnityEditor;
using UnityEngine;
using Debug = AppLog;
using System.IO;

public class PluginPlatformFixer : EditorWindow
{
    [MenuItem("Tools/Plugin Fixer/Auto Fix Plugin Platform Settings")]
    public static void FixPluginPlatforms()
    {
        int fixedCount = 0;
        int skippedCount = 0;

        // 搜索所有 dll 文件
        string[] dllPaths = Directory.GetFiles(Application.dataPath, "*.dll", SearchOption.AllDirectories);

        foreach (string dllPath in dllPaths)
        {
            string relativePath = "Assets" + dllPath.Replace(Application.dataPath, "").Replace("\\", "/");
            PluginImporter plugin = AssetImporter.GetAtPath(relativePath) as PluginImporter;

            if (plugin == null)
            {
                skippedCount++;
                continue;
            }

            // 只处理 Windows 插件
            if (dllPath.Contains("/win/") || dllPath.Contains("\\win\\"))
            {
                bool changed = false;

                // 禁用在 Android 平台的使用
                if (plugin.GetCompatibleWithPlatform(BuildTarget.Android))
                {
                    plugin.SetCompatibleWithPlatform(BuildTarget.Android, false);
                    changed = true;
                }

                // 确保只在 Editor（Windows）可用
                if (!plugin.GetCompatibleWithEditor())
                {
                    plugin.SetCompatibleWithEditor(true);
                    changed = true;
                }

                // 保存修改
                if (changed)
                {
                    plugin.SaveAndReimport();
                    fixedCount++;
                    Debug.Log($"[PluginFixer] Fixed: {relativePath}");
                }
            }
            else
            {
                skippedCount++;
            }
        }

        Debug.Log($" Plugin platform fix complete! Fixed: {fixedCount}, Skipped: {skippedCount}");
    }
}
