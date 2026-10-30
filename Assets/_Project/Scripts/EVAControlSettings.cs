using UnityEngine;

public enum EVAControlMode { Desktop, VR }

[CreateAssetMenu(menuName = "EVA/Control Settings")]
public sealed class EVAControlSettings : ScriptableObject
{
    [Tooltip("Desktop uses WASD/mouse without starting OpenXR. VR uses Quest Link/Air Link.")]
    public EVAControlMode mode = EVAControlMode.Desktop;

    [Header("VR panels (metres)")]
    [Range(1.5f, 4f)] public float vrPanelDistance = 2f;
    [Range(0.7f, 1.4f)] public float vrMenuWidth = 1f;
    [Range(1.1f, 2.2f)] public float vrTextWidth = 1.6f;
    [Range(0.5f, 1.2f)] public float vrMenuSideOffset = 0.8f;
    [Range(0.1f, 0.8f)] public float vrTextVerticalOffset = 0.45f;
    public bool vrShowConversationText = true;

    private static EVAControlSettings cached;
    public static EVAControlMode CurrentMode
    {
        get
        {
            if (cached == null) cached = Resources.Load<EVAControlSettings>("EVAControlSettings");
            return cached != null ? cached.mode : EVAControlMode.Desktop;
        }
    }
    public static bool UseVR => CurrentMode == EVAControlMode.VR;
    public static float VRPanelDistance { get { _ = CurrentMode; return cached != null ? Mathf.Clamp(cached.vrPanelDistance, 1.5f, 4f) : 2f; } }
    public static float VRMenuWidth { get { _ = CurrentMode; return cached != null ? Mathf.Clamp(cached.vrMenuWidth, 0.7f, 1.4f) : 1f; } }
    public static float VRTextWidth { get { _ = CurrentMode; return cached != null ? Mathf.Clamp(cached.vrTextWidth, 1.1f, 2.2f) : 1.6f; } }
    public static float VRMenuSideOffset { get { _ = CurrentMode; return cached != null ? Mathf.Clamp(cached.vrMenuSideOffset, 0.5f, 1.2f) : 0.8f; } }
    public static float VRTextVerticalOffset { get { _ = CurrentMode; return cached != null ? Mathf.Clamp(cached.vrTextVerticalOffset, 0.1f, 0.8f) : 0.45f; } }
    public static bool VRShowConversationText { get { _ = CurrentMode; return cached == null || cached.vrShowConversationText; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetCache() => cached = null;
}
