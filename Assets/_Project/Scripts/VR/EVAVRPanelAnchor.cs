using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;

public enum EVAVRPanelKind { Menu, Conversation, Loading }

[DisallowMultipleComponent, RequireComponent(typeof(Canvas))]
public sealed class EVAVRPanelAnchor : MonoBehaviour
{
    [SerializeField] private EVAVRPanelKind panelKind;
    [Tooltip("Optional fixed avatar/station anchor. Empty uses the selected EVA.")]
    [SerializeField] private Transform anchorOverride;
    private Camera viewer;
    private Canvas panel;
    private bool configured;
    private bool placed;
    private Transform lastAvatar;

    public static EVAVRPanelAnchor Configure(Canvas canvas, EVAVRPanelKind kind)
    {
        if (!EVAControlSettings.UseVR) return null;
        var anchor = canvas.GetComponent<EVAVRPanelAnchor>();
        if (anchor == null) anchor = canvas.gameObject.AddComponent<EVAVRPanelAnchor>();
        anchor.panel = canvas;
        anchor.panelKind = kind;
        anchor.configured = true;
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.scaleFactor = 1f;
        var screenRaycaster = canvas.GetComponent<GraphicRaycaster>();
        if (screenRaycaster != null) screenRaycaster.enabled = false;
        var raycaster = canvas.GetComponent<TrackedDeviceRaycaster>();
        if (raycaster == null) raycaster = canvas.gameObject.AddComponent<TrackedDeviceRaycaster>();
        raycaster.maxDistance = EVAVRPointer.MaximumDistance;
        raycaster.ignoreReversedGraphics = true;
        raycaster.checkFor3DOcclusion = true;
        raycaster.blockingMask = ~0;
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null) scaler.enabled = false;
        var rect = (RectTransform)canvas.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = kind == EVAVRPanelKind.Menu ? new Vector2(720, 980) :
            kind == EVAVRPanelKind.Conversation ? new Vector2(1100, 500) : new Vector2(960, 540);
        float width = kind == EVAVRPanelKind.Menu ? EVAControlSettings.VRMenuWidth :
            kind == EVAVRPanelKind.Conversation ? EVAControlSettings.VRTextWidth : 1.4f;
        rect.localScale = Vector3.one * (width / rect.sizeDelta.x);
        anchor.Recenter();
        return anchor;
    }

    private void OnEnable() { if (configured) Recenter(); }

    private Transform ResolveAvatar()
    {
        if (anchorOverride != null) return anchorOverride;
        var menu = EVASelectionMenu.Instance;
        var avatar = menu != null ? menu.PrimaryAvatar : null;
        if (avatar == null) avatar = EVASelectionMenu.FindDefaultAvatar();
        return avatar != null ? avatar.transform : null;
    }

    public void Recenter()
    {
        if (!configured || !EVAControlSettings.UseVR) return;
        viewer = Camera.main;
        if (viewer == null) { placed = false; return; }
        if (panel == null) panel = GetComponent<Canvas>();
        panel.worldCamera = viewer;
        lastAvatar = ResolveAvatar();
        float width = panelKind == EVAVRPanelKind.Menu ? EVAControlSettings.VRMenuWidth :
            panelKind == EVAVRPanelKind.Conversation ? EVAControlSettings.VRTextWidth : 1.4f;
        var rect = (RectTransform)panel.transform;
        Vector3 parentScale = rect.parent != null ? rect.parent.lossyScale : Vector3.one;
        float unitsPerPixel = width / rect.sizeDelta.x;
        rect.localScale = new Vector3(unitsPerPixel / Mathf.Max(0.001f, Mathf.Abs(parentScale.x)),
            unitsPerPixel / Mathf.Max(0.001f, Mathf.Abs(parentScale.y)),
            unitsPerPixel / Mathf.Max(0.001f, Mathf.Abs(parentScale.z)));
        float outerEdge = width * 0.5f + (panelKind == EVAVRPanelKind.Menu ? EVAControlSettings.VRMenuSideOffset : 0f);
        float distance = Mathf.Max(EVAControlSettings.VRPanelDistance, outerEdge / Mathf.Tan(35f * Mathf.Deg2Rad));
        transform.position = CalculatePosition(viewer.transform.position, viewer.transform.forward,
            lastAvatar != null ? lastAvatar.position : (Vector3?)null, panelKind,
            distance, EVAControlSettings.VRMenuSideOffset,
            EVAControlSettings.VRTextVerticalOffset);
        placed = true;
        FaceViewer();
    }

    public static Vector3 CalculatePosition(Vector3 eye, Vector3 forward, Vector3? avatar,
        EVAVRPanelKind kind, float minimumDistance, float sideOffset, float textDrop)
    {
        Vector3 direction = kind != EVAVRPanelKind.Loading && avatar.HasValue
            ? avatar.Value - eye : forward;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f) direction = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (direction.sqrMagnitude < 0.001f) direction = Vector3.forward;
        direction.Normalize();
        float distance = Mathf.Clamp(minimumDistance, 1.5f, 4f);
        if (avatar.HasValue && kind != EVAVRPanelKind.Loading)
        {
            float avatarDistance = Vector3.ProjectOnPlane(avatar.Value - eye, Vector3.up).magnitude;
            distance = Mathf.Clamp(avatarDistance - 0.6f, distance, distance + 1f);
        }
        Vector3 position = eye + direction * distance;
        if (kind == EVAVRPanelKind.Menu)
            position -= Vector3.Cross(Vector3.up, direction) * Mathf.Clamp(sideOffset, 0.5f, 1.2f);
        position.y = eye.y - (kind == EVAVRPanelKind.Conversation ? Mathf.Clamp(textDrop, 0.1f, 0.8f) : 0.08f);
        float floor = avatar.HasValue ? avatar.Value.y : 0f;
        position.y = Mathf.Max(position.y, floor + (kind == EVAVRPanelKind.Menu ? 0.9f : 0.65f));
        return position;
    }

    private void LateUpdate()
    {
        if (!configured || !EVAControlSettings.UseVR) return;
        var camera = Camera.main;
        var avatar = ResolveAvatar();
        if (!placed || camera != viewer || avatar != lastAvatar) Recenter();
        FaceViewer();
    }

    private void FaceViewer()
    {
        if (viewer == null) return;
        Vector3 normal = transform.position - viewer.transform.position;
        if (normal.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(normal, Vector3.up);
    }

    public static void RecenterAll()
    {
        foreach (var anchor in FindObjectsByType<EVAVRPanelAnchor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            anchor.Recenter();
    }
}
