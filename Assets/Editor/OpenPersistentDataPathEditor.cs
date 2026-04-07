using UnityEngine;
using Debug = AppLog;
using UnityEditor;
using System.Diagnostics; // 用于 Process

public class OpenPersistentDataPathEditor : EditorWindow
{
    [MenuItem("Tools/Open PersistentDataPath")]
    public static void OpenPersistentDataPath()
    {
        string path = Application.persistentDataPath;
        Debug.Log("Opening: " + path);

    // 根据不同平台用不同方式打开
    #if UNITY_EDITOR_WIN
        Process.Start("explorer.exe", path.Replace("/", "\\"));
#elif UNITY_EDITOR_OSX
        Process.Start("open", path);
#elif UNITY_EDITOR_LINUX
        Process.Start("xdg-open", path);
#endif
    }

}

