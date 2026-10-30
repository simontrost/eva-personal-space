using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR;

[DisallowMultipleComponent]
public sealed class EVAVRPointer : PointerInputModule
{
    public const float MaximumDistance = 8f;
    private readonly List<BaseInputModule> pausedModules = new();
    private readonly List<RaycastResult> hits = new();
    private Transform trackingSpace;
    private EVASystemLauncher launcher;
    private sealed class HandPointer
    {
        public XRNode node;
        public string name;
        public InputDevice controller;
        public ExtendedPointerEventData pointer;
        public GameObject visualRoot;
        public LineRenderer beam;
        public LineRenderer marker;
        public bool wasPressed;
        public bool waitForRelease = true;
        public bool triggerLatched;
        public string poseSource;
        public string lastStatus;
    }

    private HandPointer right;
    private HandPointer left;
    private bool initialized;

    public static EVAVRPointer Create(Transform origin, EVASystemLauncher systemLauncher)
    {
        if (!EVAControlSettings.UseVR || EventSystem.current == null) return null;
        var host = EventSystem.current.gameObject;
        var result = host.GetComponent<EVAVRPointer>();
        if (result == null) result = host.AddComponent<EVAVRPointer>();
        result.trackingSpace = origin;
        result.launcher = systemLauncher;
        result.right ??= result.CreateHand(XRNode.RightHand, "Right", -17);
        result.left ??= result.CreateHand(XRNode.LeftHand, "Left", -18);
        result.initialized = true;
        result.enabled = true;
        result.PauseOtherModules();
        result.CreateVisuals(result.right);
        result.CreateVisuals(result.left);
        host.GetComponent<EventSystem>().UpdateModules();
        Debug.Log("[EVA/VR] Both controller lasers initialized; use either trigger to click.");
        return result;
    }

    public override bool IsModuleSupported() => EVAControlSettings.UseVR;
    public override bool ShouldActivateModule() => initialized && IsModuleSupported() && base.ShouldActivateModule();
    public override bool IsPointerOverGameObject(int pointerId)
    {
        return IsOver(right, pointerId) || IsOver(left, pointerId);
    }

    private static bool IsOver(HandPointer hand, int pointerId) => hand != null &&
        (pointerId == -1 || pointerId == hand.pointer.pointerId) && hand.pointer.pointerEnter != null;

    private HandPointer CreateHand(XRNode node, string name, int pointerId) => new HandPointer
    {
        node = node, name = name,
        pointer = new ExtendedPointerEventData(eventSystem)
        {
            pointerId = pointerId, pointerType = UIPointerType.Tracked,
            button = PointerEventData.InputButton.Left
        }
    };

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
        var menu = EVASelectionMenu.Instance;
        bool ready = launcher != null && launcher.IsReady && (menu == null || menu.IsSystemReady);
        ProcessHand(right, ready);
        ProcessHand(left, ready);
    }

    private void ProcessHand(HandPointer hand, bool ready)
    {
        bool tracked = TryReadRay(hand, out Ray ray);
        bool held = ReadTrigger(hand);
        string status = !ready ? "Waiting for EVA startup" : !tracked ? "Waiting for controller tracking" : hand.poseSource;
        if (status != hand.lastStatus)
        {
            hand.lastStatus = status;
            Debug.Log("[EVA/VR] " + hand.name + " laser: " + status);
        }
        ProcessFrame(hand, ray, tracked, held, ready);
    }

    private bool TryReadRay(HandPointer hand, out Ray ray)
    {
        ray = default;
        if (trackingSpace == null) return false;

        var device = (hand.node == XRNode.RightHand
            ? UnityEngine.InputSystem.XR.XRController.rightHand : UnityEngine.InputSystem.XR.XRController.leftHand);
        if (device != null && device.added)
        {
            if (TryReadPose(device, "pointer/position", "pointer/rotation",
                "pointer/isTracked", "pointer/trackingState", out Vector3 position, out Quaternion rotation))
                return SetRay(hand, position, rotation, "OpenXR aim pose", out ray);
            if (TryReadPose(device, "devicePosition", "deviceRotation",
                "isTracked", "trackingState", out position, out rotation))
                return SetRay(hand, position, rotation, "OpenXR grip pose", out ray);
        }

        if (!hand.controller.isValid) hand.controller = InputDevices.GetDeviceAtXRNode(hand.node);
        if (!hand.controller.isValid) return false;
        bool hasTracked = hand.controller.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked);
        bool hasState = hand.controller.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state);
        var required = InputTrackingState.Position | InputTrackingState.Rotation;
        if ((hasTracked && !tracked) || (hasState && (state & required) != required) || (!hasTracked && !hasState)) return false;
        if (!hand.controller.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 legacyPosition) ||
            !hand.controller.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion legacyRotation)) return false;
        return SetRay(hand, legacyPosition, legacyRotation, "Unity XR grip pose", out ray);
    }

    private static bool TryReadPose(UnityEngine.InputSystem.InputDevice device,
        string positionPath, string rotationPath, string trackedPath, string statePath,
        out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;
        var positionControl = device.TryGetChildControl<Vector3Control>(positionPath);
        var rotationControl = device.TryGetChildControl<QuaternionControl>(rotationPath);
        var trackedControl = device.TryGetChildControl<ButtonControl>(trackedPath);
        var stateControl = device.TryGetChildControl<IntegerControl>(statePath);
        if (positionControl == null || rotationControl == null ||
            (trackedControl == null && stateControl == null) ||
            // Pose booleans may be BYTE value 1, normalized to 1/255. A normal
            // button press threshold would incorrectly treat tracked=true as false.
            (trackedControl != null && !(trackedControl.ReadValue() > 0f)) ||
            (stateControl != null && (stateControl.ReadValue() & 3) != 3)) return false;
        position = positionControl.ReadValue();
        rotation = rotationControl.ReadValue();
        return IsFinite(position.x) && IsFinite(position.y) && IsFinite(position.z) &&
            IsFinite(rotation.x) && IsFinite(rotation.y) && IsFinite(rotation.z) && IsFinite(rotation.w) &&
            Quaternion.Dot(rotation, rotation) > 0.25f;
    }

    private bool SetRay(HandPointer hand, Vector3 position, Quaternion rotation, string source, out Ray ray)
    {
        ray = default;
        if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) ||
            !IsFinite(rotation.x) || !IsFinite(rotation.y) || !IsFinite(rotation.z) || !IsFinite(rotation.w) ||
            Quaternion.Dot(rotation, rotation) <= 0.25f) return false;
        ray = new Ray(trackingSpace.TransformPoint(position), trackingSpace.rotation * rotation.normalized * Vector3.forward);
        hand.poseSource = source;
        return true;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private bool ReadTrigger(HandPointer hand)
    {
        bool pressed = false;
        float value = 0f;
        var device = (hand.node == XRNode.RightHand
            ? UnityEngine.InputSystem.XR.XRController.rightHand : UnityEngine.InputSystem.XR.XRController.leftHand);
        if (device != null && device.added)
        {
            var button = device.TryGetChildControl<ButtonControl>("triggerPressed");
            var axis = device.TryGetChildControl<AxisControl>("trigger");
            pressed = button != null && button.isPressed;
            if (axis != null) value = axis.ReadValue();
        }
        if (!hand.controller.isValid) hand.controller = InputDevices.GetDeviceAtXRNode(hand.node);
        if (hand.controller.isValid)
        {
            if (hand.controller.TryGetFeatureValue(CommonUsages.triggerButton, out bool legacyPressed)) pressed |= legacyPressed;
            if (hand.controller.TryGetFeatureValue(CommonUsages.trigger, out float legacyValue)) value = Mathf.Max(value, legacyValue);
        }
        hand.triggerLatched = pressed || value >= (hand.triggerLatched ? 0.35f : 0.65f);
        return hand.triggerLatched;
    }

    private void ProcessFrame(HandPointer hand, Ray ray, bool tracked, bool held, bool ready)
    {
        if (!tracked || !ready || !EVAControlSettings.UseVR)
        {
            CancelPointer(hand);
            SetVisualsVisible(hand, false);
            return;
        }
        if (hand.pointer.pointerPress != null && !hand.pointer.pointerPress.activeInHierarchy) CancelPointer(hand);
        hand.pointer.Reset();
        hand.pointer.trackedDevicePosition = ray.origin;
        hand.pointer.trackedDeviceOrientation = Quaternion.LookRotation(ray.direction);
        hits.Clear();
        eventSystem.RaycastAll(hand.pointer, hits);
        RaycastResult hit = default;
        foreach (var candidate in hits)
        {
            var graphic = candidate.gameObject != null ? candidate.gameObject.GetComponent<Graphic>() : null;
            if (candidate.module is TrackedDeviceRaycaster && candidate.distance <= MaximumDistance &&
                graphic != null && graphic.isActiveAndEnabled && graphic.raycastTarget && !graphic.canvasRenderer.cull)
            { hit = candidate; break; }
        }
        Vector2 position = hit.isValid ? hit.screenPosition : hand.pointer.position;
        hand.pointer.delta = position - hand.pointer.position;
        hand.pointer.position = position;
        hand.pointer.pointerCurrentRaycast = hit;
        HandlePointerExitAndEnter(hand.pointer, hit.gameObject);
        var selectable = hit.gameObject != null ? hit.gameObject.GetComponentInParent<Selectable>() : null;
        bool actionable = selectable != null && selectable.IsActive() && selectable.IsInteractable();
        DrawRay(hand, ray, hit, actionable);

        if (hand.waitForRelease)
        {
            if (!held) hand.waitForRelease = false;
            hand.wasPressed = held;
            return;
        }
        if (held && !hand.wasPressed)
        {
            hand.pointer.eligibleForClick = true;
            hand.pointer.dragging = false;
            hand.pointer.pressPosition = hand.pointer.position;
            hand.pointer.clickCount = 1;
            hand.pointer.clickTime = Time.unscaledTime;
            hand.pointer.pointerPressRaycast = hit;
            DeselectIfSelectionChanged(hit.gameObject, hand.pointer);
            var pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, hand.pointer, ExecuteEvents.pointerDownHandler);
            hand.pointer.pointerPress = pressed != null ? pressed : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            hand.pointer.rawPointerPress = hit.gameObject;
            hand.pointer.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
            if (hand.pointer.pointerDrag != null) ExecuteEvents.Execute(hand.pointer.pointerDrag, hand.pointer, ExecuteEvents.initializePotentialDrag);
        }
        if (held) ProcessDrag(hand.pointer);
        if (!held && hand.wasPressed)
        {
            var pressed = hand.pointer.pointerPress;
            ExecuteEvents.Execute(pressed, hand.pointer, ExecuteEvents.pointerUpHandler);
            if (hand.pointer.eligibleForClick && pressed != null && pressed == ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject))
                ExecuteEvents.Execute(pressed, hand.pointer, ExecuteEvents.pointerClickHandler);
            else if (hand.pointer.dragging) ExecuteEvents.ExecuteHierarchy(hit.gameObject, hand.pointer, ExecuteEvents.dropHandler);
            ClearPress(hand);
        }
        hand.wasPressed = held;
    }

    private void ClearPress(HandPointer hand)
    {
        if (hand.pointer.dragging) ExecuteEvents.Execute(hand.pointer.pointerDrag, hand.pointer, ExecuteEvents.endDragHandler);
        hand.pointer.eligibleForClick = false;
        hand.pointer.dragging = false;
        hand.pointer.pointerPress = hand.pointer.rawPointerPress = hand.pointer.pointerDrag = null;
    }

    private void CancelPointer(HandPointer hand)
    {
        if (hand == null) return;
        if (hand.pointer != null)
        {
            ExecuteEvents.Execute(hand.pointer.pointerPress, hand.pointer, ExecuteEvents.pointerUpHandler);
            ClearPress(hand);
            HandlePointerExitAndEnter(hand.pointer, null);
            hand.pointer.pointerCurrentRaycast = default;
        }
        hand.wasPressed = false;
        hand.waitForRelease = true;
        hand.triggerLatched = false;
    }

    private void CreateVisuals(HandPointer hand)
    {
        if (hand.visualRoot != null) return;
        hand.visualRoot = new GameObject("EVA " + hand.name + " Controller Laser");
        hand.visualRoot.transform.SetParent(transform, false);
        var material = Resources.Load<Material>("EVAVRPointer");
        if (material == null) { Debug.LogError("[EVA/VR] Laser material is missing."); return; }
        hand.beam = MakeLine(hand, "Beam", material, false, 2, 0.003f);
        hand.marker = MakeLine(hand, "Hit marker", material, true, 20, 0.002f);
        SetVisualsVisible(hand, false);
    }

    private LineRenderer MakeLine(HandPointer hand, string name, Material material, bool loop, int count, float width)
    {
        var host = new GameObject(name);
        host.transform.SetParent(hand.visualRoot.transform, false);
        var line = host.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.loop = loop;
        line.positionCount = count;
        line.startWidth = line.endWidth = width;
        line.numCapVertices = 4;
        line.sortingOrder = 4000;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    private void DrawRay(HandPointer hand, Ray ray, RaycastResult hit, bool actionable)
    {
        if (hand.beam == null || hand.marker == null) return;
        Color tint = actionable ? new Color(0.45f, 1f, 0.85f) : new Color(0.25f, 0.72f, 1f, 0.8f);
        hand.beam.enabled = true;
        hand.beam.startColor = hand.beam.endColor = tint;
        float distance = hit.isValid ? hit.distance : MaximumDistance;
        if (!hit.isValid && Physics.Raycast(ray, out RaycastHit obstacle, distance, ~0, QueryTriggerInteraction.Ignore)) distance = obstacle.distance;
        hand.beam.SetPosition(0, ray.GetPoint(0.03f));
        hand.beam.SetPosition(1, ray.GetPoint(Mathf.Max(0.03f, distance - 0.002f)));
        hand.marker.enabled = hit.isValid;
        hand.marker.startColor = hand.marker.endColor = tint;
        var rotation = Quaternion.LookRotation(-ray.direction);
        for (int i = 0; i < hand.marker.positionCount && hit.isValid; i++)
        {
            float angle = i * Mathf.PI * 2f / hand.marker.positionCount;
            hand.marker.SetPosition(i, ray.GetPoint(distance - 0.003f) + rotation * new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.008f);
        }
    }

    private void SetVisualsVisible(HandPointer hand, bool visible)
    {
        if (hand == null) return;
        if (hand.beam != null) hand.beam.enabled = visible;
        if (hand.marker != null) hand.marker.enabled = visible;
    }

    public override void DeactivateModule()
    {
        CancelPointer(right);
        CancelPointer(left);
        base.DeactivateModule();
    }

    protected override void OnDisable()
    {
        CancelPointer(right);
        CancelPointer(left);
        SetVisualsVisible(right, false);
        SetVisualsVisible(left, false);
        foreach (var module in pausedModules) if (module != null) module.enabled = true;
        pausedModules.Clear();
        base.OnDisable();
    }

    protected override void OnDestroy()
    {
        if (right?.visualRoot != null) Destroy(right.visualRoot);
        if (left?.visualRoot != null) Destroy(left.visualRoot);
        base.OnDestroy();
    }
}
