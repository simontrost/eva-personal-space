using System;
using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
[AddComponentMenu("EVA/EVA Appearance Controller")]
public sealed class EVAAppearanceController : MonoBehaviour
{
    public static readonly string[] AppearanceNames =
    {
        "Female_Adult_01 (original EVA)", "Male_Adult_04", "Male_Adult_10",
        "Male_Adult_16", "Female_Adult_15", "Female_Party_01"
    };

    [Serializable]
    public sealed class Appearance
    {
        public GameObject model;
        public Animator animator;
        public SkinnedMeshRenderer face;
    }

    [SerializeField, Range(0, 5)] private int appearanceIndex;
    [SerializeField, HideInInspector] private EVAAnimationController animationController;
    [SerializeField, HideInInspector] private EVAVisemeController visemeController;
    [SerializeField, HideInInspector] private EVABlinkController blinkController;
    [SerializeField, HideInInspector] private Renderer[] originalRenderers;
    [SerializeField, HideInInspector] private bool[] originalRendererEnabled;
    [SerializeField, HideInInspector] private Appearance[] appearances;
    private int appliedIndex = -1;
    private bool refreshRequested;

    public int AppearanceIndex => appearanceIndex;
    public bool IsMale => appearanceIndex >= 1 && appearanceIndex <= 3;
    public string VoiceProfile => IsMale ? "male" : "female";
    public Appearance[] Appearances => appearances;
    public bool IsConfigured => appearances != null && appearances.Length == 6;

    public void Configure(EVAAnimationController animations,
        EVAVisemeController visemes, EVABlinkController blinking,
        Renderer[] renderers, Appearance[] options)
    {
        animationController = animations;
        visemeController = visemes;
        blinkController = blinking;
        originalRenderers = renderers;
        originalRendererEnabled = Array.ConvertAll(renderers, r => r.enabled);
        appearances = options;
        appearanceIndex = 0;
        appliedIndex = -1;
        ApplyAppearance();
    }

    public void SetAppearance(int index)
    {
        if (index < 0 || index >= AppearanceNames.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        appearanceIndex = index;
        ApplyAppearance();
    }

    // Imported prefab refreshes can invalidate cached child-component references
    // while the actual appearance remains in the hierarchy. Resolve only missing
    // references; never replace an existing model or reset the selected index.
    public int RepairMissingModelReferences()
    {
        if (!IsConfigured) return 0;
        int repaired = 0;
        for (int i = 0; i < appearances.Length; i++)
        {
            var option = appearances[i];
            if (option.model == null)
            {
                var child = i == 0 ? transform : transform.Find("Appearance_" + AppearanceNames[i]);
                if (child == null) throw new InvalidOperationException("Appearance model missing: " + AppearanceNames[i]);
                option.model = child.gameObject;
                repaired++;
            }
            if (option.animator == null) { option.animator = option.model.GetComponent<Animator>(); repaired++; }
            if (option.face == null)
            {
                foreach (var renderer in option.model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null) continue;
                    for (int shape = 0; shape < renderer.sharedMesh.blendShapeCount; shape++)
                        if (renderer.sharedMesh.GetBlendShapeName(shape).EndsWith("AA_VI_00_Sil", StringComparison.Ordinal))
                        { option.face = renderer; break; }
                    if (option.face != null) break;
                }
                repaired++;
            }
            if (option.animator == null || option.face == null)
                throw new InvalidOperationException("Appearance rig/face missing: " + AppearanceNames[i]);
        }
        if (repaired > 0) ApplyAppearance();
        return repaired;
    }

    private void OnEnable() => refreshRequested = true;
    private void OnValidate() => refreshRequested = true;

    private void Update()
    {
        if (IsConfigured && (refreshRequested || appliedIndex != appearanceIndex))
            ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        if (!IsConfigured) return;
        appearanceIndex = Mathf.Clamp(appearanceIndex, 0, appearances.Length - 1);
        Appearance selected = appearances[appearanceIndex];
        if (selected.animator == null || selected.face == null
            || animationController == null || visemeController == null
            || blinkController == null)
        {
            Debug.LogError("[EVA] Appearance is not completely configured.", this);
            enabled = false;
            return;
        }

        // Keep the original model hierarchy/components intact. Only its rendering
        // and Animator are hidden; the lip-sync subscriptions stay on the host.
        // Each alternative has its own Animator root, so Generic bone paths work.
        visemeController.SetFaceRenderer(selected.face);
        blinkController.SetFaceRenderer(selected.face);
        for (int i = 1; i < appearances.Length; i++)
            appearances[i].model.SetActive(i == appearanceIndex);
        for (int i = 0; i < originalRenderers.Length; i++)
            if (originalRenderers[i] != null)
                originalRenderers[i].enabled = appearanceIndex == 0 && originalRendererEnabled[i];
        appearances[0].animator.enabled = appearanceIndex == 0;
        selected.animator.enabled = true;
        selected.animator.applyRootMotion = false;
        selected.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animationController.SetAnimator(selected.animator);
        appliedIndex = appearanceIndex;
        refreshRequested = false;
    }
}
