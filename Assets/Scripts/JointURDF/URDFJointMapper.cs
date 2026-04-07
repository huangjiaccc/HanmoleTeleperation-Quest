using System.Collections.Generic;
using UnityEngine;
using Debug = AppLog;

public class URDFJointMapper : MonoBehaviour
{
    public Dictionary<string, ArticulationBody> jointMap;

    string[] jointNames = new string[]
    {
        // leg
        "waist_yaw",
        "waist_pitch",
        "knee",
        "ankle",

        // left arm
        "left_shoulder_inner",
        "left_shoulder_outer",
        "left_upper_arm",
        "left_elbow",
        "left_forearm",
        "left_wrist_upper",
        "left_wrist_lower",

        // right arm
        "right_shoulder_inner",
        "right_shoulder_outer",
        "right_upper_arm",
        "right_elbow",
        "right_forearm",
        "right_wrist_upper",
        "right_wrist_lower",

        // neck
        "neck_yaw",
        "neck_pitch"
    };

    void Awake()
    {
        jointMap = new Dictionary<string, ArticulationBody>();

        // 自动查找
        var all = GetComponentsInChildren<ArticulationBody>(true);

        foreach (var ab in all)
        {
            string name = ab.gameObject.name;

            foreach (var j in jointNames)
            {
                if (name == j)
                {
                    jointMap[j] = ab;
                    break;
                }
            }
        }

        Debug.Log("关节映射完成，共找到：" + jointMap.Count + " / 20");
    }
}
