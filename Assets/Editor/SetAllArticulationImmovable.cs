using UnityEngine;
using Debug = AppLog;
using UnityEditor;

public class SetAllArticulationImmovable : MonoBehaviour
{
    [ContextMenu("Set All Articulation Bodies Immovable = false")]
    public void SetImmovableFalse()
    {
        var bodies = GetComponentsInChildren<ArticulationBody>(true);
        int count = 0;

        foreach (var ab in bodies)
        {
            if (ab.immovable)
            {
                ab.immovable = false;
                count++;
            }
        }

        Debug.Log($"已设置 {count} 个 ArticulationBody 的 Immovable = false");
    }
}

#if UNITY_EDITOR
// 添加一个 Editor 按钮
[CustomEditor(typeof(SetAllArticulationImmovable))]
public class SetAllArticulationImmovableEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        if (GUILayout.Button("一键 Immovable = false"))
        {
            (target as SetAllArticulationImmovable).SetImmovableFalse();
        }
    }
}
#endif
