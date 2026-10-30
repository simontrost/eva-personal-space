using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-850), DisallowMultipleComponent]
public sealed class EVADesktopController : MonoBehaviour
{
    [Header("Desktop Movement")]
    [SerializeField, Min(0.1f)] private float movementSpeed = 2.5f;
    [SerializeField, Min(1f)] private float sprintMultiplier = 2f;
    [SerializeField, Range(0.01f, 1f)] private float mouseSensitivity = 0.12f;

    private Camera view;
    private EVASystemLauncher launcher;
    private EVAConversationDisplay conversation;
    private float yaw;
    private float pitch;
    private bool skipMouseDelta;
    public bool MouseCaptured => Cursor.lockState == CursorLockMode.Locked;
    public Camera View => view;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void EnsureInstanceExists()
    {
        if (!EVAControlSettings.UseVR && FindFirstObjectByType<EVADesktopController>() == null)
            new GameObject("EVA Desktop Controller").AddComponent<EVADesktopController>();
    }

    private void Awake()
    {
        view = Camera.main;
        launcher = FindFirstObjectByType<EVASystemLauncher>();
        if (view == null) { Debug.LogError("[EVA/Desktop] Main Camera is missing."); enabled = false; return; }
        yaw = view.transform.eulerAngles.y;
        pitch = Mathf.DeltaAngle(0f, view.transform.eulerAngles.x);
        if (!EVAControlSettings.UseVR) view.stereoTargetEye = StereoTargetEyeMask.None;
    }

    private void Update()
    {
        if (EVAControlSettings.UseVR || !Application.isFocused || EVASelectionMenu.BlocksInteraction
            || (launcher != null && !launcher.IsReady))
        { ReleaseMouse(); return; }
        Keyboard keyboard = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        { ReleaseMouse(); return; }
        if (!MouseCaptured)
        {
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                if (conversation == null) conversation = FindFirstObjectByType<EVAConversationDisplay>();
                if (!EVASelectionMenu.IsPointerOverMenuControls(mouse.position.ReadValue()) &&
                    (conversation == null || !conversation.IsPointerOverControls(mouse.position.ReadValue())))
                    CaptureMouse();
            }
            return;
        }
        Vector2 movement = ReadMovement(keyboard);
        Vector2 look = mouse != null && !skipMouseDelta ? mouse.delta.ReadValue() : Vector2.zero;
        skipMouseDelta = false;
        ApplyMotion(movement, look, keyboard != null &&
            (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed), Time.deltaTime);
    }

    public static Vector2 ReadMovement(Keyboard keyboard) => keyboard == null ? Vector2.zero
        : new Vector2((keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
            (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));

    // One motion path for actual input and automated movement checks.
    public void ApplyMotion(Vector2 movement, Vector2 mouseDelta, bool sprint, float deltaTime)
    {
        if (view == null || EVAControlSettings.UseVR) return;
        yaw = Mathf.Repeat(yaw + mouseDelta.x * mouseSensitivity, 360f);
        pitch = Mathf.Clamp(pitch - mouseDelta.y * mouseSensitivity, -85f, 85f);
        view.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector2 clamped = Vector2.ClampMagnitude(movement, 1f);
        Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * new Vector3(clamped.x, 0f, clamped.y);
        view.transform.position += direction * movementSpeed * (sprint ? sprintMultiplier : 1f) * deltaTime;
    }

    private void CaptureMouse()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        skipMouseDelta = true;
    }
    public void ReleaseMouse()
    {
        if (Cursor.lockState == CursorLockMode.Locked) Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
    private void OnApplicationFocus(bool focused) { if (!focused) ReleaseMouse(); }
    private void OnDisable() { if (!EVAControlSettings.UseVR) ReleaseMouse(); }

}
