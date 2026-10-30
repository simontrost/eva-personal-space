using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;

[DefaultExecutionOrder(-900)]
[DisallowMultipleComponent]
public sealed class EVAVRController : MonoBehaviour
{
    [Header("Locomotion")]
    [SerializeField]
    [Min(0.1f)]
    private float movementSpeed = 1.75f;

    [SerializeField]
    [Range(15f, 90f)]
    private float snapTurnDegrees = 30f;

    [SerializeField]
    [Range(0.1f, 0.95f)]
    private float inputDeadZone = 0.2f;

    [SerializeField]
    [Range(0.2f, 0.95f)]
    private float snapTurnThreshold = 0.7f;

    [Header("Tracking")]
    [SerializeField]
    [Range(1f, 2.2f)]
    private float deviceModeCameraHeight = 1.65f;

    private readonly List<XRDisplaySubsystem> displaySubsystems = new();
    private readonly List<XRInputSubsystem> inputSubsystems = new();

    private Camera xrCamera;
    private Transform xrOrigin;
    private Transform cameraOffset;
    private InputDevice headDevice;
    private InputDevice leftController;
    private InputDevice rightController;
    private bool rigInitialized;
    private bool snapTurnLatched;
    private bool textToggleHeld;
    private bool recenterHeld;
    private XRManagerSettings managedXR;
    private EVASystemLauncher launcher;
    private EVAVRPointer menuPointer;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad
    )]
    private static void EnsureInstanceExists()
    {
        if (
            FindFirstObjectByType<EVAVRController>(
                FindObjectsInactive.Include
            ) != null
        )
        {
            return;
        }

        GameObject host = new GameObject("EVA VR Controller");
        host.AddComponent<EVAVRController>();
    }

    private IEnumerator Start()
    {
        launcher = FindFirstObjectByType<EVASystemLauncher>();
        if (!EVAControlSettings.UseVR) yield break;
        var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager == null) { Debug.LogError("[EVA/VR] OpenXR settings are missing."); yield break; }
        if (manager.activeLoader == null)
        {
            yield return manager.InitializeLoader();
            if (manager.activeLoader == null)
            { Debug.LogError("[EVA/VR] OpenXR could not start. Start Quest Link, or select EVA > Controls > Desktop."); yield break; }
            managedXR = manager;
            manager.StartSubsystems();
        }
        while (!IsXRDisplayRunning())
        {
            yield return null;
        }

        InitializeRig();
    }

    private void OnEnable()
    {
        Application.onBeforeRender += UpdateHeadPose;
        if (menuPointer != null) menuPointer.enabled = true;
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= UpdateHeadPose;
        if (menuPointer != null) menuPointer.enabled = false;
    }

    private void OnDestroy()
    {
        if (managedXR == null) return;
        managedXR.StopSubsystems();
        managedXR.DeinitializeLoader();
    }

    private void Update()
    {
        if (!rigInitialized)
        {
            return;
        }

        UpdateHeadPose();
        UpdateLocomotion();

        UpdatePanelControls();
    }

    private bool IsXRDisplayRunning()
    {
        displaySubsystems.Clear();
        SubsystemManager.GetSubsystems(displaySubsystems);

        foreach (XRDisplaySubsystem subsystem in displaySubsystems)
        {
            if (subsystem != null && subsystem.running)
            {
                return true;
            }
        }

        return false;
    }

    private void InitializeRig()
    {
        xrCamera = Camera.main;

        if (xrCamera == null)
        {
            Debug.LogError(
                "[EVA/VR] The Main Camera could not be found."
            );
            return;
        }

        Vector3 initialCameraPosition = xrCamera.transform.position;
        float initialYaw = xrCamera.transform.eulerAngles.y;

        GameObject originObject = new GameObject("EVA XR Origin");
        xrOrigin = originObject.transform;
        xrOrigin.SetPositionAndRotation(
            new Vector3(
                initialCameraPosition.x,
                0f,
                initialCameraPosition.z
            ),
            Quaternion.Euler(0f, initialYaw, 0f)
        );

        GameObject offsetObject = new GameObject("Camera Offset");
        cameraOffset = offsetObject.transform;
        cameraOffset.SetParent(xrOrigin, false);

        bool floorTrackingEnabled = ConfigureTrackingOrigin();

        if (!floorTrackingEnabled)
        {
            cameraOffset.localPosition = new Vector3(
                0f,
                deviceModeCameraHeight,
                0f
            );
        }

        xrCamera.transform.SetParent(cameraOffset, false);
        xrCamera.transform.localPosition = Vector3.zero;
        xrCamera.transform.localRotation = Quaternion.identity;
        xrCamera.stereoTargetEye = StereoTargetEyeMask.Both;

        RefreshInputDevices();
        UpdateHeadPose();
        EVAVRPanelAnchor.RecenterAll();
        rigInitialized = true;
        menuPointer = EVAVRPointer.Create(cameraOffset, launcher);

        Debug.Log(
            "[EVA/VR] VR rig ready. Left stick moves, right " +
            "stick snap-turns, and holding A on the right " +
            "controller activates push-to-talk. Point with the right " +
            "controller and click with its trigger; B opens/closes settings."
        );
    }

    private bool ConfigureTrackingOrigin()
    {
        inputSubsystems.Clear();
        SubsystemManager.GetSubsystems(inputSubsystems);

        foreach (XRInputSubsystem subsystem in inputSubsystems)
        {
            if (subsystem == null || !subsystem.running)
            {
                continue;
            }

            TrackingOriginModeFlags supportedModes =
                subsystem.GetSupportedTrackingOriginModes();

            if (
                (supportedModes & TrackingOriginModeFlags.Floor)
                != TrackingOriginModeFlags.Unknown
                && subsystem.TrySetTrackingOriginMode(
                    TrackingOriginModeFlags.Floor
                )
            )
            {
                subsystem.TryRecenter();
                return true;
            }

            subsystem.TrySetTrackingOriginMode(
                TrackingOriginModeFlags.Device
            );
            subsystem.TryRecenter();
        }

        return false;
    }

    private void RefreshInputDevices()
    {
        headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        leftController = InputDevices.GetDeviceAtXRNode(
            XRNode.LeftHand
        );
        rightController = InputDevices.GetDeviceAtXRNode(
            XRNode.RightHand
        );
    }

    private void UpdateHeadPose()
    {
        if (!rigInitialized || xrCamera == null)
        {
            return;
        }

        if (!headDevice.isValid)
        {
            headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        }

        if (
            headDevice.TryGetFeatureValue(
                CommonUsages.devicePosition,
                out Vector3 position
            )
        )
        {
            xrCamera.transform.localPosition = position;
        }

        if (
            headDevice.TryGetFeatureValue(
                CommonUsages.deviceRotation,
                out Quaternion rotation
            )
        )
        {
            xrCamera.transform.localRotation = rotation;
        }
    }

    private void UpdateLocomotion()
    {
        if (EVASelectionMenu.BlocksInteraction || (launcher != null && !launcher.IsReady))
        {
            snapTurnLatched = true;
            return;
        }
        if (!leftController.isValid || !rightController.isValid)
        {
            RefreshInputDevices();
        }

        Vector2 movementInput = ReadPrimaryAxis(leftController);

        if (movementInput.sqrMagnitude >= inputDeadZone * inputDeadZone)
        {
            Vector3 forward = Vector3.ProjectOnPlane(
                xrCamera.transform.forward,
                Vector3.up
            ).normalized;

            Vector3 right = Vector3.ProjectOnPlane(
                xrCamera.transform.right,
                Vector3.up
            ).normalized;

            Vector3 direction =
                forward * movementInput.y
                + right * movementInput.x;

            xrOrigin.position +=
                direction * movementSpeed * Time.deltaTime;
        }

        Vector2 turnInput = ReadPrimaryAxis(rightController);

        if (
            !snapTurnLatched
            && Mathf.Abs(turnInput.x) >= snapTurnThreshold
        )
        {
            float angle = Mathf.Sign(turnInput.x) * snapTurnDegrees;
            xrOrigin.RotateAround(
                xrCamera.transform.position,
                Vector3.up,
                angle
            );
            snapTurnLatched = true;
        }
        else if (Mathf.Abs(turnInput.x) <= inputDeadZone)
        {
            snapTurnLatched = false;
        }
    }

    private static Vector2 ReadPrimaryAxis(InputDevice device)
    {
        if (
            device.isValid
            && device.TryGetFeatureValue(
                CommonUsages.primary2DAxis,
                out Vector2 value
            )
        )
        {
            return value;
        }

        return Vector2.zero;
    }

    private void UpdatePanelControls()
    {
        if (!leftController.isValid) leftController = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        bool toggle = leftController.isValid && leftController.TryGetFeatureValue(CommonUsages.primaryButton, out bool x) && x;
        bool recenter = leftController.isValid && leftController.TryGetFeatureValue(CommonUsages.secondaryButton, out bool y) && y;
        if (toggle && !textToggleHeld && launcher != null && launcher.IsReady && !EVASelectionMenu.BlocksInteraction)
            FindFirstObjectByType<EVAConversationDisplay>()?.ToggleVisibility();
        if (recenter && !recenterHeld) EVAVRPanelAnchor.RecenterAll();
        textToggleHeld = toggle;
        recenterHeld = recenter;
    }
}
