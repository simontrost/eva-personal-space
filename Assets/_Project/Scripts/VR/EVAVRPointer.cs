using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR;

// Uses the existing OpenXR controller and uGUI events; A remains push-to-talk.
[DisallowMultipleComponent]
public sealed class EVAVRPointer : PointerInputModule
{
    public const float MaximumDistance = 8f;
    private readonly List<BaseInputModule> pausedModules = new();
    private readonly List<RaycastResult> hits = new();
    private Transform trackingSpace;
    private EVASystemLauncher launcher;
    private InputDevice controller;
    private ExtendedPointerEventData pointer;
    private GameObject visualRoot;
    private LineRenderer beam;
    private LineRenderer marker;
    private bool initialized;
    private bool wasPressed;
    private bool waitForRelease = true;
    private bool triggerLatched;

    public static EVAVRPointer Create(Transform origin, EVASystemLauncher systemLauncher)
    {
        if (!EVAControlSettings.UseVR || EventSystem.current == null) return null;
        var host = EventSystem.current.gameObject;
        var result = host.GetComponent<EVAVRPointer>();
        if (result == null) result = host.AddComponent<EVAVRPointer>();
        result.trackingSpace = origin;
        result.launcher = systemLauncher;
        result.pointer = new ExtendedPointerEventData(host.GetComponent<EventSystem>())
        {
            pointerId = -17, pointerType = UIPointerType.Tracked,
            button = PointerEventData.InputButton.Left
        };
        result.initialized = true;
        result.PauseOtherModules();
        result.CreateVisuals();
        return result;
    }

    public override bool IsModuleSupported() => EVAControlSettings.UseVR;
    public override bool ShouldActivateModule() => initialized && IsModuleSupported() && base.ShouldActivateModule();
    public override bool IsPointerOverGameObject(int pointerId) => pointer != null && pointer.pointerEnter != null;

    protected override void OnEnable()
    {
        base.OnEnable();
        if (initialized) PauseOtherModules();
    }

    private void PauseOtherModules()
    {
        foreach (var module in GetComponents<BaseInputModule>())
        {
            if (module == this || !module.enabled) continue;
            pausedModules.Add(module);
            module.enabled = false;
        }
    }

    public override void Process()
    {
        if (!initialized) return;
        bool tracked = TryReadRay(out Ray ray);
        bool held = ReadTrigger();
        var menu = EVASelectionMenu.Instance;
        bool ready = launcher != null && launcher.IsReady && (menu == null || menu.IsSystemReady);
        ProcessFrame(ray, tracked, held, ready);
    }

    private bool TryReadRay(out Ray ray)
    {
        ray = default;
        if (trackingSpace == null) return false;
        if (!controller.isValid) controller = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!controller.isValid || !controller.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked ||
            !controller.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) ||
            (state & (InputTrackingState.Position | InputTrackingState.Rotation)) != (InputTrackingState.Position | InputTrackingState.Rotation) ||
            !controller.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) ||
            !controller.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation)) return false;

        // Quest exposes an aim pose in addition to the grip pose. Match the ray to
        // the controller's pointing direction instead of the user's head direction.
        var device = UnityEngine.InputSystem.XR.XRController.rightHand;
        var aimPosition = device?.TryGetChildControl<Vector3Control>("pointerPosition");
        var aimRotation = device?.TryGetChildControl<QuaternionControl>("pointerRotation");
        if (aimPosition != null && aimRotation != null)
        {
            var aimTracked = device.TryGetChildControl<ButtonControl>("pointer/isTracked");
            if (aimTracked != null && !aimTracked.isPressed) return false;
            var aimState = device.TryGetChildControl<IntegerControl>("pointer/trackingState");
            if (aimState != null && (aimState.ReadValue() & 3) != 3) return false;
            position = aimPosition.ReadValue();
            rotation = aimRotation.ReadValue();
        }
        ray = new Ray(trackingSpace.TransformPoint(position), trackingSpace.rotation * rotation * Vector3.forward);
        return ray.direction.sqrMagnitude > 0.9f;
    }

    private bool ReadTrigger()
    {
        if (!controller.isValid) return false;
        if (controller.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed)) return pressed;
        if (!controller.TryGetFeatureValue(CommonUsages.trigger, out float value)) return false;
        triggerLatched = value >= (triggerLatched ? 0.35f : 0.65f);
        return triggerLatched;
    }

    private void ProcessFrame(Ray ray, bool tracked, bool held, bool ready)
    {
        if (!tracked || !ready || !EVAControlSettings.UseVR)
        {
            CancelPointer();
            SetVisualsVisible(false);
            return;
        }
        // A held trigger after loading, tracking loss or closing a menu must not click.
        if (pointer.pointerPress != null && !pointer.pointerPress.activeInHierarchy) CancelPointer();
        pointer.Reset();
        pointer.trackedDevicePosition = ray.origin;
        pointer.trackedDeviceOrientation = Quaternion.LookRotation(ray.direction);
        hits.Clear();
        eventSystem.RaycastAll(pointer, hits);
        RaycastResult hit = default;
        foreach (var candidate in hits)
        {
            // Ignore screen-space raycasts and non-interactive labels/decorations.
            var graphic = candidate.gameObject != null ? candidate.gameObject.GetComponent<Graphic>() : null;
            if (candidate.module is TrackedDeviceRaycaster && candidate.distance <= MaximumDistance &&
                graphic != null && graphic.isActiveAndEnabled && graphic.raycastTarget && !graphic.canvasRenderer.cull)
            { hit = candidate; break; }
        }
        Vector2 position = hit.isValid ? hit.screenPosition : pointer.position;
        pointer.delta = position - pointer.position;
        pointer.position = position;
        pointer.pointerCurrentRaycast = hit;
        HandlePointerExitAndEnter(pointer, hit.gameObject);
        var selectable = hit.gameObject != null ? hit.gameObject.GetComponentInParent<Selectable>() : null;
        bool actionable = selectable != null && selectable.IsActive() && selectable.IsInteractable();
        DrawRay(ray, hit, actionable);

        if (waitForRelease)
        {
            if (!held) waitForRelease = false;
            wasPressed = held;
            return;
        }
        if (held && !wasPressed)
        {
            pointer.eligibleForClick = true;
            pointer.dragging = false;
            pointer.pressPosition = pointer.position;
            pointer.clickCount = 1;
            pointer.clickTime = Time.unscaledTime;
            pointer.pointerPressRaycast = hit;
            DeselectIfSelectionChanged(hit.gameObject, pointer);
            var pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            pointer.pointerPress = pressed != null ? pressed : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            pointer.rawPointerPress = hit.gameObject;
            pointer.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
            if (pointer.pointerDrag != null) ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.initializePotentialDrag);
        }
        if (held) ProcessDrag(pointer);
        if (!held && wasPressed)
        {
            var pressed = pointer.pointerPress;
            ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerUpHandler);
            if (pointer.eligibleForClick && pressed != null && pressed == ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject))
                ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerClickHandler);
            else if (pointer.dragging) ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.dropHandler);
            ClearPress();
        }
        wasPressed = held;
    }

    private void ClearPress()
    {
        if (pointer.dragging) ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.endDragHandler);
        pointer.eligibleForClick = false;
        pointer.dragging = false;
        pointer.pointerPress = pointer.rawPointerPress = pointer.pointerDrag = null;
    }

    private void CancelPointer()
    {
        if (pointer != null)
        {
            ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
            ClearPress();
            HandlePointerExitAndEnter(pointer, null);
            pointer.pointerCurrentRaycast = default;
        }
        wasPressed = false;
        waitForRelease = true;
        triggerLatched = false;
    }

    private void CreateVisuals()
    {
        if (visualRoot != null) return;
        visualRoot = new GameObject("EVA Right Controller Laser");
        visualRoot.transform.SetParent(transform, false);
        var material = Resources.Load<Material>("EVAVRPointer");
        if (material == null) { Debug.LogError("[EVA/VR] Laser material is missing."); return; }
        beam = MakeLine("Beam", material, false, 2, 0.003f);
        marker = MakeLine("Hit marker", material, true, 20, 0.002f);
        SetVisualsVisible(false);
    }

    private LineRenderer MakeLine(string name, Material material, bool loop, int count, float width)
    {
        var host = new GameObject(name);
        host.transform.SetParent(visualRoot.transform, false);
        var line = host.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.loop = loop;
        line.positionCount = count;
        line.startWidth = line.endWidth = width;
        line.numCapVertices = 4;
        // World-space uGUI is transparent and sorted above ordinary geometry.
        // Draw the hit marker after the UI while still depth-testing the scene.
        line.sortingOrder = 4000;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    private void DrawRay(Ray ray, RaycastResult hit, bool actionable)
    {
        if (beam == null || marker == null) return;
        Color tint = actionable ? new Color(0.45f, 1f, 0.85f) : new Color(0.25f, 0.72f, 1f, 0.8f);
        beam.enabled = true;
        beam.startColor = beam.endColor = tint;
        float distance = hit.isValid ? hit.distance : MaximumDistance;
        if (!hit.isValid && Physics.Raycast(ray, out RaycastHit obstacle, distance, ~0, QueryTriggerInteraction.Ignore)) distance = obstacle.distance;
        beam.SetPosition(0, ray.GetPoint(0.03f));
        beam.SetPosition(1, ray.GetPoint(Mathf.Max(0.03f, distance - 0.002f)));
        marker.enabled = hit.isValid;
        marker.startColor = marker.endColor = tint;
        var rotation = Quaternion.LookRotation(-ray.direction);
        for (int i = 0; i < marker.positionCount && hit.isValid; i++)
        {
            float angle = i * Mathf.PI * 2f / marker.positionCount;
            marker.SetPosition(i, ray.GetPoint(distance - 0.003f) + rotation * new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.008f);
        }
    }

    private void SetVisualsVisible(bool visible)
    {
        if (beam != null) beam.enabled = visible;
        if (marker != null) marker.enabled = visible;
    }

    public override void DeactivateModule() { CancelPointer(); base.DeactivateModule(); }

    protected override void OnDisable()
    {
        CancelPointer();
        SetVisualsVisible(false);
        foreach (var module in pausedModules) if (module != null) module.enabled = true;
        pausedModules.Clear();
        base.OnDisable();
    }

    protected override void OnDestroy()
    {
        if (visualRoot != null) Destroy(visualRoot);
        base.OnDestroy();
    }
}
