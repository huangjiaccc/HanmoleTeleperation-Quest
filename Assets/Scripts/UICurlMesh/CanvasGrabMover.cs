#if !IS_ANDROID
using UnityEngine;

[DisallowMultipleComponent]
public class CanvasGrabMover : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform leftController;
    [SerializeField] private Transform rightController;
    [Tooltip("Optional. If set, joystick movement is relative to this transform (e.g. HMD).")]
    [SerializeField] private Transform moveSpaceOverride;

    [Header("Grab")]
    [Min(0.1f)]
    [SerializeField] private float longPressSeconds = 1f;
    [Range(0.1f, 1f)]
    [SerializeField] private float triggerThreshold = 0.8f;
    [Min(0.1f)]
    [SerializeField] private float grabDistance = 2f;
    [Tooltip("If enabled, the canvas snaps to a fixed distance from the controller on grab.")]
    [SerializeField] private bool snapToGrabDistance = false;
    [Tooltip("If enabled, re-parent the canvas to the grabbing controller while held.")]
    [SerializeField] private bool followRotation = true;

    [Header("Selection")]
    [Tooltip("Only allow grabbing this canvas when the controller ray is pointing at it.")]
    [SerializeField] private bool requirePointerHit = true;
    [Min(0.1f)]
    [SerializeField] private float pointerMaxDistance = 30f;
    [SerializeField] private LayerMask pointerMask = ~0;
    [Tooltip("Optional. If set, raycast must hit this collider.")]
    [SerializeField] private Collider grabCollider;

    [Header("Move (while holding trigger)")]
    [Min(0f)]
    [SerializeField] private float moveSpeed = 0.6f;
    [Range(0f, 1f)]
    [SerializeField] private float stickDeadzone = 0.2f;
    [SerializeField] private bool lockY = true;
    [SerializeField] private bool useRightStickForBothHands = true;

    private enum Hand
    {
        None,
        Left,
        Right
    }

    private Hand activeHand = Hand.None;
    private Transform activeGrabber;
    private Transform originalParent;
    private float leftHold;
    private float rightHold;
    private Vector3 grabLocalOffset;
    private Vector3 stickOffsetLocal;
    private Quaternion grabRotation;
    private Quaternion grabRotationOffset;
    private bool warnedMissingCollider;

    private static CanvasGrabMover activeGrabOwner;

    private void OnDisable()
    {
        EndGrab();
    }

    private void Update()
    {
        if (OVRInputController.instance == null)
        {
            return;
        }

        if (activeGrabOwner != null && activeGrabOwner != this)
        {
            ResetHolds();
            return;
        }

        if (activeHand == Hand.None)
        {
            UpdateHoldTimers();
            TryBeginGrab();
            return;
        }

        if (!IsTriggerPressed(activeHand))
        {
            EndGrab();
            return;
        }

        ApplyStickMove();
        UpdateGrabPose();
    }

    private void UpdateHoldTimers()
    {
        if (IsTriggerPressed(Hand.Left) && IsPointingAtThisCanvas(Hand.Left))
        {
            leftHold += Time.deltaTime;
        }
        else
        {
            leftHold = 0f;
        }

        if (IsTriggerPressed(Hand.Right) && IsPointingAtThisCanvas(Hand.Right))
        {
            rightHold += Time.deltaTime;
        }
        else
        {
            rightHold = 0f;
        }
    }

    private void TryBeginGrab()
    {
        bool leftReady = leftHold >= longPressSeconds && leftController != null;
        bool rightReady = rightHold >= longPressSeconds && rightController != null;

        if (!leftReady && !rightReady)
        {
            return;
        }

        Hand hand;
        if (leftReady && rightReady)
        {
            float leftTrigger = OVRInputController.instance.triggers.l;
            float rightTrigger = OVRInputController.instance.triggers.r;
            hand = rightTrigger >= leftTrigger ? Hand.Right : Hand.Left;
        }
        else
        {
            hand = rightReady ? Hand.Right : Hand.Left;
        }

        BeginGrab(hand);
    }

    private void BeginGrab(Hand hand)
    {
        if (activeGrabOwner != null && activeGrabOwner != this)
        {
            return;
        }

        activeHand = hand;
        activeGrabber = hand == Hand.Left ? leftController : rightController;
        if (activeGrabber == null)
        {
            activeHand = Hand.None;
            return;
        }

        originalParent = transform.parent;
        transform.SetParent(activeGrabber, true);
        activeGrabOwner = this;
        leftHold = 0f;
        rightHold = 0f;
        stickOffsetLocal = Vector3.zero;

        if (snapToGrabDistance)
        {
            Vector3 toCanvas = transform.position - activeGrabber.position;
            Vector3 dir = toCanvas.sqrMagnitude > 0.0001f ? toCanvas.normalized : activeGrabber.forward;
            grabLocalOffset = activeGrabber.InverseTransformDirection(dir) * grabDistance;
        }
        else
        {
            grabLocalOffset = activeGrabber.InverseTransformPoint(transform.position);
        }
        grabRotation = transform.rotation;
        grabRotationOffset = Quaternion.Inverse(activeGrabber.rotation) * transform.rotation;

        UpdateGrabPose();
    }

    private void EndGrab()
    {
        if (activeGrabOwner == this)
        {
            activeGrabOwner = null;
        }

        activeHand = Hand.None;
        activeGrabber = null;
        ResetHolds();
        if (originalParent != null) 
        {
            transform.SetParent(originalParent, true);
        }
        originalParent = null;
    }

    private void UpdateGrabPose()
    {
        if (activeGrabber == null)
        {
            return;
        }

        transform.position = activeGrabber.TransformPoint(grabLocalOffset + stickOffsetLocal);

        if (followRotation)
        {
            transform.rotation = activeGrabber.rotation * grabRotationOffset;
        }
        else
        {
            transform.rotation = grabRotation;
        }
    }

    private void ApplyStickMove()
    {
        Vector2 stick = GetActiveStick();
        if (stick.sqrMagnitude < stickDeadzone * stickDeadzone)
        {
            return;
        }

        Transform moveSpace = moveSpaceOverride != null ? moveSpaceOverride : activeGrabber;
        if (moveSpace == null)
        {
            return;
        }

        Vector3 worldMove = (moveSpace.right * stick.x + moveSpace.forward * stick.y) * moveSpeed * Time.deltaTime;
        if (lockY)
        {
            worldMove.y = 0f;
        }

        Vector3 localMove = activeGrabber.InverseTransformDirection(worldMove);
        stickOffsetLocal += localMove;
    }

    private bool IsPointingAtThisCanvas(Hand hand)
    {
        if (!requirePointerHit)
        {
            return true;
        }

        Transform controller = hand == Hand.Left ? leftController : rightController;
        if (controller == null)
        {
            return false;
        }

        EnsureGrabCollider();
        if (grabCollider == null)
        {
            if (!warnedMissingCollider)
            {
                Debug.LogWarning($"[CanvasGrabMover] No collider found for {name}. Disable 'Require Pointer Hit' or assign a collider.");
                warnedMissingCollider = true;
            }
            return false;
        }

        Ray ray = new Ray(controller.position, controller.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, pointerMaxDistance, pointerMask, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        if (grabCollider != null)
        {
            return hit.collider == grabCollider;
        }

        return hit.transform.IsChildOf(transform);
    }

    private void EnsureGrabCollider()
    {
        if (grabCollider != null)
        {
            return;
        }
        if (grabCollider == null)
        {
            grabCollider = GetComponentInChildren<Collider>();
        }
    }

    private Vector2 GetActiveStick()
    {
        if (useRightStickForBothHands)
        {
            return new Vector2(OVRInputController.instance.RightJoy[0], OVRInputController.instance.RightJoy[1]);
        }

        if (activeHand == Hand.Left)
        {
            return new Vector2(OVRInputController.instance.LeftJoy[0], OVRInputController.instance.LeftJoy[1]);
        }

        return new Vector2(OVRInputController.instance.RightJoy[0], OVRInputController.instance.RightJoy[1]);
    }

    private bool IsTriggerPressed(Hand hand)
    {
        float value = hand == Hand.Left
            ? OVRInputController.instance.triggers.l
            : OVRInputController.instance.triggers.r;

        return value >= triggerThreshold;
    }

    private void ResetHolds()
    {
        leftHold = 0f;
        rightHold = 0f;
    }
}
#endif