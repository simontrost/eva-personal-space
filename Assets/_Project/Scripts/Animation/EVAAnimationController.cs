using UnityEngine;

public enum EVAAnimationStyle
{
    NeutralProfessional,
    FriendlyRelaxed,
    Expressive
}

public sealed class EVAAnimationController : MonoBehaviour
{
    private static readonly int IdleStateHash =
        Animator.StringToHash("Base Layer.Idle");

    private static readonly int ListeningStateHash =
        Animator.StringToHash("Base Layer.Listening");

    private static readonly int ThinkingStateHash =
        Animator.StringToHash("Base Layer.Thinking");

    private static readonly int SpeakingStateHash =
        Animator.StringToHash("Base Layer.Speaking");

    [Header("References")]
    [SerializeField]
    private Animator animator;

    [SerializeField]
    private EVAStateController stateController;

    [Header("Animation Style")]
    [SerializeField]
    private EVAAnimationStyle animationStyle =
        EVAAnimationStyle.NeutralProfessional;

    [SerializeField]
    private RuntimeAnimatorController neutralProfessionalController;

    [SerializeField]
    private RuntimeAnimatorController friendlyRelaxedController;

    [SerializeField]
    private RuntimeAnimatorController expressiveController;

    [Header("Transition Settings")]
    [SerializeField]
    [Min(0f)]
    private float transitionDurationSeconds = 0.2f;

    [Header("Debugging")]
    [SerializeField]
    private bool logAnimationChanges = true;

    private EVAAnimationStyle appliedAnimationStyle;
    private bool hasAppliedAnimationStyle;

    public Animator CurrentAnimator => animator;
    public EVAAnimationStyle AnimationStyle => animationStyle;

    public void SetStyle(EVAAnimationStyle style)
    {
        if ((int)style < 0 || (int)style > 2)
            throw new System.ArgumentOutOfRangeException(nameof(style));
        animationStyle = style;
        if (Application.isPlaying) ApplyAnimationStyle(false);
        else OnValidate();
    }

    public void SetAnimator(Animator replacement)
    {
        if (replacement == null)
        {
            throw new System.ArgumentNullException(nameof(replacement));
        }

        float normalizedTime = Application.isPlaying
            && animator != null && animator.isActiveAndEnabled
            && animator.isInitialized
            ? animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f
            : 0f;
        animator = replacement;
        animator.runtimeAnimatorController = GetSelectedController();
        if (!Application.isPlaying)
        {
            return;
        }

        animator.Rebind();
        appliedAnimationStyle = animationStyle;
        hasAppliedAnimationStyle = true;
        PlayState(stateController != null ? stateController.CurrentState : EVAState.Idle,
            true, normalizedTime);
        animator.Update(0f);
    }

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        if (stateController == null)
        {
            stateController = FindFirstObjectByType<EVAStateController>();
        }
    }

    private void OnEnable()
    {
        if (stateController != null)
        {
            stateController.StateChanged += HandleStateChanged;
        }
    }

    private void Start()
    {
        ApplyAnimationStyle(true);

        if (stateController == null)
        {
            Debug.LogError(
                "[EVA] Animation controller could not find " +
                "EVAStateController."
            );
        }
    }

    private void OnValidate()
    {
        if (Application.isPlaying)
        {
            return;
        }

        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        RuntimeAnimatorController selectedController =
            GetSelectedController();

        if (animator != null && selectedController != null)
        {
            animator.runtimeAnimatorController = selectedController;
        }
    }

    private void Update()
    {
        if (
            !hasAppliedAnimationStyle
            || animationStyle != appliedAnimationStyle
        )
        {
            ApplyAnimationStyle(false);
        }
    }

    private void OnDisable()
    {
        if (stateController != null)
        {
            stateController.StateChanged -= HandleStateChanged;
        }
    }

    private void HandleStateChanged(
        EVAState previousState,
        EVAState currentState
    )
    {
        PlayState(currentState, false);
    }

    private void ApplyAnimationStyle(bool restartAtBeginning)
    {
        if (animator == null)
        {
            Debug.LogError(
                "[EVA] Animation controller has no Animator reference."
            );
            return;
        }

        RuntimeAnimatorController selectedController =
            GetSelectedController();

        if (selectedController == null)
        {
            Debug.LogError(
                $"[EVA] No controller is assigned for animation style " +
                $"'{animationStyle}'."
            );
            return;
        }

        float normalizedTime = 0f;

        if (
            !restartAtBeginning
            && animator.runtimeAnimatorController != null
        )
        {
            normalizedTime =
                animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
        }

        animator.runtimeAnimatorController = selectedController;

        appliedAnimationStyle = animationStyle;
        hasAppliedAnimationStyle = true;

        EVAState currentState = stateController != null
            ? stateController.CurrentState
            : EVAState.Idle;

        PlayState(currentState, true, normalizedTime);

        if (logAnimationChanges)
        {
            Debug.Log(
                $"[EVA] Animation style changed: {animationStyle}"
            );
        }
    }

    private RuntimeAnimatorController GetSelectedController()
    {
        switch (animationStyle)
        {
            case EVAAnimationStyle.NeutralProfessional:
                return neutralProfessionalController;

            case EVAAnimationStyle.FriendlyRelaxed:
                return friendlyRelaxedController;

            case EVAAnimationStyle.Expressive:
                return expressiveController;

            default:
                return neutralProfessionalController;
        }
    }

    private void PlayState(
        EVAState state,
        bool playImmediately,
        float normalizedTime = 0f
    )
    {
        if (
            animator == null
            || animator.runtimeAnimatorController == null
        )
        {
            return;
        }

        int stateHash = GetStateHash(state);

        if (!animator.HasState(0, stateHash))
        {
            Debug.LogError(
                $"[EVA] Animator state '{state}' was not found " +
                "on Base Layer."
            );
            return;
        }

        if (playImmediately)
        {
            animator.Play(stateHash, 0, normalizedTime);
            return;
        }

        animator.CrossFadeInFixedTime(
            stateHash,
            transitionDurationSeconds,
            0
        );
    }

    private static int GetStateHash(EVAState state)
    {
        switch (state)
        {
            case EVAState.Listening:
                return ListeningStateHash;

            case EVAState.Thinking:
                return ThinkingStateHash;

            case EVAState.Speaking:
                return SpeakingStateHash;

            case EVAState.Idle:
            default:
                return IdleStateHash;
        }
    }
}
