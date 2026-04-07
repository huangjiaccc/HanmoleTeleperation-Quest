using UnityEditor;
using UnityEngine;

namespace Quest3VideoPlayer.Editor
{
    [CustomEditor(typeof(QuestStreamVideoPlayer))]
    internal sealed class QuestStreamVideoPlayerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty configModeProp = serializedObject.FindProperty("configMode");
            SerializedProperty targetRendererProp = serializedObject.FindProperty("targetRenderer");
            SerializedProperty targetUiProp = serializedObject.FindProperty("targetUI");
            SerializedProperty targetUi2Prop = serializedObject.FindProperty("targetUI2");

            EditorGUILayout.PropertyField(configModeProp);
            EditorGUILayout.PropertyField(targetRendererProp);
            EditorGUILayout.PropertyField(targetUiProp);
            EditorGUILayout.PropertyField(targetUi2Prop);

            var mode = (QuestStreamVideoPlayer.ConfigMode)configModeProp.enumValueIndex;
            if (mode == QuestStreamVideoPlayer.ConfigMode.ProductionFixed)
            {
                EditorGUILayout.HelpBox(
                    "ProductionFixed：运行时使用代码里的固定值（忽略 Inspector 里的大多数参数）。\n" +
                    "DebugInspector：运行时使用 Inspector 的可调参数，用于排查问题。",
                    MessageType.Info);

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LabelField("Fixed Defaults (from code)", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Resolution", "2560 x 1440");
                    EditorGUILayout.LabelField("Playback Delay", "0.08");
                    EditorGUILayout.LabelField("Max Buffered Frames", "2");
                    EditorGUILayout.LabelField("Prefer HardwareBuffer Frames", "True");
                    EditorGUILayout.LabelField("Use Java Decoder Color Info", "True");
                    EditorGUILayout.LabelField("Manual YUV Input Mode", "ByteNarrowJava");
                }

                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox("要调整解码/颜色参数，请切到 DebugInspector。", MessageType.None);
            }
            else
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("DebugInspector (editable)", EditorStyles.boldLabel);
                DrawPropertiesExcluding(serializedObject, "m_Script", "configMode", "targetRenderer", "targetUI","targetUI2");
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}

