using UnityEngine;

[DefaultExecutionOrder(100), DisallowMultipleComponent]
public sealed class EVAGazeController : MonoBehaviour
{
    [Header("Player Tracking")]
    [Tooltip("Leave empty to follow the Main Camera (desktop or VR headset).")]
    [SerializeField] private Transform target;
    [SerializeField, Range(10f, 80f)] private float maximumHeadYaw = 60f;
    [SerializeField, Range(5f, 45f)] private float maximumHeadPitch = 30f;
    [SerializeField, Min(0.1f)] private float gazeResponse = 7f;
    [SerializeField, Min(0.05f)] private float minimumDistance = 0.5f;

    [Header("Body Turning")]
    [SerializeField] private bool turnBody = true;
    [SerializeField, Range(20f, 90f)] private float turnStartAngle = 45f;
    [SerializeField, Range(0f, 25f)] private float turnStopAngle = 12f;
    [SerializeField, Min(0.05f)] private float turnBlendSeconds = 0.18f;

    private EVAAnimationController animationController;
    private EVAAppearanceController appearance;
    private EVATurnAnimationLibrary library;
    private Animator animator;
    private RuntimeAnimatorController appliedController;
    private Transform head, neck;
    private Quaternion animatedHead, animatedNeck;
    private Vector3 headForwardLocal, headUpLocal;
    private bool offsetApplied, turning, warned;
    private float turnDirection, turnElapsed, turnDuration, turnWeight;
    private float lookYaw, lookPitch;
    private int turnLayer = -1;


    private void Awake()
    {
        animationController = GetComponent<EVAAnimationController>();
        appearance = GetComponent<EVAAppearanceController>();
        library = Resources.Load<EVATurnAnimationLibrary>("EVATurnAnimations");
    }

    private void Update()
    {
        BeginFrame();
        if (target == null && Camera.main != null) target = Camera.main.transform;
        TickBody(Time.deltaTime);
    }

    public void BeginFrame()
    {
        RestoreAnimatedPose();
        RefreshRig();
    }

    public void TickBody(float deltaTime)
    {
        RefreshRig();
        if (animator == null || !animator.isActiveAndEnabled || !animator.isInitialized) return;
        Vector3 direction = target != null ? target.position - transform.position : Vector3.zero;
        direction = Vector3.ProjectOnPlane(direction, Vector3.up);
        bool valid = target != null && direction.sqrMagnitude >= minimumDistance * minimumDistance;
        float angle = valid ? Vector3.SignedAngle(transform.forward, direction, Vector3.up) : 0f;
        if (!valid || !turnBody || turnLayer < 0 || library == null) turning = false;
        else
        {
            if (turning && (Mathf.Abs(angle) <= turnStopAngle
                || (Mathf.Abs(angle) < 100f && Mathf.Sign(angle) != turnDirection))) turning = false;
            if (!turning && Mathf.Abs(angle) > turnStartAngle) BeginTurn(Mathf.Sign(angle));
            if (turning)
            {
                float remaining = Mathf.Max(0f, Mathf.Abs(angle) - turnStopAngle);
                float step = Mathf.Min(60f / turnDuration * deltaTime, remaining);
                transform.rotation = Quaternion.AngleAxis(step * turnDirection, Vector3.up) * transform.rotation;
                turnElapsed += deltaTime;
                if (turnElapsed >= turnDuration && remaining > step + turnStopAngle)
                    BeginTurn(turnDirection);
            }
        }
        if (turnLayer >= 0)
        {
            turnWeight = Mathf.MoveTowards(turnWeight, turning ? 1f : 0f,
                deltaTime / Mathf.Max(0.05f, turnBlendSeconds));
            animator.SetLayerWeight(turnLayer, turnWeight);
        }
    }

    private void BeginTurn(float direction)
    {
        bool male = appearance != null && appearance.IsMale;
        bool right = direction > 0f;
        var clip = library.GetClip(male, right);
        int hash = Animator.StringToHash(EVATurnAnimationLibrary.StateName(male, right));
        if (clip == null || !animator.HasState(turnLayer, hash)) return;
        turnDirection = direction;
        turnElapsed = 0f;
        turnDuration = Mathf.Max(0.2f, clip.length);
        turning = true;
        animator.Play(hash, turnLayer, 0f);
    }

    private void LateUpdate() => TickGaze(Time.deltaTime);

    public void TickGaze(float deltaTime)
    {
        RestoreAnimatedPose();
        RefreshRig();
        if (head == null || animator == null || !animator.isActiveAndEnabled) return;
        animatedHead = head.localRotation;
        if (neck != null) animatedNeck = neck.localRotation;
        Vector3 direction = target != null ? target.position - head.position : Vector3.zero;
        Vector3 horizontal = Vector3.ProjectOnPlane(direction, Vector3.up);
        bool valid = target != null && horizontal.sqrMagnitude >= minimumDistance * minimumDistance;
        float desiredYaw = valid ? Mathf.Clamp(Vector3.SignedAngle(transform.forward, horizontal,
            Vector3.up), -maximumHeadYaw, maximumHeadYaw) : 0f;
        float desiredPitch = valid ? Mathf.Clamp(-Mathf.Atan2(direction.y, horizontal.magnitude)
            * Mathf.Rad2Deg, -maximumHeadPitch, maximumHeadPitch) : 0f;
        float response = 1f - Mathf.Exp(-gazeResponse * Mathf.Max(0f, deltaTime));
        lookYaw = Mathf.Lerp(lookYaw, desiredYaw, response);
        lookPitch = Mathf.Lerp(lookPitch, desiredPitch, response);
        if (!valid && Mathf.Abs(lookYaw) + Mathf.Abs(lookPitch) < 0.01f) return;

        Vector3 forward = Quaternion.AngleAxis(lookYaw, Vector3.up) * transform.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        forward = Quaternion.AngleAxis(lookPitch, right) * forward;
        Quaternion currentFrame = Quaternion.LookRotation(head.TransformDirection(headForwardLocal),
            head.TransformDirection(headUpLocal));
        Quaternion correction = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Inverse(currentFrame);
        if (neck != null) neck.rotation = Quaternion.Slerp(Quaternion.identity, correction, 0.3f) * neck.rotation;
        head.rotation = Quaternion.Slerp(Quaternion.identity, correction, neck != null ? 0.7f : 1f) * head.rotation;
        offsetApplied = true;
    }

    public void RefreshRig()
    {
        if (library == null) library = Resources.Load<EVATurnAnimationLibrary>("EVATurnAnimations");
        if (appearance == null) appearance = GetComponent<EVAAppearanceController>();
        if (animationController == null) animationController = GetComponent<EVAAnimationController>();
        Animator next = animationController != null ? animationController.CurrentAnimator : GetComponent<Animator>();
        if (next == animator && next != null && next.isInitialized
            && turnLayer >= 0 && next.runtimeAnimatorController == appliedController) return;
        RestoreAnimatedPose();
        if (animator != null && animator.isInitialized && turnLayer >= 0) animator.SetLayerWeight(turnLayer, 0f);
        animator = next;
        appliedController = next != null ? next.runtimeAnimatorController : null;
        head = neck = null;
        turnLayer = next != null && next.isInitialized ? next.GetLayerIndex(EVATurnAnimationLibrary.LayerName) : -1;
        turning = false;
        turnWeight = 0f;
        if (next == null) return;
        Transform skeleton = next.transform.Find("Bip01");
        if (skeleton == null) return;
        foreach (var bone in skeleton.GetComponentsInChildren<Transform>(true))
        {
            if (bone.name == "Bip01 Head") head = bone;
            else if (bone.name == "Bip01 Neck") neck = bone;
        }
        if (head != null)
        {
            var axes = next.GetComponent<EVARocketboxHeadAxes>();
            if (axes != null) { headForwardLocal = axes.forward; headUpLocal = axes.up; }
            else { headForwardLocal = head.InverseTransformDirection(transform.forward); headUpLocal = head.InverseTransformDirection(Vector3.up); }
        }
        if ((head == null || turnLayer < 0 || library == null) && !warned)
        {
            warned = true;
            Debug.LogWarning("[EVA/Gaze] Tracking rig/turn layer missing. Use EVA > Gaze > Set Up Player Tracking.", this);
        }
    }

    private void RestoreAnimatedPose()
    {
        if (!offsetApplied) return;
        if (head != null) head.localRotation = animatedHead;
        if (neck != null) neck.localRotation = animatedNeck;
        offsetApplied = false;
    }

    private void OnDisable()
    {
        RestoreAnimatedPose();
        if (animator != null && animator.isInitialized && turnLayer >= 0) animator.SetLayerWeight(turnLayer, 0f);
        turning = false;
    }
}
