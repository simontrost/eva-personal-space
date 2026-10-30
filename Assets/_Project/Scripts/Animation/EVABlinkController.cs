using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class EVABlinkController : MonoBehaviour
{
    private const string LeftBlinkBlendShape =
        "AK_09_EyeBlinkLeft";

    private const string RightBlinkBlendShape =
        "AK_10_EyeBlinkRight";

    [Header("References")]
    [SerializeField]
    private SkinnedMeshRenderer faceRenderer;

    [Header("Blink Strength")]
    [SerializeField]
    [Range(0f, 100f)]
    private float maximumBlinkWeight = 100f;

    [Header("Blink Timing")]
    [SerializeField]
    [Min(0.1f)]
    private float minimumIntervalSeconds = 2.5f;

    [SerializeField]
    [Min(0.1f)]
    private float maximumIntervalSeconds = 5.5f;

    [SerializeField]
    [Min(0.01f)]
    private float closingDurationSeconds = 0.07f;

    [SerializeField]
    [Min(0f)]
    private float closedDurationSeconds = 0.035f;

    [SerializeField]
    [Min(0.01f)]
    private float openingDurationSeconds = 0.11f;

    [Header("Debugging")]
    [SerializeField]
    private bool logInitialization = true;

    private int leftBlinkIndex = -1;
    private int rightBlinkIndex = -1;
    private BlinkPhase phase = BlinkPhase.Waiting;
    private float phaseStartedAt;
    private float nextBlinkAt;
    private bool blendShapesReady;

    public SkinnedMeshRenderer FaceRenderer => faceRenderer;

    public void SetFaceRenderer(SkinnedMeshRenderer replacement)
    {
        if (!RendererContainsBlinkShapes(replacement))
        {
            throw new ArgumentException("The selected appearance needs both blink shapes.",
                nameof(replacement));
        }

        if (blendShapesReady && faceRenderer != null) ApplyBlinkWeight(0f);
        blendShapesReady = false;
        faceRenderer = replacement;
        ResolveBlendShapes();
        phase = BlinkPhase.Waiting;
        ScheduleNextBlink();
    }

    private enum BlinkPhase
    {
        Waiting,
        Closing,
        Closed,
        Opening
    }

    private void Awake()
    {
        if (!RendererContainsBlinkShapes(faceRenderer))
        {
            faceRenderer = FindFaceRenderer();
        }

        ResolveBlendShapes();
    }

    private void OnEnable()
    {
        phase = BlinkPhase.Waiting;
        ScheduleNextBlink();
    }

    private void LateUpdate()
    {
        if (!blendShapesReady)
        {
            return;
        }

        float now = Time.unscaledTime;

        switch (phase)
        {
            case BlinkPhase.Waiting:
                if (now >= nextBlinkAt)
                {
                    StartPhase(BlinkPhase.Closing, now);
                }
                break;

            case BlinkPhase.Closing:
                ApplyBlinkWeight(
                    SmoothProgress(
                        now,
                        closingDurationSeconds
                    ) * maximumBlinkWeight
                );

                if (
                    now - phaseStartedAt
                    >= closingDurationSeconds
                )
                {
                    ApplyBlinkWeight(maximumBlinkWeight);
                    StartPhase(BlinkPhase.Closed, now);
                }
                break;

            case BlinkPhase.Closed:
                ApplyBlinkWeight(maximumBlinkWeight);

                if (
                    now - phaseStartedAt
                    >= closedDurationSeconds
                )
                {
                    StartPhase(BlinkPhase.Opening, now);
                }
                break;

            case BlinkPhase.Opening:
                ApplyBlinkWeight(
                    (1f - SmoothProgress(
                        now,
                        openingDurationSeconds
                    )) * maximumBlinkWeight
                );

                if (
                    now - phaseStartedAt
                    >= openingDurationSeconds
                )
                {
                    ApplyBlinkWeight(0f);
                    phase = BlinkPhase.Waiting;
                    ScheduleNextBlink();
                }
                break;
        }
    }

    private void OnDisable()
    {
        if (blendShapesReady)
        {
            ApplyBlinkWeight(0f);
        }
    }

    private void OnValidate()
    {
        if (maximumIntervalSeconds < minimumIntervalSeconds)
        {
            maximumIntervalSeconds = minimumIntervalSeconds;
        }
    }

    private void ResolveBlendShapes()
    {
        if (faceRenderer == null || faceRenderer.sharedMesh == null)
        {
            Debug.LogError(
                "[EVA] Blink controller requires a valid " +
                "SkinnedMeshRenderer."
            );
            return;
        }

        Mesh mesh = faceRenderer.sharedMesh;

        leftBlinkIndex = FindBlendShapeIndex(
            mesh,
            LeftBlinkBlendShape
        );

        rightBlinkIndex = FindBlendShapeIndex(
            mesh,
            RightBlinkBlendShape
        );

        if (leftBlinkIndex < 0 || rightBlinkIndex < 0)
        {
            Debug.LogError(
                "[EVA] Required eye blink blend shapes were not " +
                "found on the selected face renderer."
            );
            return;
        }

        blendShapesReady = true;
        ApplyBlinkWeight(0f);

        if (logInitialization && Application.isPlaying)
        {
            Debug.Log(
                "[EVA] Automatic blinking is ready on " +
                $"'{faceRenderer.name}'."
            );
        }
    }

    private SkinnedMeshRenderer FindFaceRenderer()
    {
        SkinnedMeshRenderer rendererOnThisObject =
            GetComponent<SkinnedMeshRenderer>();

        if (RendererContainsBlinkShapes(rendererOnThisObject))
        {
            return rendererOnThisObject;
        }

        Animator parentAnimator = GetComponentInParent<Animator>();
        Transform searchRoot = parentAnimator != null
            ? parentAnimator.transform
            : transform.root;

        SkinnedMeshRenderer[] renderers =
            searchRoot.GetComponentsInChildren<SkinnedMeshRenderer>(
                true
            );

        foreach (SkinnedMeshRenderer candidate in renderers)
        {
            if (RendererContainsBlinkShapes(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool RendererContainsBlinkShapes(
        SkinnedMeshRenderer renderer
    )
    {
        if (renderer == null || renderer.sharedMesh == null)
        {
            return false;
        }

        return FindBlendShapeIndex(
            renderer.sharedMesh,
            LeftBlinkBlendShape
        ) >= 0
            && FindBlendShapeIndex(
                renderer.sharedMesh,
                RightBlinkBlendShape
            ) >= 0;
    }

    private static int FindBlendShapeIndex(
        Mesh mesh,
        string requiredName
    )
    {
        int exactIndex = mesh.GetBlendShapeIndex(requiredName);

        if (exactIndex >= 0)
        {
            return exactIndex;
        }

        string requiredSuffix = "." + requiredName;

        for (
            int blendShapeIndex = 0;
            blendShapeIndex < mesh.blendShapeCount;
            blendShapeIndex++
        )
        {
            string importedName =
                mesh.GetBlendShapeName(blendShapeIndex);

            if (
                importedName.EndsWith(
                    requiredSuffix,
                    StringComparison.Ordinal
                )
            )
            {
                return blendShapeIndex;
            }
        }

        return -1;
    }

    private void StartPhase(BlinkPhase nextPhase, float now)
    {
        phase = nextPhase;
        phaseStartedAt = now;
    }

    private float SmoothProgress(float now, float duration)
    {
        float progress = Mathf.Clamp01(
            (now - phaseStartedAt) / duration
        );

        return Mathf.SmoothStep(0f, 1f, progress);
    }

    private void ScheduleNextBlink()
    {
        float minimumInterval = Mathf.Max(
            0.1f,
            minimumIntervalSeconds
        );

        float maximumInterval = Mathf.Max(
            minimumInterval,
            maximumIntervalSeconds
        );

        nextBlinkAt = Time.unscaledTime + UnityEngine.Random.Range(
            minimumInterval,
            maximumInterval
        );
    }

    private void ApplyBlinkWeight(float weight)
    {
        float clampedWeight = Mathf.Clamp(
            weight,
            0f,
            maximumBlinkWeight
        );

        faceRenderer.SetBlendShapeWeight(
            leftBlinkIndex,
            clampedWeight
        );

        faceRenderer.SetBlendShapeWeight(
            rightBlinkIndex,
            clampedWeight
        );
    }
}
