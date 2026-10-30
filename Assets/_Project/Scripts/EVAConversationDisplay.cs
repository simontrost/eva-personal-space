using System;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[DefaultExecutionOrder(-800)]
[DisallowMultipleComponent]
public sealed class EVAConversationDisplay : MonoBehaviour
{
    private const float ResponseRefreshIntervalSeconds = 1f / 30f;
    private const string PreferencePrefix =
        "EVA.ConversationDisplay.";
    private const string PositionXPreference =
        PreferencePrefix + "PositionX";
    private const string PositionYPreference =
        PreferencePrefix + "PositionY";
    private const string WidthPreference =
        PreferencePrefix + "Width";
    private const string HeightPreference =
        PreferencePrefix + "Height";
    private const string VisibilityPreference =
        PreferencePrefix + "Visible";
    private const string VRVisibilityPreference = PreferencePrefix + "VR.Visible";

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad
    )]
    private static void EnsureInstanceExists()
    {
        if (
            FindFirstObjectByType<EVAConversationDisplay>(
                FindObjectsInactive.Include
            ) != null
            || FindFirstObjectByType<BackendConnection>() == null
        )
        {
            return;
        }

        GameObject host = new GameObject(
            "EVA Conversation Display Controller"
        );

        host.AddComponent<EVAConversationDisplay>();
    }

    [Header("References")]
    [SerializeField]
    private BackendConnection backendConnection;

    [SerializeField]
    private MicrophoneCapture microphoneCapture;

    [Header("Appearance")]
    private static Color userPanelColor => EVAInterfaceTheme.Panel;
    private static Color evaPanelColor => EVAInterfaceTheme.Panel;
    private static Color userAccentColor => EVAInterfaceTheme.Text;
    private static Color evaAccentColor => EVAInterfaceTheme.Text;

    [Tooltip("Maximum displayed words per second. Spoken words are confirmed before appearing and then stay unchanged.")]
    [SerializeField, Range(8f, 40f)] private float wordsPerSecond = 20f;

    [SerializeField]
    [Range(14, 32)]
    private int maximumBodyFontSize = 24;

    [SerializeField]
    [Range(11, 24)]
    private int minimumBodyFontSize = 15;

    [Header("Interaction")]
    [SerializeField]
    private Key toggleKey = Key.T;

    [SerializeField]
    private Vector2 defaultSize = new Vector2(1600f, 340f);

    [SerializeField]
    private Vector2 minimumSize = new Vector2(700f, 240f);

    [Serializable]
    private sealed class MessageEnvelope
    {
        public string type = string.Empty;
        public string sessionId = string.Empty;
        public string text = string.Empty;
        public string code = string.Empty;
        public string message = string.Empty;
        public int index = -1;
        public int revision;
    }

    private readonly StringBuilder responseBuilder = new();
    private readonly EVAWordStream userWords = new();
    private readonly EVAStableTranscript userTranscript = new();
    private readonly EVAWordStream evaWords = new();
    private int lastTranscriptRevision;
    private bool transcriptionFinal;
    private float nextWordTime;

    private GameObject displayRoot;
    private Canvas displayCanvas;
    private EVAVRPanelAnchor worldPanel;
    private RectTransform safeAreaTransform;
    private RectTransform conversationArea;
    private RectTransform dragHandle;
    private RectTransform resizeHandle;
    private RectTransform toggleButton;
    private Text userBodyText;
    private Text evaBodyText;
    private Text toggleButtonText;
    private string currentSessionId;
    private int lastTokenIndex = -1;
    private bool subscribed;
    private bool responseDirty;
    private bool conversationVisible = true;
    private bool menuSuppressed;
    private bool isDragging;
    private bool isResizing;
    private float nextResponseRefreshTime;
    private Vector2 lastPointerPosition;
    private Vector2 resizeStartPointerPosition;
    private Vector2 resizeStartSize;
    private Vector2 resizeStartPosition;
    private int lastScreenWidth;
    private int lastScreenHeight;
    private Rect lastSafeArea;

    private void Awake()
    {
        if (backendConnection == null)
        {
            backendConnection =
                FindFirstObjectByType<BackendConnection>();
        }

        if (microphoneCapture == null)
        {
            microphoneCapture =
                FindFirstObjectByType<MicrophoneCapture>();
        }

        CreateDisplay();
        SetUserText(string.Empty);
        SetEvaText(string.Empty);
    }

    private void OnEnable()
    {
        SubscribeToEvents();
    }

    private void Update()
    {
        UpdateSafeAreaIfRequired();
        HandleVisibilityShortcut();
        HandlePointerInteraction();

        if (
            responseDirty
            && Time.unscaledTime >= nextResponseRefreshTime
        )
        {
            FlushStreamingResponse();
        }
        if (Time.unscaledTime >= nextWordTime)
        {
            if (userWords.TryAdvance(out string userText) && userBodyText != null) userBodyText.text = userText;
            if (evaWords.TryAdvance(out string evaText) && evaBodyText != null) evaBodyText.text = evaText;
            nextWordTime = Time.unscaledTime + 1f / Mathf.Max(1f, wordsPerSecond);
        }
    }

    private void OnDisable()
    {
        UnsubscribeFromEvents();
    }

    private void OnDestroy()
    {
        UnsubscribeFromEvents();
        SaveLayout();

        if (displayRoot != null)
        {
            Destroy(displayRoot);
        }
    }

    private void SubscribeToEvents()
    {
        if (subscribed)
        {
            return;
        }

        if (backendConnection == null)
        {
            Debug.LogError(
                "[EVA] Conversation display could not find " +
                "BackendConnection."
            );
            return;
        }

        backendConnection.TextMessageReceived +=
            HandleTextMessageReceived;
        backendConnection.Disconnected += HandleDisconnected;

        if (microphoneCapture != null)
        {
            microphoneCapture.ListeningStarted +=
                HandleListeningStarted;
            microphoneCapture.ProcessingStarted +=
                HandleProcessingStarted;
            microphoneCapture.InteractionFailed +=
                HandleInteractionFailed;
        }

        subscribed = true;
    }

    private void UnsubscribeFromEvents()
    {
        if (!subscribed)
        {
            return;
        }

        if (backendConnection != null)
        {
            backendConnection.TextMessageReceived -=
                HandleTextMessageReceived;
            backendConnection.Disconnected -= HandleDisconnected;
        }

        if (microphoneCapture != null)
        {
            microphoneCapture.ListeningStarted -=
                HandleListeningStarted;
            microphoneCapture.ProcessingStarted -=
                HandleProcessingStarted;
            microphoneCapture.InteractionFailed -=
                HandleInteractionFailed;
        }

        subscribed = false;
    }

    private void HandleListeningStarted()
    {
        BeginSession(microphoneCapture != null ? microphoneCapture.RecordingSessionId : null);
        SetUserText("Listening...");
        SetEvaText(string.Empty);
    }

    private void HandleProcessingStarted()
    {
        if (userTranscript.Text.Length == 0 && userWords.VisibleText.Length == 0) SetUserText("Recognizing speech...");
    }

    private void HandleInteractionFailed()
    {
        ResetConversationState();
        SetUserText("Recording failed.");
        SetEvaText(string.Empty);
    }

    private void HandleDisconnected()
    {
        ResetConversationState();
        SetEvaText("Connection lost.");
    }

    private void HandleTextMessageReceived(string json)
    {
        MessageEnvelope envelope;

        try
        {
            envelope = JsonUtility.FromJson<MessageEnvelope>(json);
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[EVA] Conversation display ignored malformed JSON: " +
                exception.Message
            );
            return;
        }

        if (envelope == null || string.IsNullOrEmpty(envelope.type))
        {
            return;
        }

        switch (envelope.type)
        {
            case "transcription.started":
                if (currentSessionId == null) BeginSession(envelope.sessionId);
                if (IsCurrentSession(envelope.sessionId) && userTranscript.Text.Length == 0 && userWords.VisibleText.Length == 0)
                    SetUserText("Recognizing speech...");
                break;

            case "transcription.partial":
                if (IsCurrentSession(envelope.sessionId) && !transcriptionFinal && envelope.revision > lastTranscriptRevision)
                {
                    lastTranscriptRevision = envelope.revision;
                    if (userTranscript.Accept(NormalizeDisplayText(envelope.text), false))
                        userWords.SetTarget(userTranscript.Text, true);
                }
                break;

            case "transcription.result":
                HandleTranscriptionResult(envelope);
                break;

            case "transcription.empty":
                if (IsCurrentSession(envelope.sessionId))
                {
                    if (userTranscript.Text.Length == 0) SetUserText("No speech detected.");
                    SetEvaText(string.Empty);
                }
                break;

            case "llm.started":
                HandleLlmStarted(envelope.sessionId);
                break;

            case "llm.token":
                HandleLlmToken(envelope);
                break;

            case "llm.completed":
                HandleLlmCompleted(envelope);
                break;

            case "response.cancelled":
                HandleResponseCancelled(envelope.sessionId);
                break;

            case "error":
                HandleError(envelope);
                break;
        }
    }

    private void HandleTranscriptionResult(
        MessageEnvelope envelope
    )
    {
        if (!IsCurrentSession(envelope.sessionId))
        {
            return;
        }

        transcriptionFinal = true;
        string transcript = NormalizeDisplayText(envelope.text);
        userTranscript.Accept(transcript, true);
        if (userTranscript.Text.Length == 0) SetUserText("No speech detected.");
        else userWords.SetTarget(userTranscript.Text, true);
    }

    private void HandleLlmStarted(string sessionId)
    {
        if (!IsCurrentSession(sessionId))
        {
            return;
        }

        responseBuilder.Clear();
        lastTokenIndex = -1;
        responseDirty = false;
        SetEvaText("Thinking...");
    }

    private void HandleLlmToken(MessageEnvelope envelope)
    {
        if (
            !IsCurrentSession(envelope.sessionId)
            || envelope.index <= lastTokenIndex
        )
        {
            return;
        }

        if (envelope.index == 0)
        {
            responseBuilder.Clear();
        }

        responseBuilder.Append(envelope.text ?? string.Empty);
        lastTokenIndex = envelope.index;
        responseDirty = true;
    }

    private void HandleLlmCompleted(MessageEnvelope envelope)
    {
        if (!IsCurrentSession(envelope.sessionId))
        {
            return;
        }

        string response = NormalizeDisplayText(envelope.text);

        if (string.IsNullOrEmpty(response))
        {
            response = NormalizeDisplayText(
                responseBuilder.ToString()
            );
        }

        responseDirty = false;
        if (string.IsNullOrEmpty(response)) SetEvaText(string.Empty);
        else evaWords.SetTarget(response, true);
    }

    private void HandleResponseCancelled(string sessionId)
    {
        if (!IsCurrentSession(sessionId))
        {
            return;
        }

        responseDirty = false;
        string visible = evaWords.VisibleText;
        SetEvaText(visible.Length > 0 ? visible : "Response interrupted.");
    }

    private void HandleError(MessageEnvelope envelope)
    {
        if (
            !string.IsNullOrEmpty(envelope.sessionId)
            && !IsCurrentSession(envelope.sessionId)
        )
        {
            return;
        }

        string displayedMessage = string.IsNullOrWhiteSpace(
            envelope.message
        )
            ? "An error occurred."
            : envelope.message.Trim();

        if (envelope.code == "transcription_failed")
        {
            SetUserText("Speech recognition failed.");
            return;
        }

        if (envelope.code == "response_pipeline_failed")
        {
            SetEvaText("Error: " + displayedMessage);
            return;
        }

        if (
            envelope.code == "empty_recording"
            || envelope.code == "no_active_recording"
        )
        {
            SetUserText("No speech detected.");
            SetEvaText(string.Empty);
            return;
        }

        SetEvaText("Error: " + displayedMessage);
    }

    private void BeginSession(string sessionId)
    {
        currentSessionId = sessionId;
        userWords.Clear();
        userTranscript.Clear();
        evaWords.Clear();
        lastTranscriptRevision = 0;
        transcriptionFinal = false;
        nextWordTime = 0f;
        responseBuilder.Clear();
        lastTokenIndex = -1;
        responseDirty = false;
    }

    private bool IsCurrentSession(string sessionId)
    {
        return
            !string.IsNullOrEmpty(currentSessionId)
            && currentSessionId == sessionId;
    }

    private void ResetConversationState() => BeginSession(null);

    private void FlushStreamingResponse()
    {
        responseDirty = false;
        nextResponseRefreshTime =
            Time.unscaledTime + ResponseRefreshIntervalSeconds;

        if (responseBuilder.Length > 0)
        {
            evaWords.SetTarget(responseBuilder.ToString().TrimStart(), false);
        }
    }

    private void SetUserText(string text)
    {
        userWords.Clear();
        if (userBodyText != null)
        {
            userBodyText.text = text;
        }
    }

    private void SetEvaText(string text)
    {
        evaWords.Clear();
        if (evaBodyText != null)
        {
            evaBodyText.text = text;
        }
    }

    private static string NormalizeDisplayText(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Trim();
    }

    public void SetVisibility(bool visible) => SetConversationVisible(visible, false);
    public void RecenterVRPanel() { if (worldPanel != null) worldPanel.Recenter(); }

    public void SetMenuSuppressed(bool suppressed)
    {
        menuSuppressed = suppressed;
        isDragging = false;
        isResizing = false;
        if (displayRoot != null) displayRoot.SetActive(!suppressed);
    }

    public void ToggleVisibility()
    {
        if (menuSuppressed) return;
        SetConversationVisible(!conversationVisible, true);
    }

    private void HandleVisibilityShortcut()
    {
        if (menuSuppressed || !Application.isFocused) return;
        Keyboard keyboard = Keyboard.current;

        if (
            keyboard != null
            && toggleKey != Key.None
            && keyboard[toggleKey].wasPressedThisFrame
        )
        {
            ToggleVisibility();
        }
    }

    private void HandlePointerInteraction()
    {
        if (menuSuppressed || Cursor.lockState == CursorLockMode.Locked) return;
        Mouse mouse = Mouse.current;

        if (mouse == null)
        {
            return;
        }

        Vector2 pointerPosition = mouse.position.ReadValue();

        if (mouse.leftButton.wasPressedThisFrame)
        {
            if (ContainsPointer(toggleButton, pointerPosition))
            {
                ToggleVisibility();
                isDragging = false;
                isResizing = false;
                return;
            }

            if (!conversationVisible)
            {
                return;
            }

            if (ContainsPointer(resizeHandle, pointerPosition))
            {
                isResizing = true;
                isDragging = false;
                resizeStartPointerPosition = pointerPosition;
                resizeStartSize = conversationArea.sizeDelta;
                resizeStartPosition =
                    conversationArea.anchoredPosition;
                return;
            }

            if (ContainsPointer(dragHandle, pointerPosition))
            {
                isDragging = true;
                isResizing = false;
                lastPointerPosition = pointerPosition;
            }
        }

        if (mouse.leftButton.isPressed)
        {
            if (isResizing)
            {
                ResizeConversationArea(pointerPosition);
            }
            else if (isDragging)
            {
                MoveConversationArea(pointerPosition);
            }
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            if (isDragging || isResizing)
            {
                SaveLayout();
            }

            isDragging = false;
            isResizing = false;
        }
    }

    private void MoveConversationArea(Vector2 pointerPosition)
    {
        Vector2 pointerDelta = ConvertScreenDeltaToCanvas(
            pointerPosition - lastPointerPosition
        );

        conversationArea.anchoredPosition += pointerDelta;
        lastPointerPosition = pointerPosition;
        ClampConversationAreaToSafeArea();
    }

    private void ResizeConversationArea(Vector2 pointerPosition)
    {
        Vector2 pointerDelta = ConvertScreenDeltaToCanvas(
            pointerPosition - resizeStartPointerPosition
        );

        Vector2 requestedSize = new Vector2(
            resizeStartSize.x + pointerDelta.x,
            resizeStartSize.y - pointerDelta.y
        );

        Vector2 clampedSize = ClampConversationSize(requestedSize);
        Vector2 appliedSizeChange = clampedSize - resizeStartSize;

        conversationArea.sizeDelta = clampedSize;
        conversationArea.anchoredPosition =
            resizeStartPosition
            + new Vector2(
                appliedSizeChange.x * 0.5f,
                -appliedSizeChange.y * 0.5f
            );

        ClampConversationAreaToSafeArea();
    }

    private Vector2 ConvertScreenDeltaToCanvas(Vector2 screenDelta)
    {
        if (displayCanvas != null && displayCanvas.renderMode == RenderMode.WorldSpace && Mouse.current != null)
        {
            Vector2 end = Mouse.current.position.ReadValue();
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(safeAreaTransform, end,
                    displayCanvas.worldCamera, out Vector2 to) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(safeAreaTransform, end - screenDelta,
                    displayCanvas.worldCamera, out Vector2 from)) return to - from;
            return Vector2.zero;
        }
        float scaleFactor = displayCanvas == null
            ? 1f
            : Mathf.Max(0.0001f, displayCanvas.scaleFactor);

        return screenDelta / scaleFactor;
    }

    public bool IsPointerOverControls(Vector2 position) =>
        ContainsPointer(toggleButton, position)
        || (conversationVisible && ContainsPointer(conversationArea, position));

    private static bool ContainsPointer(
        RectTransform target,
        Vector2 pointerPosition
    )
    {
        var canvas = target != null ? target.GetComponentInParent<Canvas>() : null;
        return
            target != null
            && target.gameObject.activeInHierarchy
            && RectTransformUtility.RectangleContainsScreenPoint(
                target,
                pointerPosition,
                canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null
            );
    }

    private Vector2 ClampConversationSize(Vector2 requestedSize)
    {
        if (safeAreaTransform == null)
        {
            return requestedSize;
        }

        Rect safeRect = safeAreaTransform.rect;
        float maximumWidth = Mathf.Max(
            minimumSize.x,
            safeRect.width - 20f
        );
        float maximumHeight = Mathf.Max(
            minimumSize.y,
            safeRect.height - 20f
        );

        return new Vector2(
            Mathf.Clamp(
                requestedSize.x,
                minimumSize.x,
                maximumWidth
            ),
            Mathf.Clamp(
                requestedSize.y,
                minimumSize.y,
                maximumHeight
            )
        );
    }

    private void ClampConversationAreaToSafeArea()
    {
        if (
            safeAreaTransform == null
            || conversationArea == null
        )
        {
            return;
        }

        conversationArea.sizeDelta = ClampConversationSize(
            conversationArea.sizeDelta
        );

        Rect safeRect = safeAreaTransform.rect;
        Vector2 size = conversationArea.sizeDelta;
        float halfWidth = size.x * 0.5f;
        float halfHeight = size.y * 0.5f;
        float maximumX = Mathf.Max(
            0f,
            safeRect.width * 0.5f - halfWidth
        );
        float minimumY = EVAControlSettings.UseVR ? -Mathf.Max(0f, safeRect.height * 0.5f - halfHeight) : halfHeight;
        float maximumY = EVAControlSettings.UseVR ? -minimumY : Mathf.Max(minimumY, safeRect.height - halfHeight);

        Vector2 position = conversationArea.anchoredPosition;
        position.x = Mathf.Clamp(position.x, -maximumX, maximumX);
        position.y = Mathf.Clamp(position.y, minimumY, maximumY);
        conversationArea.anchoredPosition = position;
    }

    private void SetConversationVisible(bool visible, bool save)
    {
        conversationVisible = visible;
        isDragging = false;
        isResizing = false;

        if (conversationArea != null)
        {
            conversationArea.gameObject.SetActive(visible);
        }

        if (toggleButtonText != null)
        {
            string shortcut = EVAControlSettings.UseVR ? "X" : "T";
            toggleButtonText.text = (visible ? "Hide Text  [" : "Show Text  [") + shortcut + "]";
        }

        if (save)
        {
            SaveLayout();
        }
    }

    private void LoadLayout()
    {
        if (EVAControlSettings.UseVR)
        {
            conversationArea.anchorMin = conversationArea.anchorMax = new Vector2(0.5f, 0.5f);
            conversationArea.sizeDelta = new Vector2(1060f, 400f);
            conversationArea.anchoredPosition = new Vector2(0f, -40f);
            ClampConversationAreaToSafeArea();
            SetConversationVisible(PlayerPrefs.GetInt(VRVisibilityPreference,
                EVAControlSettings.VRShowConversationText ? 1 : 0) != 0, false);
            return;
        }
        Vector2 size = new Vector2(
            PlayerPrefs.GetFloat(
                WidthPreference,
                defaultSize.x
            ),
            PlayerPrefs.GetFloat(
                HeightPreference,
                defaultSize.y
            )
        );

        size = ClampConversationSize(size);

        float defaultPositionY = 28f + size.y * 0.5f;
        Vector2 position = new Vector2(
            PlayerPrefs.GetFloat(PositionXPreference, 0f),
            PlayerPrefs.GetFloat(
                PositionYPreference,
                defaultPositionY
            )
        );

        conversationArea.sizeDelta = size;
        conversationArea.anchoredPosition = position;
        ClampConversationAreaToSafeArea();

        bool visible = PlayerPrefs.GetInt(
            VisibilityPreference,
            1
        ) != 0;

        SetConversationVisible(visible, false);
    }

    private void SaveLayout()
    {
        if (conversationArea == null)
        {
            return;
        }

        if (EVAControlSettings.UseVR)
        {
            PlayerPrefs.SetInt(VRVisibilityPreference, conversationVisible ? 1 : 0);
            PlayerPrefs.Save();
            return;
        }

        Vector2 position = conversationArea.anchoredPosition;
        Vector2 size = conversationArea.sizeDelta;

        PlayerPrefs.SetFloat(PositionXPreference, position.x);
        PlayerPrefs.SetFloat(PositionYPreference, position.y);
        PlayerPrefs.SetFloat(WidthPreference, size.x);
        PlayerPrefs.SetFloat(HeightPreference, size.y);
        PlayerPrefs.SetInt(
            VisibilityPreference,
            conversationVisible ? 1 : 0
        );
        PlayerPrefs.Save();
    }

    private void CreateDisplay()
    {
        int uiLayer = LayerMask.NameToLayer("UI");

        if (uiLayer < 0)
        {
            uiLayer = 5;
        }

        displayRoot = new GameObject(
            "EVA Conversation Display",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler)
        );

        displayRoot.layer = uiLayer;
        displayRoot.transform.SetParent(transform, false);

        displayCanvas = displayRoot.GetComponent<Canvas>();
        displayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        displayCanvas.sortingOrder = 2000;

        CanvasScaler scaler =
            displayRoot.GetComponent<CanvasScaler>();

        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode =
            CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        worldPanel = EVAVRPanelAnchor.Configure(displayCanvas, EVAVRPanelKind.Conversation);

        safeAreaTransform = CreateUiObject(
            "Safe Area",
            displayRoot.transform,
            uiLayer
        );

        StretchAcrossParent(safeAreaTransform);
        ApplySafeArea();

        CreateToggleButton(uiLayer);

        conversationArea = CreateUiObject(
            "Conversation Area",
            safeAreaTransform,
            uiLayer
        );

        conversationArea.anchorMin = new Vector2(0.5f, 0f);
        conversationArea.anchorMax = new Vector2(0.5f, 0f);
        conversationArea.pivot = new Vector2(0.5f, 0.5f);
        conversationArea.sizeDelta = defaultSize;
        conversationArea.anchoredPosition = new Vector2(
            0f,
            28f + defaultSize.y * 0.5f
        );

        CreateDragHandle(uiLayer);

        RectTransform panelsRoot = CreateUiObject(
            "Text Panels",
            conversationArea,
            uiLayer
        );

        StretchAcrossParent(panelsRoot);
        panelsRoot.offsetMax = new Vector2(0f, -40f);

        RectTransform userPanel = CreatePanel(
            "User Panel",
            panelsRoot,
            uiLayer,
            new Vector2(0f, 0.62f),
            Vector2.one,
            userPanelColor,
            userAccentColor
        );

        RectTransform evaPanel = CreatePanel(
            "EVA Panel",
            panelsRoot,
            uiLayer,
            Vector2.zero,
            new Vector2(1f, 0.56f),
            evaPanelColor,
            evaAccentColor
        );

        CreateLabel(
            "User Label",
            userPanel,
            uiLayer,
            "01  YOU",
            userAccentColor
        );

        userBodyText = CreateBodyText(
            "User Text",
            userPanel,
            uiLayer
        );

        CreateLabel(
            "EVA Label",
            evaPanel,
            uiLayer,
            "02  EVA",
            evaAccentColor
        );

        evaBodyText = CreateBodyText(
            "EVA Text",
            evaPanel,
            uiLayer
        );

        CreateResizeHandle(uiLayer);
        LoadLayout();
        toggleButton.SetAsLastSibling();
    }

    private void CreateToggleButton(int uiLayer)
    {
        toggleButton = CreateUiObject(
            "Conversation Toggle",
            safeAreaTransform,
            uiLayer
        );

        toggleButton.anchorMin = Vector2.one;
        toggleButton.anchorMax = Vector2.one;
        toggleButton.pivot = Vector2.one;
        toggleButton.anchoredPosition = new Vector2(-24f, -24f);
        toggleButton.sizeDelta = EVAControlSettings.UseVR ? new Vector2(220f, 50f) : new Vector2(190f, 46f);

        Image image = toggleButton.gameObject.AddComponent<Image>();
        image.color = EVAInterfaceTheme.Card;
        image.raycastTarget = EVAControlSettings.UseVR;
        if (EVAControlSettings.UseVR)
        {
            var button = toggleButton.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(ToggleVisibility);
        }

        RectTransform textTransform = CreateUiObject(
            "Label",
            toggleButton,
            uiLayer
        );

        StretchAcrossParent(textTransform);

        toggleButtonText =
            textTransform.gameObject.AddComponent<Text>();
        toggleButtonText.font = GetRuntimeFont();
        toggleButtonText.fontSize = 17;
        toggleButtonText.fontStyle = FontStyle.Bold;
        toggleButtonText.alignment = TextAnchor.MiddleCenter;
        toggleButtonText.color = Color.white;
        toggleButtonText.supportRichText = false;
        toggleButtonText.raycastTarget = false;
    }

    private void CreateDragHandle(int uiLayer)
    {
        dragHandle = CreateUiObject(
            "Move Handle",
            conversationArea,
            uiLayer
        );

        dragHandle.anchorMin = new Vector2(0f, 1f);
        dragHandle.anchorMax = Vector2.one;
        dragHandle.pivot = new Vector2(0.5f, 1f);
        dragHandle.anchoredPosition = Vector2.zero;
        dragHandle.sizeDelta = new Vector2(0f, 36f);

        Image image = dragHandle.gameObject.AddComponent<Image>();
        image.color = EVAInterfaceTheme.Card;
        image.raycastTarget = false;

        RectTransform textTransform = CreateUiObject(
            "Instruction",
            dragHandle,
            uiLayer
        );

        StretchAcrossParent(textTransform);

        Text text = textTransform.gameObject.AddComponent<Text>();
        text.font = GetRuntimeFont();
        text.fontSize = 15;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = EVAInterfaceTheme.Muted;
        text.supportRichText = false;
        text.raycastTarget = false;
        text.text = EVAControlSettings.UseVR ? "Conversation  |  X: show/hide  |  Y: recenter" : "Drag to move";
    }

    private void CreateResizeHandle(int uiLayer)
    {
        resizeHandle = CreateUiObject(
            "Resize Handle",
            conversationArea,
            uiLayer
        );

        resizeHandle.anchorMin = new Vector2(1f, 0f);
        resizeHandle.anchorMax = new Vector2(1f, 0f);
        resizeHandle.pivot = new Vector2(1f, 0f);
        resizeHandle.anchoredPosition = Vector2.zero;
        resizeHandle.sizeDelta = new Vector2(42f, 42f);

        Image image = resizeHandle.gameObject.AddComponent<Image>();
        image.color = EVAInterfaceTheme.Accent;
        image.raycastTarget = false;

        for (int i = 0; i < 3; i++)
        {
            RectTransform grip = CreateUiObject("Grip " + i, resizeHandle, uiLayer);
            grip.anchorMin = grip.anchorMax = new Vector2(1f, 0f);
            grip.pivot = new Vector2(1f, 0.5f);
            grip.anchoredPosition = new Vector2(-8f, 8f + i * 6f);
            grip.sizeDelta = new Vector2(8f + i * 8f, 2f);
            grip.localRotation = Quaternion.Euler(0f, 0f, 45f);
            Image line = grip.gameObject.AddComponent<Image>();
            line.color = EVAInterfaceTheme.Text;
            line.raycastTarget = false;
        }
    }

    private static RectTransform CreatePanel(
        string objectName,
        RectTransform parent,
        int layer,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Color backgroundColor,
        Color outlineColor
    )
    {
        RectTransform panel = CreateUiObject(
            objectName,
            parent,
            layer
        );

        panel.anchorMin = anchorMin;
        panel.anchorMax = anchorMax;
        panel.offsetMin = Vector2.zero;
        panel.offsetMax = Vector2.zero;

        Image image = panel.gameObject.AddComponent<Image>();
        image.color = backgroundColor;
        image.raycastTarget = false;

        return panel;
    }

    private static void CreateLabel(
        string objectName,
        RectTransform parent,
        int layer,
        string label,
        Color color
    )
    {
        RectTransform labelTransform = CreateUiObject(
            objectName,
            parent,
            layer
        );

        labelTransform.anchorMin = new Vector2(0f, 1f);
        labelTransform.anchorMax = Vector2.one;
        labelTransform.pivot = new Vector2(0.5f, 1f);
        labelTransform.offsetMin = new Vector2(18f, -43f);
        labelTransform.offsetMax = new Vector2(-18f, -10f);

        Text text = labelTransform.gameObject.AddComponent<Text>();
        text.font = GetRuntimeFont();
        text.fontSize = 18;
        text.fontStyle = FontStyle.Bold;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = color;
        text.supportRichText = false;
        text.raycastTarget = false;
        text.text = label;
    }

    private Text CreateBodyText(
        string objectName,
        RectTransform parent,
        int layer
    )
    {
        RectTransform bodyTransform = CreateUiObject(
            objectName,
            parent,
            layer
        );

        bodyTransform.anchorMin = Vector2.zero;
        bodyTransform.anchorMax = Vector2.one;
        bodyTransform.offsetMin = new Vector2(18f, 14f);
        bodyTransform.offsetMax = new Vector2(-18f, -48f);

        Text text = bodyTransform.gameObject.AddComponent<Text>();
        text.font = GetRuntimeFont();
        text.fontSize = maximumBodyFontSize;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = minimumBodyFontSize;
        text.resizeTextMaxSize = maximumBodyFontSize;
        text.alignment = TextAnchor.UpperLeft;
        text.color = EVAInterfaceTheme.Text;
        text.supportRichText = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;

        return text;
    }

    private static Font GetRuntimeFont()
    {
        return Resources.GetBuiltinResource<Font>(
            "LegacyRuntime.ttf"
        );
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

    private void UpdateSafeAreaIfRequired()
    {
        if (EVAControlSettings.UseVR) return;
        if (
            Screen.width == lastScreenWidth
            && Screen.height == lastScreenHeight
            && Screen.safeArea == lastSafeArea
        )
        {
            return;
        }

        ApplySafeArea();
    }

    private void ApplySafeArea()
    {
        if (EVAControlSettings.UseVR && safeAreaTransform != null)
        {
            StretchAcrossParent(safeAreaTransform);
            Canvas.ForceUpdateCanvases();
            ClampConversationAreaToSafeArea();
            return;
        }
        if (
            safeAreaTransform == null
            || Screen.width <= 0
            || Screen.height <= 0
        )
        {
            return;
        }

        Rect safeArea = Screen.safeArea;

        safeAreaTransform.anchorMin = new Vector2(
            safeArea.xMin / Screen.width,
            safeArea.yMin / Screen.height
        );
        safeAreaTransform.anchorMax = new Vector2(
            safeArea.xMax / Screen.width,
            safeArea.yMax / Screen.height
        );
        safeAreaTransform.offsetMin = Vector2.zero;
        safeAreaTransform.offsetMax = Vector2.zero;

        Canvas.ForceUpdateCanvases();

        lastScreenWidth = Screen.width;
        lastScreenHeight = Screen.height;
        lastSafeArea = safeArea;

        ClampConversationAreaToSafeArea();
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
