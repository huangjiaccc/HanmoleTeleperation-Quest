using UnityEngine;

public class SphereFollowTarget : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;

    [Header("Follow")]
    [SerializeField] private bool followPosition = true;


    [Header("Update")]
    private bool useLateUpdate = true;



    private void Awake()
    {
#if UNITY_EDITOR
        followPosition = false;
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

        Vector3 desiredPosition = new Vector3(transform.position.x, target.position.y - UIManager.Instance.UD_slider.value, UIManager.Instance.LR_slider.value);

        if (followPosition)
        {
            transform.position = desiredPosition;
        }

    }
}
