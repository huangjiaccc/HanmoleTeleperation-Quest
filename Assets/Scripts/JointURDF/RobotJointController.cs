using UnityEngine;
using Debug = AppLog;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;

public class RobotJointController : MonoBehaviour
{
    public static RobotJointController instance;
    [Header("关节映射")]
    public URDFJointMapper mapper;

    [Header("关节值显示")]
    public bool showJointValues = true;
    public Vector2 displayOffset = new Vector2(10, 10);

    [Header("调试设置")]
    public bool logChanges = false;
    public float updateThreshold = 0.001f; // 只更新变化大于此值的关节
    [Header("Drive Fallback")]
    [SerializeField] private float fallbackDriveStiffness = 10000f;
    [SerializeField] private float fallbackDriveDamping = 10000f;
    [SerializeField] private float fallbackDriveForceLimit = 10000f;

    // 当前关节值存储（弧度）
    private float[] currentJoints = new float[20];
    private float[] previousJoints = new float[20];
    private Component urdfControllerComponent;


    float[] zeroJoints = new float[20] {
    0.0f, 0.0f, 0.0f, 0.0f,           // 腰部: yaw, pitch; 膝盖; 脚踝
    0.0f, 0.0f, 0.0f, 0.0f, 0.0f,     // 左臂: 肩内旋, 肩外旋, 上臂, 肘, 前臂
    0.0f, 0.0f,                       // 左臂: 腕上, 腕下
    0.0f, 0.0f, 0.0f, 0.0f, 0.0f,     // 右臂: 肩内旋, 肩外旋, 上臂, 肘, 前臂
    0.0f, 0.0f,                       // 右臂: 腕上, 腕下
    0.0f, 0.0f                        // 颈部: yaw, pitch
};

    float[] tPoseJoints = new float[20] {
    0.0f,      // waist_yaw: 0
    0.0f,      // waist_pitch: 0
    0.0f,      // knee: 0
    0.0f,      // ankle: 0
    
    // 左臂 - 水平展开，稍微向下
    0.0f,      // left_shoulder_inner: 0
    1.57f,     // left_shoulder_outer: 90度 (π/2)
    0.0f,      // left_upper_arm: 0
    0.0f,      // left_elbow: 0
    0.0f,      // left_forearm: 0
    0.0f,      // left_wrist_upper: 0
    0.0f,      // left_wrist_lower: 0
    
    // 右臂 - 水平展开，稍微向下
    0.0f,      // right_shoulder_inner: 0
    -1.57f,    // right_shoulder_outer: -90度 (-π/2)
    0.0f,      // right_upper_arm: 0
    0.0f,      // right_elbow: 0
    0.0f,      // right_forearm: 0
    0.0f,      // right_wrist_upper: 0
    0.0f,      // right_wrist_lower: 0
    
    // 颈部
    0.0f,      // neck_yaw: 0
    0.0f       // neck_pitch: 0
};

    float[] greetingPose = new float[20] {
    0.3f,      // waist_yaw: 17.2度 (轻微右转)
    0.1f,      // waist_pitch: 5.7度 (轻微前倾)
    0.0f,      // knee: 0
    0.0f,      // ankle: 0
    
    // 左臂 - 挥手姿势
    0.0f,      // left_shoulder_inner: 0
    0.8f,      // left_shoulder_outer: 45.8度 (抬起)
    0.5f,      // left_upper_arm: 28.6度 (向前)
    -1.0f,     // left_elbow: -57.3度 (弯曲)
    0.0f,      // left_forearm: 0
    0.2f,      // left_wrist_upper: 11.5度
    -0.2f,     // left_wrist_lower: -11.5度
    
    // 右臂 - 指点姿势
    0.0f,      // right_shoulder_inner: 0
    -0.5f,     // right_shoulder_outer: -28.6度 (稍微放下)
    0.3f,      // right_upper_arm: 17.2度 (向前)
    -1.2f,     // right_elbow: -68.8度 (弯曲)
    0.0f,      // right_forearm: 0
    0.1f,      // right_wrist_upper: 5.7度
    0.1f,      // right_wrist_lower: 5.7度
    
    // 颈部 - 看向左边
    0.5f,      // neck_yaw: 28.6度 (向左看)
    0.1f       // neck_pitch: 5.7度 (轻微向下)
};
    float[] walkingPose = new float[20] {
    0.1f,      // waist_yaw: 5.7度 (轻微转向)
    -0.05f,    // waist_pitch: -2.9度 (轻微后仰)
    0.3f,      // knee: 17.2度 (弯曲)
    0.1f,      // ankle: 5.7度
    
    // 左臂 - 摆动向后
    0.0f,      // left_shoulder_inner: 0
    0.3f,      // left_shoulder_outer: 17.2度 (向后)
    0.2f,      // left_upper_arm: 11.5度
    -0.5f,     // left_elbow: -28.6度 (弯曲)
    0.0f,      // left_forearm: 0
    0.0f,      // left_wrist_upper: 0
    0.0f,      // left_wrist_lower: 0
    
    // 右臂 - 摆动向前
    0.0f,      // right_shoulder_inner: 0
    -0.3f,     // right_shoulder_outer: -17.2度 (向前)
    0.2f,      // right_upper_arm: 11.5度
    -0.5f,     // right_elbow: -28.6度 (弯曲)
    0.0f,      // right_forearm: 0
    0.0f,      // right_wrist_upper: 0
    0.0f,      // right_wrist_lower: 0
    
    // 颈部 - 看向前方
    0.0f,      // neck_yaw: 0
    0.0f       // neck_pitch: 0
};
    float[] DefaultPose = new float[20] {
        -0.00014f,
        0.00045f,
        0.00005f,
        -0.00123f,
        2.54633f,
        -0.71035f,
        0.92892f,
        -2.02177f,
        0.05464f,
        7.96685f,
        -0.03279f,
        -2.67748f,
        1.73763f,
        -2.02177f,
        10.3274f,
        -10.19626f,
        -0.05464f,
        28.86209f,
        0,
        0
};

    float[] TestPose = new float[20] 
    {   1.46f,0.89f,-2.09f,1.05f,
        -1.56f,-0.35f,-0.14f,0,-0.35f,0.43f,0.43f,
        1.57f,-0.35f,-0.14f,0.0f,-0.14f,0.43f,0.43f,
        1.03f,-0.56f
    };

    private void Awake()
    {
        instance = this;
    }

    void Start()
    {
        // 等待一帧确保mapper已初始化
        StartCoroutine(InitializeDelayed());
    }

    System.Collections.IEnumerator InitializeDelayed()
    {
        yield return null;

        if (mapper == null)
            mapper = GetComponent<URDFJointMapper>();
        if (urdfControllerComponent == null)
            urdfControllerComponent = FindUrdfControllerComponent();

        if (mapper != null && mapper.jointMap.Count > 0)
        {
            // 初始化为零位
            ResetToZero();
            Debug.Log("机器人关节控制器初始化完成，关节数: " + mapper.jointMap.Count);
        }
    }

    // 重置所有关节到零位
    public void ResetToZero()
    {
        ApplyJoints(zeroJoints);
    }

    private void Update()
    {
        //if (Input.GetKeyDown(KeyCode.J)) 
        //{
        //    ApplyJoints(walkingPose);
        //}
        //else if (Input.GetKeyDown(KeyCode.K)) 
        //{
        //    ApplyJoints(greetingPose); 
        //}
        //else if (Input.GetKeyDown(KeyCode.L)) 
        //{
        //    ApplyJoints(tPoseJoints);
        //}
    }

    // 按固定顺序应用关节值（弧度）
    public void ApplyJoints(float[] j)
    {
        if (mapper == null || mapper.jointMap == null || mapper.jointMap.Count == 0)
        {
            Debug.LogWarning("关节映射未初始化！");
            return;
        }

        if(j == null) 
        {
            return;
        }

        if (j.Length < 20)
        {
            Debug.LogError($"关节数组长度不足: {j.Length}，需要20个");
            return;
        }

        // 更新关节值
        Array.Copy(currentJoints, previousJoints, currentJoints.Length);
        Array.Copy(j, currentJoints, Math.Min(j.Length, currentJoints.Length));

        // 应用每个关节
        SetJoint(j[0], "waist_yaw");
        SetJoint(j[1], "waist_pitch");
        SetJoint(j[2], "knee");
        SetJoint(j[3], "ankle");

        SetJoint(j[4], "left_shoulder_inner");
        SetJoint(j[5], "left_shoulder_outer");
        SetJoint(j[6], "left_upper_arm");
        SetJoint(j[7], "left_elbow");
        SetJoint(j[8], "left_forearm");
        SetJoint(j[9], "left_wrist_upper");
        SetJoint(j[10], "left_wrist_lower");

        SetJoint(j[11], "right_shoulder_inner");
        SetJoint(j[12], "right_shoulder_outer");
        SetJoint(j[13], "right_upper_arm");
        SetJoint(j[14], "right_elbow");
        SetJoint(j[15], "right_forearm");
        SetJoint(j[16], "right_wrist_upper");
        SetJoint(j[17], "right_wrist_lower");

        SetJoint(j[18], "neck_yaw");
        SetJoint(j[19], "neck_pitch");
    }

    public void ApplyNamedJoints(IReadOnlyDictionary<string, float> jointsByName)
    {
        if (mapper == null || mapper.jointMap == null || mapper.jointMap.Count == 0)
        {
            Debug.LogWarning("关节映射未初始化！");
            return;
        }

        if (jointsByName == null || jointsByName.Count == 0)
        {
            return;
        }

        Array.Copy(currentJoints, previousJoints, currentJoints.Length);

        foreach (KeyValuePair<string, float> pair in jointsByName)
        {
            if (!mapper.jointMap.ContainsKey(pair.Key))
            {
                continue;
            }

            int index = GetJointIndex(pair.Key);
            if (index >= 0 && index < currentJoints.Length)
            {
                currentJoints[index] = pair.Value;
            }

            SetJoint(pair.Value, pair.Key);
        }
    }

    // 单个关节设置
    void SetJoint(float rad, string name)
    {
        if (!mapper.jointMap.ContainsKey(name))
            return;

        // 检查是否有显著变化
        int index = GetJointIndex(name);
        if (index >= 0 && Math.Abs(currentJoints[index] - previousJoints[index]) < updateThreshold)
            return;

        var articulation = mapper.jointMap[name];
        if (articulation == null)
            return;

        var drive = articulation.xDrive;
        ApplyDriveDefaultsIfNeeded(ref drive);
        drive.target = (float)(rad * Mathf.Rad2Deg); // 弧度转角度
        articulation.xDrive = drive;

        if (logChanges)
        {
            Debug.Log($"{name}: {rad:F6} rad -> {drive.target:F2}°");
        }
    }

    private void ApplyDriveDefaultsIfNeeded(ref ArticulationDrive drive)
    {
        float stiffness = GetUrdfControllerFloat("stiffness", fallbackDriveStiffness);
        float damping = GetUrdfControllerFloat("damping", fallbackDriveDamping);
        float forceLimit = GetUrdfControllerFloat("forceLimit", fallbackDriveForceLimit);

        if (drive.stiffness <= 0f)
        {
            drive.stiffness = stiffness;
        }

        if (drive.damping <= 0f)
        {
            drive.damping = damping;
        }

        if (drive.forceLimit <= 0f)
        {
            drive.forceLimit = forceLimit;
        }
    }

    private Component FindUrdfControllerComponent()
    {
        MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            Type type = behaviour.GetType();
            if (type.FullName == "Unity.Robotics.UrdfImporter.Control.Controller" || type.Name == "Controller")
            {
                return behaviour;
            }
        }

        return null;
    }

    private float GetUrdfControllerFloat(string memberName, float fallback)
    {
        if (urdfControllerComponent == null)
        {
            return fallback;
        }

        Type type = urdfControllerComponent.GetType();
        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (field != null && field.FieldType == typeof(float))
        {
            float value = (float)field.GetValue(urdfControllerComponent);
            if (value > 0f)
            {
                return value;
            }
        }

        PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (property != null && property.PropertyType == typeof(float))
        {
            float value = (float)property.GetValue(urdfControllerComponent, null);
            if (value > 0f)
            {
                return value;
            }
        }

        return fallback;
    }

    // 获取关节索引
    int GetJointIndex(string name)
    {
        string[] names = new string[]
        {
            "waist_yaw", "waist_pitch", "knee", "ankle",
            "left_shoulder_inner", "left_shoulder_outer", "left_upper_arm", "left_elbow",
            "left_forearm", "left_wrist_upper", "left_wrist_lower",
            "right_shoulder_inner", "right_shoulder_outer", "right_upper_arm", "right_elbow",
            "right_forearm", "right_wrist_upper", "right_wrist_lower",
            "neck_yaw", "neck_pitch"
        };

        return Array.IndexOf(names, name);
    }

    // 获取当前关节值（弧度）
    public float[] GetCurrentJoints()
    {
        return (float[])currentJoints.Clone();
    }

    // 获取当前关节值（角度）
    public float[] GetCurrentJointsDegrees()
    {
        float[] degrees = new float[currentJoints.Length];
        for (int i = 0; i < currentJoints.Length; i++)
        {
            degrees[i] = (float)(currentJoints[i] * Mathf.Rad2Deg);
        }
        return degrees;
    }

    //void OnGUI()
    //{
    //    if (!showJointValues || currentJoints == null)
    //        return;

    //    GUILayout.BeginArea(new Rect(displayOffset.x, displayOffset.y, 300, 600));
    //    GUILayout.BeginVertical("box");

    //    GUILayout.Label("关节状态（弧度）:");
    //    for (int i = 0; i < currentJoints.Length; i++)
    //    {
    //        string jointName = GetJointName(i);
    //        GUILayout.Label($"{jointName}: {currentJoints[i]:F4} rad");
    //    }

    //    GUILayout.EndVertical();
    //    GUILayout.EndArea();
    //}

    string GetJointName(int index)
    {
        string[] names = new string[]
        {
            "waist_yaw", "waist_pitch", "knee", "ankle",
            "left_shoulder_inner", "left_shoulder_outer", "left_upper_arm", "left_elbow",
            "left_forearm", "left_wrist_upper", "left_wrist_lower",
            "right_shoulder_inner", "right_shoulder_outer", "right_upper_arm", "right_elbow",
            "right_forearm", "right_wrist_upper", "right_wrist_lower",
            "neck_yaw", "neck_pitch"
        };

        return index < names.Length ? names[index] : $"关节{index}";
    }
}
