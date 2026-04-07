using UnityEngine;

public class OVRReset : MonoBehaviour
{
    public static OVRReset Instance;
    public Transform parent; // 需要移动的父物体
    public Transform child;  // 已知世界位置和旋转的子物体
#if !IS_ANDROID

    private void Awake()
    {
        Instance = this;
    }
    public void AlignChildToWorld()
    {
        // 保存子物体本地矩阵
                
        Vector3 childLocalPos = child.localPosition;
        Vector3 targetPos = new Vector3(0, child.localPosition.y, 0);

        Quaternion childLocalRot = child.localRotation;

        // 构造目标世界旋转
        Quaternion targetRot = Quaternion.Euler(0,0,0);

        // 计算新的父物体旋转
        Quaternion parentRot = targetRot * Quaternion.Inverse(childLocalRot);

        // 计算新的父物体位置
        Vector3 parentPos = targetPos - parentRot * childLocalPos;

        // 应用到父物体
        parent.position = parentPos;
        parent.rotation = parentRot;
    }
#endif
}
