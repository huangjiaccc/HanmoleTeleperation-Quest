using UnityEngine;

[DisallowMultipleComponent]
public class LookAtTarget : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private string targetName = "CenterEyeAnchor";

    [Header("Behavior")]
    [SerializeField] private bool useLateUpdate = true;
    [SerializeField] private bool lockYAxis = true;
    [Tooltip("Flip 180° after LookAt. Useful when the object's forward is opposite (e.g. Canvas facing away).")]
    [SerializeField] private bool flipForward = false;
    [SerializeField] private Vector3 worldUp = Vector3.up;

    private float nextFindTime;

    private void Update()
    {
        if (!useLateUpdate)
        {
            Tick();
        }
    }

    private void LateUpdate()
    {
        if (useLateUpdate)
        {
            Tick();
        }
    }

    private void Tick()
    {
        if (target == null)
        {
            TryResolveTarget();
            if (target == null)
            {
                return;
            }
        }

        Vector3 lookPos = target.position;
        if (lockYAxis)
        {
            lookPos.y = transform.position.y;
        }

        if ((lookPos - transform.position).sqrMagnitude < 0.0001f)
        {
            return;
        }

        transform.LookAt(lookPos, worldUp);
        if (flipForward)
        {
            transform.Rotate(0f, 180f, 0f, Space.Self);
        }
    }

    private void TryResolveTarget()
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        if (Time.unscaledTime < nextFindTime)
        {
            return;
        }

        nextFindTime = Time.unscaledTime + 1f;
        GameObject go = GameObject.Find(targetName);
        if (go != null)
        {
            target = go.transform;
        }
    }
}
