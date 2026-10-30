using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR;

public enum EVAResponseLanguage { English, German }

// This controller owns selection only. The existing launcher owns system readiness.
[DefaultExecutionOrder(-750), DisallowMultipleComponent]
[AddComponentMenu("EVA/EVA Selection Menu")]
public sealed class EVASelectionMenu : MonoBehaviour
{
    private const string CharacterPreference = "EVA.Selection.Character";
    private const string StylePreference = "EVA.Selection.Style";
    private const string LanguagePreference = "EVA.Selection.Language";
    private const string EnglishVoicePreference = "EVA.Selection.Voice.en";
    private const string GermanVoicePreference = "EVA.Selection.Voice.de";
    private static EVASelectionMenu instance;
    private static readonly string[] CharacterLabels =
    {
        "Female / Adult 01", "Male / Adult 04", "Male / Adult 10",
        "Male / Adult 16", "Female / Adult 15", "Female / Party 01"
    };
    private static readonly string[] StyleLabels = { "Neutral", "Friendly", "Expressive" };
    private static readonly string[] LanguageLabels = { "English", "Deutsch" };

    [Header("Character")]
    [Tooltip("Optional. Leave empty to use the configured EVA nearest the centre of the camera view.")]
    [SerializeField] private EVAAppearanceController primaryAvatar;
    [SerializeField, Range(0, 5)] private int initialCharacter;
    [SerializeField] private EVAAnimationStyle initialBehaviour = EVAAnimationStyle.NeutralProfessional;
    [SerializeField] private EVAResponseLanguage initialLanguage = EVAResponseLanguage.English;
    [SerializeField] private bool rememberSelection = true;

    [Header("References (found automatically when empty)")]
    [SerializeField] private EVASystemLauncher launcher;
    [SerializeField] private BackendConnection backend;
    [SerializeField] private MicrophoneCapture microphone;
    [SerializeField] private StreamingAudioPlayer audioPlayer;
    [SerializeField] private EVAStateController stateController;

    private readonly List<Button> navigationButtons = new();
    private readonly List<Button> characterButtons = new();
    private readonly List<Button> styleButtons = new();
    private readonly List<Button> languageButtons = new();
    private readonly List<Button> voiceButtons = new();
    private readonly List<Button> voicePreviewButtons = new();
    private readonly List<BaseInputModule> pausedInputModules = new();
    private InputSystemUIInputModule addedInputModule;
    private EVAConversationDisplay conversation;
    private EVAAnimationController animations;
    private EventSystem eventSystem;
    private GameObject menuRoot;
    private GameObject settingsRoot;
    private EVAVRPanelAnchor worldPanel;
    private Button settingsButton;
    private Button startButton;
    private AudioSource previewSource;
    private AudioClip previewClip;
    private string previewRequestId;
    private float previewDeadline;
    private float previewEndsAt;
    private int previewVoiceIndex = -1;
    private int englishVoice;
    private int germanVoice;
    private bool backendEventsSubscribed;
    private Text startLabel;
    private bool initialized;
    private bool menuOpen;
    private bool conversationStarted;
    private bool vrConfirmHeld;
    private bool vrMenuHeld;
    private float nextVRNavigationTime;
    private UnityEngine.XR.InputDevice rightController;

    public static EVASelectionMenu Instance => instance;
    public static bool BlocksInteraction => instance != null &&
        (!instance.initialized || !instance.conversationStarted || instance.menuOpen);
    public bool IsMenuOpen => menuOpen;
    public EVAAppearanceController PrimaryAvatar => primaryAvatar;
    public int SelectedCharacter { get; private set; }
    public EVAAnimationStyle SelectedBehaviour { get; private set; }
    public EVAResponseLanguage SelectedLanguage { get; private set; }
    public string ResponseLanguage => SelectedLanguage == EVAResponseLanguage.German ? "de" : "en";
    public string SelectedVoiceProfile => GetVoiceIndex(SelectedLanguage) == 1 ? "male" : "female";
    public bool IsVoicePreviewBusy => !string.IsNullOrEmpty(previewRequestId) || previewClip != null;
    public bool IsSystemReady => launcher != null && launcher.IsReady &&
        backend != null && backend.IsConnected;

    [Serializable]
    private sealed class VoicePreviewRequest
    {
        public string type = "voice.preview";
        public string requestId;
        public string responseLanguage;
        public string voiceProfile;
    }

    [Serializable]
    private sealed class VoicePreviewMessage
    {
        public string type;
        public string requestId;
        public string language;
        public string voiceProfile;
        public string encoding;
        public int sampleRate;
        public int channels;
        public int byteCount;
        public string audioBase64;
        public string message;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetInstance() => instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void EnsureInstanceExists()
    {
        if (FindFirstObjectByType<EVASelectionMenu>(FindObjectsInactive.Include) != null ||
            FindFirstObjectByType<EVASystemLauncher>() == null || FindDefaultAvatar() == null) return;
        new GameObject("EVA Selection Menu Controller").AddComponent<EVASelectionMenu>();
    }

    public static EVAAppearanceController FindDefaultAvatar()
    {
        var camera = Camera.main;
        EVAAppearanceController selected = null;
        float bestScore = float.PositiveInfinity;
        foreach (var candidate in FindObjectsByType<EVAAppearanceController>(
            FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
        {
            if (!candidate.IsConfigured || !candidate.gameObject.scene.IsValid()) continue;
            float score = candidate.gameObject.activeInHierarchy ? 0f : 1000f;
            if (camera != null)
            {
                Vector3 viewport = camera.WorldToViewportPoint(candidate.transform.position + Vector3.up);
                score += (viewport.x - 0.5f) * (viewport.x - 0.5f);
                if (viewport.z <= 0f) score += 100f;
            }
            if (selected == null || score < bestScore) { selected = candidate; bestScore = score; }
        }
        return selected;
    }

    public void ConfigurePrimaryAvatar(EVAAppearanceController avatar) => primaryAvatar = avatar;

    private void Awake()
    {
        if (instance != null && instance != this) { enabled = false; return; }
        instance = this;
        if (launcher == null) launcher = FindFirstObjectByType<EVASystemLauncher>();
        if (backend == null) backend = FindFirstObjectByType<BackendConnection>();
        if (microphone == null) microphone = FindFirstObjectByType<MicrophoneCapture>();
        if (audioPlayer == null) audioPlayer = FindFirstObjectByType<StreamingAudioPlayer>();
        if (stateController == null) stateController = FindFirstObjectByType<EVAStateController>();
        previewSource = gameObject.AddComponent<AudioSource>();
        previewSource.playOnAwake = false;
        previewSource.loop = false;
        previewSource.spatialBlend = 0f;
        CreateInterface();
        menuRoot.SetActive(false);
        settingsRoot.SetActive(false);
    }

    private void OnEnable()
    {
        if (backend == null || backendEventsSubscribed) return;
        backend.TextMessageReceived += HandleVoicePreview;
        backendEventsSubscribed = true;
    }

    private void Start()
    {
        if (instance != this) return;
        conversation = FindFirstObjectByType<EVAConversationDisplay>();
        if (conversation != null)
        {
            if (!EVAControlSettings.UseVR) conversation.SetVisibility(false);
            conversation.SetMenuSuppressed(true);
        }
        if (primaryAvatar == null) primaryAvatar = FindDefaultAvatar();
        if (primaryAvatar == null || !primaryAvatar.IsConfigured)
        {
            Debug.LogError("[EVA/Interface] A configured EVA appearance controller is required.", this);
            return;
        }
        animations = primaryAvatar.GetComponent<EVAAnimationController>();
        if (animations == null)
        {
            Debug.LogError("[EVA/Interface] The selected EVA has no animation controller.", this);
            return;
        }
        primaryAvatar.RepairMissingModelReferences();
        primaryAvatar.gameObject.SetActive(true);
        foreach (var avatar in FindObjectsByType<EVAAppearanceController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (avatar != primaryAvatar && avatar.gameObject.scene == primaryAvatar.gameObject.scene)
                avatar.gameObject.SetActive(false);
        }
        SelectedCharacter = Mathf.Clamp(rememberSelection
            ? PlayerPrefs.GetInt(CharacterPreference, initialCharacter) : initialCharacter, 0, 5);
        int style = rememberSelection ? PlayerPrefs.GetInt(StylePreference, (int)initialBehaviour)
            : (int)initialBehaviour;
        SelectedBehaviour = (EVAAnimationStyle)Mathf.Clamp(style, 0, 2);
        int language = rememberSelection ? PlayerPrefs.GetInt(LanguagePreference, (int)initialLanguage)
            : (int)initialLanguage;
        SelectedLanguage = (EVAResponseLanguage)Mathf.Clamp(language, 0, 1);
        primaryAvatar.SetAppearance(SelectedCharacter);
        int defaultVoice = primaryAvatar.IsMale ? 1 : 0;
        englishVoice = Mathf.Clamp(rememberSelection
            ? PlayerPrefs.GetInt(EnglishVoicePreference, defaultVoice) : defaultVoice, 0, 1);
        germanVoice = Mathf.Clamp(rememberSelection
            ? PlayerPrefs.GetInt(GermanVoicePreference, defaultVoice) : defaultVoice, 0, 1);
        animations.SetStyle(SelectedBehaviour);
        initialized = true;
        RefreshChoices();
        Debug.Log("[EVA/Interface] One EVA is active. Waiting for system readiness.");
    }

    private void Update()
    {
        if (instance != this || !initialized) return;
        if (!conversationStarted && !menuOpen && IsSystemReady) TryOpenMenu();
        if (menuOpen)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            startButton.interactable = IsSystemReady;
            foreach (var button in voicePreviewButtons) button.interactable = IsSystemReady;
        }
        if (IsVoicePreviewBusy && !IsSystemReady) StopVoicePreview();
        else if (!string.IsNullOrEmpty(previewRequestId) && Time.unscaledTime >= previewDeadline)
            FailVoicePreview("Preview timed out. Please try again.");
        else if (previewClip != null && Time.unscaledTime >= previewEndsAt) StopVoicePreview();
        settingsRoot.SetActive(conversationStarted && !menuOpen && IsSystemReady);
        settingsButton.interactable = CanChangeCharacter();
        if (Application.isFocused && Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
        {
            if (menuOpen) StartConversation();
            else TryOpenMenu();
        }
        HandleVRInput();
    }

    private bool CanChangeCharacter() => initialized && IsSystemReady &&
        (stateController == null || stateController.CurrentState == EVAState.Idle) &&
        (microphone == null || (!microphone.IsRecording && !microphone.IsSending)) &&
        (audioPlayer == null || !audioPlayer.HasActiveResponse);

    public bool TryOpenMenu()
    {
        if (!CanChangeCharacter()) return false;
        SelectedCharacter = primaryAvatar.AppearanceIndex;
        SelectedBehaviour = animations.AnimationStyle;
        RefreshChoices();
        menuOpen = true;
        menuRoot.SetActive(true);
        if (worldPanel != null) worldPanel.Recenter();
        settingsRoot.SetActive(false);
        if (conversation == null) conversation = FindFirstObjectByType<EVAConversationDisplay>();
        if (conversation != null) conversation.SetMenuSuppressed(true);
        var desktop = FindFirstObjectByType<EVADesktopController>();
        if (desktop != null) desktop.ReleaseMouse();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        startLabel.text = conversationStarted ? "Continue conversation" : "Start conversation";
        if (eventSystem != null) eventSystem.SetSelectedGameObject(characterButtons[SelectedCharacter].gameObject);
        vrConfirmHeld = ReadVRButton(UnityEngine.XR.CommonUsages.primaryButton);
        nextVRNavigationTime = 0f;
        return true;
    }

    public bool StartConversation()
    {
        if (!initialized || !menuOpen || !IsSystemReady) return false;
        StopVoicePreview();
        conversationStarted = true;
        menuOpen = false;
        menuRoot.SetActive(false);
        if (eventSystem != null) eventSystem.SetSelectedGameObject(null);
        if (conversation != null) conversation.SetMenuSuppressed(false);
        if (conversation != null) conversation.RecenterVRPanel();
        if (rememberSelection)
        {
            PlayerPrefs.SetInt(CharacterPreference, SelectedCharacter);
            PlayerPrefs.SetInt(StylePreference, (int)SelectedBehaviour);
            PlayerPrefs.SetInt(LanguagePreference, (int)SelectedLanguage);
            PlayerPrefs.SetInt(EnglishVoicePreference, englishVoice);
            PlayerPrefs.SetInt(GermanVoicePreference, germanVoice);
            PlayerPrefs.Save();
        }
        Debug.Log($"[EVA/Interface] Conversation ready: {CharacterLabels[SelectedCharacter]}, {SelectedBehaviour}, {ResponseLanguage}, {SelectedVoiceProfile} voice.");
        return true;
    }

    public void ChooseCharacter(int index)
    {
        if (index < 0 || index >= CharacterLabels.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (!initialized || !menuOpen) return;
        primaryAvatar.SetAppearance(index);
        SelectedCharacter = index;
        RefreshChoices();
    }

    public void ChooseBehaviour(EVAAnimationStyle style)
    {
        if ((int)style < 0 || (int)style >= StyleLabels.Length) throw new ArgumentOutOfRangeException(nameof(style));
        if (!initialized || !menuOpen) return;
        animations.SetStyle(style);
        SelectedBehaviour = style;
        RefreshChoices();
    }

    public void ChooseLanguage(EVAResponseLanguage language)
    {
        if ((int)language < 0 || (int)language >= LanguageLabels.Length)
            throw new ArgumentOutOfRangeException(nameof(language));
        if (!initialized || !menuOpen) return;
        StopVoicePreview();
        SelectedLanguage = language;
        RefreshChoices();
    }

    public int GetVoiceIndex(EVAResponseLanguage language) =>
        language == EVAResponseLanguage.German ? germanVoice : englishVoice;

    public void ChooseVoice(int index)
    {
        if (index < 0 || index > 1) throw new ArgumentOutOfRangeException(nameof(index));
        if (!initialized || !menuOpen) return;
        StopVoicePreview();
        if (SelectedLanguage == EVAResponseLanguage.German) germanVoice = index;
        else englishVoice = index;
        RefreshChoices();
    }

    public async void ToggleVoicePreview(int voiceIndex)
    {
        if (voiceIndex < 0 || voiceIndex > 1) throw new ArgumentOutOfRangeException(nameof(voiceIndex));
        if (IsVoicePreviewBusy && previewVoiceIndex == voiceIndex) { StopVoicePreview(); return; }
        if (!initialized || !menuOpen || !IsSystemReady) return;
        StopVoicePreview();
        previewVoiceIndex = voiceIndex;
        string requestId = Guid.NewGuid().ToString("N");
        previewRequestId = requestId;
        previewDeadline = Time.unscaledTime + 20f;
        RefreshPreviewButtons();
        try
        {
            await backend.SendJsonAsync(JsonUtility.ToJson(new VoicePreviewRequest
            {
                requestId = requestId, responseLanguage = ResponseLanguage,
                voiceProfile = voiceIndex == 1 ? "male" : "female"
            }));
        }
        catch (Exception exception)
        {
            if (previewRequestId == requestId) FailVoicePreview(exception.Message);
        }
    }

    public void StopVoicePreview()
    {
        previewRequestId = null;
        previewVoiceIndex = -1;
        if (previewSource != null) { previewSource.Stop(); previewSource.clip = null; }
        if (previewClip != null) Destroy(previewClip);
        previewClip = null;
        RefreshPreviewButtons();
    }

    private void FailVoicePreview(string message)
    {
        StopVoicePreview();
        Debug.LogWarning("[EVA/Interface] Voice preview: " + message);
    }

    private void RefreshPreviewButtons()
    {
        for (int i = 0; i < voicePreviewButtons.Count; i++)
        {
            var button = voicePreviewButtons[i];
            if (button == null) continue;
            var glyph = EVASelectionPreviewIcon.Glyph.Play;
            if (i == previewVoiceIndex)
            {
                if (!string.IsNullOrEmpty(previewRequestId)) glyph = EVASelectionPreviewIcon.Glyph.Loading;
                else if (previewClip != null) glyph = EVASelectionPreviewIcon.Glyph.Stop;
            }
            button.GetComponent<EVASelectionPreviewIcon>().SetGlyph(glyph);
        }
    }

    private void HandleVoicePreview(string json)
    {
        if (!menuOpen || string.IsNullOrEmpty(previewRequestId)) return;
        try
        {
            var message = JsonUtility.FromJson<VoicePreviewMessage>(json);
            if (message == null || message.requestId != previewRequestId) return;
            if (message.type == "voice.preview.error") { FailVoicePreview(message.message); return; }
            if (message.type != "voice.preview.audio") return;
            if (message.language != ResponseLanguage || message.voiceProfile != (previewVoiceIndex == 1 ? "male" : "female"))
                throw new InvalidOperationException("The preview does not match the selected voice/language.");
            if (message.encoding != "pcm_s16le" || message.channels != 1 || message.sampleRate < 8000 ||
                message.sampleRate > 48000 || string.IsNullOrEmpty(message.audioBase64) ||
                message.audioBase64.Length > 2000000)
                throw new InvalidOperationException("Invalid preview audio format.");
            byte[] pcm = Convert.FromBase64String(message.audioBase64);
            if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length != message.byteCount ||
                pcm.Length > message.sampleRate * 2 * 15)
                throw new InvalidOperationException("Invalid preview audio length.");
            var samples = new float[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768f;
            previewClip = AudioClip.Create("EVA voice preview", samples.Length, 1, message.sampleRate, false);
            if (!previewClip.SetData(samples, 0)) throw new InvalidOperationException("Could not load preview samples.");
            previewRequestId = null;
            previewSource.clip = previewClip;
            previewSource.Play();
            previewEndsAt = Time.unscaledTime + previewClip.length + 0.1f;
            RefreshPreviewButtons();
        }
        catch (Exception exception) { FailVoicePreview(exception.Message); }
    }

    private void RefreshChoices()
    {
        for (int i = 0; i < characterButtons.Count; i++) MarkChoice(characterButtons[i], i == SelectedCharacter);
        for (int i = 0; i < styleButtons.Count; i++) MarkChoice(styleButtons[i], i == (int)SelectedBehaviour);
        for (int i = 0; i < languageButtons.Count; i++) MarkChoice(languageButtons[i], i == (int)SelectedLanguage);
        for (int i = 0; i < voiceButtons.Count; i++)
        {
            string name = SelectedLanguage == EVAResponseLanguage.German
                ? (i == 0 ? "Ramona" : "Thorsten") : (i == 0 ? "Kristin" : "Mike");
            voiceButtons[i].GetComponentInChildren<Text>().text = name + (i == 0 ? " (female)" : " (male)");
            MarkChoice(voiceButtons[i], i == GetVoiceIndex(SelectedLanguage));
        }
    }

    private static void MarkChoice(Button button, bool selected)
    {
        ColorBlock colors = button.colors;
        colors.normalColor = selected ? EVAInterfaceTheme.Accent : EVAInterfaceTheme.Card;
        colors.highlightedColor = new Color(0.16f, 0.40f, 0.55f);
        colors.selectedColor = new Color(0.20f, 0.43f, 0.56f);
        colors.pressedColor = new Color(0.08f, 0.26f, 0.37f);
        button.colors = colors;
    }

    public static bool IsPointerOverMenuControls(Vector2 screenPosition)
    {
        if (instance == null) return false;
        if (instance.menuOpen) return true;
        var rect = instance.settingsButton != null ? instance.settingsButton.transform as RectTransform : null;
        if (rect == null || !rect.gameObject.activeInHierarchy) return false;
        var canvas = rect.GetComponentInParent<Canvas>();
        var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, camera);
    }

    private bool ReadVRButton(InputFeatureUsage<bool> usage)
    {
        if (!EVAControlSettings.UseVR) return false;
        if (!rightController.isValid) rightController = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        return rightController.isValid && rightController.TryGetFeatureValue(usage, out bool value) && value;
    }

    private void HandleVRInput()
    {
        if (!EVAControlSettings.UseVR) return;
        bool settingsHeld = ReadVRButton(UnityEngine.XR.CommonUsages.secondaryButton);
        if (settingsHeld && !vrMenuHeld)
        {
            if (menuOpen) StartConversation(); else TryOpenMenu();
        }
        vrMenuHeld = settingsHeld;
        bool confirm = ReadVRButton(UnityEngine.XR.CommonUsages.primaryButton);
        if (menuOpen)
        {
            if (rightController.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis) &&
                axis.sqrMagnitude > 0.4f && Time.unscaledTime >= nextVRNavigationTime)
            {
                int direction = Mathf.Abs(axis.y) >= Mathf.Abs(axis.x) ? (axis.y < 0f ? 1 : -1) : (axis.x > 0f ? 1 : -1);
                GameObject selected = eventSystem.currentSelectedGameObject;
                int index = navigationButtons.FindIndex(b => b.gameObject == selected);
                index = (index + direction + navigationButtons.Count) % navigationButtons.Count;
                eventSystem.SetSelectedGameObject(navigationButtons[index].gameObject);
                nextVRNavigationTime = Time.unscaledTime + 0.3f;
            }
            if (confirm && !vrConfirmHeld && eventSystem.currentSelectedGameObject != null)
            {
                var button = eventSystem.currentSelectedGameObject.GetComponent<Button>();
                if (button != null && button.IsInteractable()) button.onClick.Invoke();
            }
        }
        vrConfirmHeld = confirm;
    }

    private void OnDisable()
    {
        StopVoicePreview();
        if (backend != null && backendEventsSubscribed) backend.TextMessageReceived -= HandleVoicePreview;
        backendEventsSubscribed = false;
        if (instance != this) return;
        if (menuRoot != null) menuRoot.SetActive(false);
        if (settingsRoot != null) settingsRoot.SetActive(false);
        if (conversation != null) conversation.SetMenuSuppressed(false);
        instance = null;
    }

    private void OnDestroy()
    {
        StopVoicePreview();
        if (backend != null && backendEventsSubscribed) backend.TextMessageReceived -= HandleVoicePreview;
        if (instance == this) instance = null;
        foreach (var module in pausedInputModules) if (module != null) module.enabled = true;
        if (addedInputModule != null) Destroy(addedInputModule);
    }

    private void CreateInterface()
    {
        eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            var host = new GameObject("EVA UI EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            host.transform.SetParent(transform, false);
            eventSystem = host.GetComponent<EventSystem>();
        }
        else if (eventSystem.GetComponent<InputSystemUIInputModule>() == null)
        {
            foreach (var module in eventSystem.GetComponents<BaseInputModule>())
            {
                if (!module.enabled) continue;
                pausedInputModules.Add(module);
                module.enabled = false;
            }
            addedInputModule = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
        }

        var canvasObject = new GameObject("EVA Selection Interface", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.layer = 5;
        canvasObject.transform.SetParent(transform, false);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 3000;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        worldPanel = EVAVRPanelAnchor.Configure(canvas, EVAVRPanelKind.Menu);

        menuRoot = Rect("Selection screen", canvasObject.transform, Vector2.zero, Vector2.one).gameObject;
        var shade = menuRoot.AddComponent<Image>();
        shade.color = EVAControlSettings.UseVR ? Color.clear : new Color(0.01f, 0.02f, 0.04f, 0.20f);
        var panel = Rect("Selection panel", menuRoot.transform,
            EVAControlSettings.UseVR ? Vector2.zero : new Vector2(0.025f, 0.05f),
            EVAControlSettings.UseVR ? Vector2.one : new Vector2(0.40f, 0.95f));
        panel.gameObject.AddComponent<Image>().color = EVAInterfaceTheme.Panel;
        Label("Title", panel, "EVA", new Vector2(0.055f, 0.91f), new Vector2(0.945f, 0.985f), 58, true);
        Label("Character heading", panel, "01  CHARACTER", new Vector2(0.055f, 0.855f), new Vector2(0.945f, 0.905f), 23, true);
        for (int i = 0; i < CharacterLabels.Length; i++)
        {
            int index = i;
            int row = i / 2, column = i % 2;
            float left = 0.055f + column * 0.455f;
            float top = 0.845f - row * 0.085f;
            var button = MakeButton("Character " + i, panel, CharacterLabels[i],
                new Vector2(left, top - 0.07f), new Vector2(left + 0.43f, top), () => ChooseCharacter(index));
            characterButtons.Add(button);
            navigationButtons.Add(button);
        }
        Label("Behaviour heading", panel, "02  BEHAVIOUR", new Vector2(0.055f, 0.54f), new Vector2(0.945f, 0.59f), 23, true);
        for (int i = 0; i < StyleLabels.Length; i++)
        {
            var style = (EVAAnimationStyle)i;
            float left = 0.055f + i * 0.305f;
            var button = MakeButton("Behaviour " + i, panel, StyleLabels[i],
                new Vector2(left, 0.455f), new Vector2(left + 0.28f, 0.53f), () => ChooseBehaviour(style));
            styleButtons.Add(button);
            navigationButtons.Add(button);
        }
        Label("Language heading", panel, "03  LANGUAGE", new Vector2(0.055f, 0.39f), new Vector2(0.945f, 0.44f), 23, true);
        for (int i = 0; i < LanguageLabels.Length; i++)
        {
            var language = (EVAResponseLanguage)i;
            float left = 0.055f + i * 0.455f;
            var button = MakeButton("Language " + i, panel, LanguageLabels[i],
                new Vector2(left, 0.315f), new Vector2(left + 0.43f, 0.385f), () => ChooseLanguage(language));
            languageButtons.Add(button);
            navigationButtons.Add(button);
        }
        Label("Voice heading", panel, "04  VOICE", new Vector2(0.055f, 0.25f), new Vector2(0.945f, 0.30f), 23, true);
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            float left = 0.055f + i * 0.455f;
            var button = MakeButton("Voice " + i, panel, "",
                new Vector2(left, 0.15f), new Vector2(left + 0.43f, 0.235f), () => ChooseVoice(index));
            // The name selects the voice. The separate child control only previews it.
            var label = button.GetComponentInChildren<Text>().rectTransform;
            label.anchorMin = new Vector2(0.04f, 0.08f);
            label.anchorMax = new Vector2(0.76f, 0.92f);
            voiceButtons.Add(button);
            navigationButtons.Add(button);
            var previewRect = Rect("Preview voice " + i, button.transform,
                new Vector2(0.79f, 0.12f), new Vector2(0.98f, 0.88f));
            previewRect.gameObject.AddComponent<CanvasRenderer>();
            var icon = previewRect.gameObject.AddComponent<EVASelectionPreviewIcon>();
            icon.color = Color.white;
            var preview = previewRect.gameObject.AddComponent<Button>();
            preview.targetGraphic = icon;
            var colors = preview.colors;
            colors.normalColor = new Color(0.76f, 0.85f, 0.91f);
            colors.highlightedColor = Color.white;
            colors.selectedColor = new Color(0.38f, 0.85f, 1f);
            colors.pressedColor = new Color(0.25f, 0.67f, 0.85f);
            preview.colors = colors;
            preview.onClick.AddListener(() => ToggleVoicePreview(index));
            voicePreviewButtons.Add(preview);
            navigationButtons.Add(preview);
        }
        startButton = MakeButton("Start conversation", panel, "Start conversation",
            new Vector2(0.055f, 0.035f), new Vector2(0.945f, 0.115f), () => StartConversation());
        startLabel = startButton.GetComponentInChildren<Text>();
        navigationButtons.Add(startButton);

        settingsRoot = Rect("Conversation controls", canvasObject.transform, Vector2.zero, Vector2.one).gameObject;
        settingsButton = MakeButton("Character settings", settingsRoot.transform, "Character settings  [M]",
            new Vector2(0.02f, 0.92f), new Vector2(0.23f, 0.98f), () => TryOpenMenu());
        // In VR, right B is the settings control; avoid a detached corner HUD.
        if (EVAControlSettings.UseVR) settingsButton.gameObject.SetActive(false);
    }

    private static RectTransform Rect(string name, Transform parent, Vector2 minimum, Vector2 maximum)
    {
        var host = new GameObject(name, typeof(RectTransform));
        host.layer = 5;
        host.transform.SetParent(parent, false);
        var rect = host.GetComponent<RectTransform>();
        rect.anchorMin = minimum; rect.anchorMax = maximum;
        rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        return rect;
    }

    private static Text Label(string name, Transform parent, string value, Vector2 minimum,
        Vector2 maximum, int size, bool bold = false)
    {
        var text = Rect(name, parent, minimum, maximum).gameObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.text = value; text.color = Color.white;
        text.fontSize = size; text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        text.alignment = TextAnchor.MiddleLeft;
        text.resizeTextForBestFit = true; text.resizeTextMinSize = 12; text.resizeTextMaxSize = size;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        return text;
    }

    private static Button MakeButton(string name, Transform parent, string text, Vector2 minimum,
        Vector2 maximum, UnityEngine.Events.UnityAction action)
    {
        var rect = Rect(name, parent, minimum, maximum);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = Color.white;
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        MarkChoice(button, false);
        button.onClick.AddListener(action);
        var label = Label("Label", rect, text, new Vector2(0.06f, 0.08f), new Vector2(0.94f, 0.92f), 24, true);
        label.alignment = TextAnchor.MiddleCenter;
        return button;
    }
}
