using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(-900)]
[DisallowMultipleComponent]
public sealed class EVALoadingOverlay : MonoBehaviour
{
    private const int SpinnerBarCount = 12;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad
    )]
    private static void EnsureInstanceExists()
    {
        if (
            FindFirstObjectByType<EVALoadingOverlay>(
                FindObjectsInactive.Include
            ) != null
            || FindFirstObjectByType<EVASystemLauncher>() == null
        )
        {
            return;
        }

        GameObject host = new GameObject(
            "EVA Loading Overlay Controller"
        );

        host.AddComponent<EVALoadingOverlay>();
    }

    [Header("References")]
    [SerializeField]
    private EVASystemLauncher systemLauncher;

    [SerializeField]
    private MicrophoneCapture microphoneCapture;

    [Tooltip(
        "Optional UI or control components that should also stay " +
        "disabled while EVA is starting."
    )]
    [SerializeField]
    private MonoBehaviour[] additionalInteractionComponents =
        Array.Empty<MonoBehaviour>();

    [Header("Appearance")]
    [SerializeField]
    private Color backgroundColor =
        new Color(0.025f, 0.035f, 0.055f, 0.92f);

    [SerializeField]
    private Color spinnerColor =
        new Color(0.25f, 0.72f, 1f, 1f);

    [SerializeField]
    private Color errorColor =
        new Color(1f, 0.32f, 0.32f, 1f);

    [SerializeField]
    [Range(48f, 180f)]
    private float spinnerSize = 96f;

    [SerializeField]
    [Range(30f, 720f)]
    private float spinnerSpeedDegreesPerSecond = 210f;

    [SerializeField]
    [Range(14, 40)]
    private int statusFontSize = 22;

    private readonly Dictionary<MonoBehaviour, bool>
        originalInteractionStates = new();

    private readonly List<Image> spinnerBars = new();

    private GameObject overlayRoot;
    private RectTransform spinnerTransform;
    private Text statusText;
    private bool startupFailed;
    private bool interactionWasEnabled = true;

    private void Awake()
    {
        if (systemLauncher == null)
        {
            systemLauncher =
                FindFirstObjectByType<EVASystemLauncher>();
        }

        if (microphoneCapture == null)
        {
            microphoneCapture =
                FindFirstObjectByType<MicrophoneCapture>();
        }

        if (microphoneCapture != null)
        {
            interactionWasEnabled =
                microphoneCapture.InteractionEnabled;
        }

        CacheAdditionalInteractionStates();
        CreateOverlay();
        SetInteractionEnabled(false);
        ShowLoadingState("Starting EVA...");
    }

    private void OnEnable()
    {
        if (systemLauncher != null)
        {
            systemLauncher.StatusChanged += HandleStatusChanged;
            ApplyCurrentLauncherState();
        }
        else
        {
            ShowFailureState(
                "System launcher was not found. Check the Console."
            );

            Debug.LogError(
                "[EVA] Loading overlay could not find " +
                "EVASystemLauncher."
            );
        }
    }

    private void Update()
    {
        if (
            overlayRoot == null
            || !overlayRoot.activeSelf
            || spinnerTransform == null
            || startupFailed
        )
        {
            return;
        }

        spinnerTransform.Rotate(
            0f,
            0f,
            -spinnerSpeedDegreesPerSecond
                * Time.unscaledDeltaTime
        );
    }

    private void OnDisable()
    {
        if (systemLauncher != null)
        {
            systemLauncher.StatusChanged -= HandleStatusChanged;
        }
    }

    private void OnDestroy()
    {
        if (overlayRoot != null)
        {
            Destroy(overlayRoot);
        }
    }

    private void ApplyCurrentLauncherState()
    {
        if (systemLauncher.IsReady)
        {
            CompleteLoading();
            return;
        }

        if (systemLauncher.CurrentStatus == "Failed")
        {
            ShowFailureState(
                "Startup failed. Check the Console and restart " +
                "Play Mode."
            );
            return;
        }

        string status = systemLauncher.CurrentStatus == "Idle"
            ? "Starting EVA..."
            : systemLauncher.CurrentStatus;

        ShowLoadingState(status);
    }

    private void HandleStatusChanged(string status)
    {
        if (systemLauncher.IsReady && status == "Ready")
        {
            CompleteLoading();
            return;
        }

        if (status == "Failed")
        {
            ShowFailureState(
                "Startup failed. Check the Console and restart " +
                "Play Mode."
            );
            return;
        }

        ShowLoadingState(status);
    }

    private void CompleteLoading()
    {
        startupFailed = false;
        SetInteractionEnabled(true);

        if (overlayRoot != null)
        {
            overlayRoot.SetActive(false);
        }

        Debug.Log(
            "[EVA] Loading screen hidden. Interaction is enabled."
        );
    }

    private void ShowLoadingState(string status)
    {
        startupFailed = false;
        SetInteractionEnabled(false);
        SetSpinnerColor(spinnerColor);

        if (statusText != null)
        {
            statusText.text = status;
        }

        if (overlayRoot != null)
        {
            overlayRoot.SetActive(true);
        }
    }

    private void ShowFailureState(string message)
    {
        startupFailed = true;
        SetInteractionEnabled(false);
        SetSpinnerColor(errorColor);

        if (statusText != null)
        {
            statusText.text = message;
        }

        if (overlayRoot != null)
        {
            overlayRoot.SetActive(true);
        }
    }

    private void SetInteractionEnabled(bool enabled)
    {
        if (microphoneCapture != null)
        {
            microphoneCapture.SetInteractionEnabled(
                enabled && interactionWasEnabled
            );
        }

        foreach (
            KeyValuePair<MonoBehaviour, bool> entry
            in originalInteractionStates
        )
        {
            if (entry.Key == null)
            {
                continue;
            }

            entry.Key.enabled = enabled && entry.Value;
        }
    }

    private void CacheAdditionalInteractionStates()
    {
        foreach (
            MonoBehaviour component
            in additionalInteractionComponents
        )
        {
            if (
                component == null
                || component == this
                || component == systemLauncher
                || component == microphoneCapture
                || component is BackendConnection
                || originalInteractionStates.ContainsKey(component)
            )
            {
                continue;
            }

            originalInteractionStates.Add(
                component,
                component.enabled
            );
        }
    }

    private void CreateOverlay()
    {
        int uiLayer = LayerMask.NameToLayer("UI");

        if (uiLayer < 0)
        {
            uiLayer = 5;
        }

        overlayRoot = new GameObject(
            "EVA Loading Overlay",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );

        overlayRoot.layer = uiLayer;
        overlayRoot.transform.SetParent(transform, false);

        Canvas canvas = overlayRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        CanvasScaler scaler =
            overlayRoot.GetComponent<CanvasScaler>();

        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode =
            CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        EVAVRPanelAnchor.Configure(canvas, EVAVRPanelKind.Loading);

        RectTransform background = CreateUiObject(
            "Background",
            overlayRoot.transform,
            uiLayer
        );

        StretchAcrossParent(background);

        Image backgroundImage =
            background.gameObject.AddComponent<Image>();

        backgroundImage.color = backgroundColor;
        backgroundImage.raycastTarget = true;

        spinnerTransform = CreateUiObject(
            "Spinner",
            background,
            uiLayer
        );

        spinnerTransform.anchorMin = new Vector2(0.5f, 0.5f);
        spinnerTransform.anchorMax = new Vector2(0.5f, 0.5f);
        spinnerTransform.pivot = new Vector2(0.5f, 0.5f);
        spinnerTransform.sizeDelta =
            new Vector2(spinnerSize, spinnerSize);
        spinnerTransform.anchoredPosition = new Vector2(0f, 30f);

        CreateSpinnerBars(uiLayer);
        CreateStatusText(background, uiLayer);
    }

    private void CreateSpinnerBars(int uiLayer)
    {
        float radius = spinnerSize * 0.34f;
        float barWidth = spinnerSize * 0.075f;
        float barHeight = spinnerSize * 0.24f;

        for (int barIndex = 0; barIndex < SpinnerBarCount; barIndex++)
        {
            float angle =
                360f * barIndex / SpinnerBarCount;

            float radians = angle * Mathf.Deg2Rad;

            RectTransform bar = CreateUiObject(
                $"Bar {barIndex + 1}",
                spinnerTransform,
                uiLayer
            );

            bar.anchorMin = new Vector2(0.5f, 0.5f);
            bar.anchorMax = new Vector2(0.5f, 0.5f);
            bar.pivot = new Vector2(0.5f, 0.5f);
            bar.sizeDelta = new Vector2(barWidth, barHeight);
            bar.anchoredPosition = new Vector2(
                Mathf.Sin(radians) * radius,
                Mathf.Cos(radians) * radius
            );
            bar.localRotation = Quaternion.Euler(0f, 0f, -angle);

            Image barImage = bar.gameObject.AddComponent<Image>();

            float alpha = Mathf.Lerp(
                0.16f,
                1f,
                (barIndex + 1f) / SpinnerBarCount
            );

            barImage.color = new Color(
                spinnerColor.r,
                spinnerColor.g,
                spinnerColor.b,
                spinnerColor.a * alpha
            );

            barImage.raycastTarget = false;
            spinnerBars.Add(barImage);
        }
    }

    private void CreateStatusText(
        RectTransform parent,
        int uiLayer
    )
    {
        RectTransform textTransform = CreateUiObject(
            "Status",
            parent,
            uiLayer
        );

        textTransform.anchorMin = new Vector2(0.5f, 0.5f);
        textTransform.anchorMax = new Vector2(0.5f, 0.5f);
        textTransform.pivot = new Vector2(0.5f, 0.5f);
        textTransform.sizeDelta = new Vector2(900f, 80f);
        textTransform.anchoredPosition = new Vector2(
            0f,
            -spinnerSize * 0.65f
        );

        statusText = textTransform.gameObject.AddComponent<Text>();
        statusText.font = Resources.GetBuiltinResource<Font>(
            "LegacyRuntime.ttf"
        );
        statusText.fontSize = statusFontSize;
        statusText.alignment = TextAnchor.MiddleCenter;
        statusText.color = Color.white;
        statusText.raycastTarget = false;
        statusText.horizontalOverflow =
            HorizontalWrapMode.Wrap;
        statusText.verticalOverflow =
            VerticalWrapMode.Overflow;
        statusText.text = "Starting EVA...";
    }

    private void SetSpinnerColor(Color color)
    {
        for (int barIndex = 0; barIndex < spinnerBars.Count; barIndex++)
        {
            float alpha = Mathf.Lerp(
                0.16f,
                1f,
                (barIndex + 1f) / spinnerBars.Count
            );

            spinnerBars[barIndex].color = new Color(
                color.r,
                color.g,
                color.b,
                color.a * alpha
            );
        }
    }

    private static RectTransform CreateUiObject(
        string objectName,
        Transform parent,
        int layer
    )
    {
        GameObject uiObject = new GameObject(
            objectName,
            typeof(RectTransform)
        );

        uiObject.layer = layer;
        uiObject.transform.SetParent(parent, false);

        return uiObject.GetComponent<RectTransform>();
    }

    private static void StretchAcrossParent(
        RectTransform rectTransform
    )
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
    }
}
