using UnityEditor;
using UnityEngine;

namespace Quest3VideoPlayer.Editor
{
    [CustomEditor(typeof(VideoColorCalibrator))]
    internal sealed class VideoColorCalibratorEditor : UnityEditor.Editor
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

            var mode = (VideoColorCalibrator.ConfigMode)configModeProp.enumValueIndex;
            if (mode == VideoColorCalibrator.ConfigMode.ProductionFixed)
            {
                EditorGUILayout.HelpBox(
                    "ProductionFixed：运行时使用代码里的固定值（忽略 Inspector 里的调试参数）。\n" +
                    "DebugInspector：运行时使用 Inspector 的可调参数，用于排查颜色问题。",
                    MessageType.Info);

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LabelField("Fixed Defaults (from code)", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Force Fixed Manual YUV Params", "True");
                    EditorGUILayout.LabelField("Auto Calibrate GPU YUV", "False");
                    EditorGUILayout.LabelField("Unity Video Texture Is Linear", "True");
                }
            }
            else
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("DebugInspector (editable)", EditorStyles.boldLabel);
                DrawPropertiesExcluding(serializedObject, "m_Script", "configMode", "targetRenderer", "targetUI", "targetUI2");
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}

