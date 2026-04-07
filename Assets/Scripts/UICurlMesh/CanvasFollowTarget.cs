using UnityEngine;
#if !IS_ANDROID
[DisallowMultipleComponent]
public class CanvasFollowTarget : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;

    [Header("Follow")]
    [SerializeField] private bool followPosition = true;
    [SerializeField] private bool followRotation = true;

    [Header("Offsets (Target Local Space)")]
    [SerializeField] private Vector3 positionOffset = Vector3.zero;
    [SerializeField] private Vector3 eulerOffset = Vector3.zero;

    [Header("Update")]
    [Tooltip("For XR, LateUpdate usually reduces jitter vs Update.")]
    [SerializeField] private bool useLateUpdate = true;

    [Header("Smoothing (0 = snap)")]
    [Min(0f)]
    [SerializeField] private float positionLerpSpeed = 0f;
    [Min(0f)]
    [SerializeField] private float rotationLerpSpeed = 0f;


    private void Awake()
    {
#if UNITY_EDITOR
        followPosition = false;
        followRotation = false;
#endif
    }

    private void Update()
    {

        if (!useLateUpdate)
        {
            Tick(Time.deltaTime);
        }
    }

    private void LateUpdate()
    {
        if (useLateUpdate)
        {
            Tick(Time.deltaTime);
        }
    }


    private void Tick(float deltaTime)
    {
        if (target == null)
        {
            return;
        }

        Vector3 desiredPosition = target.TransformPoint(positionOffset);
        Quaternion desiredRotation = target.rotation * Quaternion.Euler(eulerOffset);

        if (followPosition)
        {
            if (positionLerpSpeed <= 0f)
            {
                transform.position = desiredPosition;
            }
            else
            {
                float t = 1f - Mathf.Exp(-positionLerpSpeed * deltaTime);
                transform.position = Vector3.Lerp(transform.position, desiredPosition, t);
            }
        }

        if (followRotation)
        {
            if (rotationLerpSpeed <= 0f)
            {
                transform.rotation = desiredRotation;
            }
            else
            {
                float t = 1f - Mathf.Exp(-rotationLerpSpeed * deltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, t);
            }
        }
    }
}
#endif
