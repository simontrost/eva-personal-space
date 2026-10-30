using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

public sealed class MicrophoneCapture : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private BackendConnection backendConnection;

    [SerializeField]
    private StreamingAudioPlayer streamingAudioPlayer;

    [Tooltip("Optional fixed voice source. Leave empty to use the closest active EVA to the Main Camera.")]
    [SerializeField] private EVAAppearanceController appearanceVoiceSource;

    [Header("Push To Talk")]
    [SerializeField]
    private Key pushToTalkKey = Key.B;

    [Tooltip(
        "In VR, hold the primary button on this controller. " +
        "RightHand maps to the A button on Meta Quest Touch."
    )]
    [SerializeField]
    private XRNode controllerPushToTalkHand = XRNode.RightHand;

    [Header("Microphone")]
    [Tooltip("Leave empty to use the Windows default microphone.")]
    [SerializeField]
    private string microphoneDevice = "";

    [SerializeField]
    [Range(8000, 48000)]
    private int requestedSampleRate = 16000;

    [SerializeField]
    [Range(1, 120)]
    private int maximumRecordingSeconds = 30;

    private AudioClip recordingClip;
    private string activeDevice;
    private bool isRecording;
    private bool isSending;
    private bool pushToTalkHeld;
    private string recordingVoiceProfile = "female";
    private string recordingResponseLanguage = "en";
    private bool waitForPushToTalkRelease;
    private string recordingSessionId;
    private CancellationTokenSource recordingCancellation;
    private Task recordingStreamTask = Task.CompletedTask;
    private Exception recordingStreamError;
    private bool streamWhileRecording;
    private int sentFrames;
    private int observedFrames;
    private float recordingStartedAt;
    public string RecordingSessionId => recordingSessionId;
    private UnityEngine.XR.InputDevice pushToTalkController;

    public bool InteractionEnabled { get; private set; } = true;
    public bool CanRecord => InteractionEnabled && !EVASelectionMenu.BlocksInteraction;
    public bool IsRecording => isRecording;
    public bool IsSending => isSending;

    public event Action ListeningStarted;
    public event Action ProcessingStarted;
    public event Action InteractionFailed;

    [Serializable]
    private sealed class AudioStartMessage
    {
        public string type = "audio.start";
        public string sessionId;
        public int sampleRate;
        public int channels;
        public string encoding = "pcm_s16le";
        public int byteCount;
        public bool liveTranscription;
        public string voiceProfile = "female";
        public string responseLanguage = "en";
    }

    [Serializable]
    private sealed class AudioEndMessage
    {
        public string type = "audio.end";
        public string sessionId;
    }

    [Serializable]
    private sealed class ResponseCancelMessage
    {
        public string type = "response.cancel";
    }

    private void Awake()
    {
        if (streamingAudioPlayer == null)
        {
            streamingAudioPlayer =
                FindFirstObjectByType<StreamingAudioPlayer>();
        }
    }

    private void Update()
    {
        if (!CanRecord)
        {
            CancelActiveRecording();
            pushToTalkHeld = false;
            waitForPushToTalkRelease = true;
            return;
        }

        bool keyboardHeld = IsKeyboardPushToTalkHeld();
        bool controllerHeld = IsControllerPushToTalkHeld();
        bool shouldRecord = keyboardHeld || controllerHeld;

        // The A button used to confirm the menu must be released before recording.
        if (waitForPushToTalkRelease)
        {
            if (!shouldRecord) waitForPushToTalkRelease = false;
            pushToTalkHeld = false;
            return;
        }

        if (shouldRecord && !pushToTalkHeld)
        {
            BeginRecording();
        }

        if (!shouldRecord && pushToTalkHeld)
        {
            EndRecordingAndSend();
        }

        pushToTalkHeld = shouldRecord;
        if (isRecording && recordingStreamError != null)
        {
            CancelActiveRecording();
            InteractionFailed?.Invoke();
        }
        else if (isRecording && Time.unscaledTime - recordingStartedAt >= maximumRecordingSeconds)
        {
            EndRecordingAndSend();
            waitForPushToTalkRelease = true;
        }
    }

    private bool IsKeyboardPushToTalkHeld()
    {
        Keyboard keyboard = Keyboard.current;

        return
            Application.isFocused && keyboard != null
            && pushToTalkKey != Key.None
            && keyboard[pushToTalkKey].isPressed;
    }

    private bool IsControllerPushToTalkHeld()
    {
        if (!pushToTalkController.isValid)
        {
            pushToTalkController = InputDevices.GetDeviceAtXRNode(
                controllerPushToTalkHand
            );
        }

        return
            pushToTalkController.isValid
            && pushToTalkController.TryGetFeatureValue(
                UnityEngine.XR.CommonUsages.primaryButton,
                out bool pressed
            )
            && pressed;
    }

    public void BeginRecording()
    {
        if (!CanRecord)
        {
            return;
        }

        if (isRecording)
        {
            return;
        }

        if (isSending)
        {
            Debug.LogWarning(
                "[EVA] Please wait until the previous recording was sent."
            );
            return;
        }

        if (backendConnection == null)
        {
            Debug.LogError(
                "[EVA] BackendConnection reference is missing."
            );
            return;
        }

        if (!backendConnection.IsReady)
        {
            Debug.LogWarning(
                "[EVA] Push-to-talk ignored because the backend isn't connected."
            );
            return;
        }

        if (
            streamingAudioPlayer != null
            && streamingAudioPlayer.InterruptPlayback()
        )
        {
            SendResponseCancellation();
        }

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError(
                "[EVA] Windows didn't report an available microphone."
            );
            return;
        }

        activeDevice = string.IsNullOrWhiteSpace(microphoneDevice)
            ? null
            : microphoneDevice;

        recordingClip = Microphone.Start(
            activeDevice,
            false,
            maximumRecordingSeconds,
            requestedSampleRate
        );

        if (recordingClip == null)
        {
            Debug.LogError(
                "[EVA] The microphone recording couldn't be started."
            );
            return;
        }

        isRecording = true;
        recordingSessionId = Guid.NewGuid().ToString("N");
        recordingCancellation = new CancellationTokenSource();
        recordingStreamError = null;
        sentFrames = observedFrames = 0;
        recordingStartedAt = Time.unscaledTime;
        streamWhileRecording = backendConnection.SupportsLiveTranscription;
        recordingVoiceProfile = ResolveRecordingVoiceProfile();
        // Freeze the menu choice for this turn, just like the appearance's voice profile.
        recordingResponseLanguage = EVASelectionMenu.Instance != null
            ? EVASelectionMenu.Instance.ResponseLanguage : "en";

        ListeningStarted?.Invoke();
        recordingStreamTask = streamWhileRecording
            ? StreamRecordingAsync(recordingClip, recordingCancellation.Token)
            : Task.CompletedTask;

        string displayedDevice = activeDevice ?? "Windows default";

        Debug.Log(
            $"[EVA] Push-to-talk started. Device: {displayedDevice}; voice profile: {recordingVoiceProfile}"
        );
    }

    public async void EndRecordingAndSend()
    {
        if (!CanRecord) { CancelActiveRecording(); return; }
        if (!isRecording || recordingClip == null) return;

        AudioClip completedClip = recordingClip;
        int recordedFrames = GetRecordedFrames(completedClip);
        Microphone.End(activeDevice);
        isRecording = false;
        isSending = true;
        ProcessingStarted?.Invoke();
        bool completed = false;
        try
        {
            await recordingStreamTask;
            if (recordingStreamError != null) throw recordingStreamError;
            if (recordedFrames <= 0) throw new InvalidOperationException("The recording contains no samples.");
            if (!streamWhileRecording)
                await SendRecordingStartAsync(completedClip.frequency, recordedFrames * 2, false);
            await SendFramesAsync(completedClip, recordedFrames, recordingCancellation.Token);
            EnsureConnected();
            await backendConnection.SendJsonAsync(JsonUtility.ToJson(
                new AudioEndMessage { sessionId = recordingSessionId }));
            completed = true;
            Debug.Log($"[EVA] Recording sent: {(float)recordedFrames / completedClip.frequency:F2}s, {sentFrames * 2} PCM bytes.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Debug.LogError("[EVA] Audio transmission failed: " + exception.Message);
            InteractionFailed?.Invoke();
        }
        finally
        {
            if (!completed) await SendRecordingCancellationAsync(recordingSessionId);
            recordingCancellation?.Dispose();
            recordingCancellation = null;
            recordingClip = null;
            activeDevice = null;
            isSending = false;
            Destroy(completedClip);
        }
    }

    private async Task StreamRecordingAsync(AudioClip clip, CancellationToken cancellation)
    {
        try
        {
            await SendRecordingStartAsync(clip.frequency, 0, true);
            while (isRecording)
            {
                cancellation.ThrowIfCancellationRequested();
                int frames = GetRecordedFrames(clip);
                // Send short contiguous chunks; never retransmit an earlier prefix.
                if (frames - sentFrames >= clip.frequency / 5)
                    await SendFramesAsync(clip, frames, cancellation);
                await Task.Delay(60, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            recordingStreamError = exception;
        }
    }

    private Task SendRecordingStartAsync(int sampleRate, int byteCount, bool live)
    {
        EnsureConnected();
        return backendConnection.SendJsonAsync(JsonUtility.ToJson(new AudioStartMessage
        {
            sessionId = recordingSessionId, sampleRate = sampleRate, channels = 1,
            byteCount = byteCount, liveTranscription = live,
            voiceProfile = recordingVoiceProfile, responseLanguage = recordingResponseLanguage
        }));
    }

    private int GetRecordedFrames(AudioClip clip)
    {
        int position = Microphone.GetPosition(activeDevice);
        if (!Microphone.IsRecording(activeDevice) &&
            Time.unscaledTime - recordingStartedAt >= maximumRecordingSeconds - 0.1f)
            position = clip.samples;
        observedFrames = Mathf.Clamp(Mathf.Max(observedFrames, position), 0, clip.samples);
        return observedFrames;
    }

    private async Task SendFramesAsync(AudioClip clip, int endFrame, CancellationToken cancellation)
    {
        while (sentFrames < endFrame)
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureConnected();
            int frames = Mathf.Min(endFrame - sentFrames, clip.frequency / 4);
            float[] samples = new float[frames * clip.channels];
            if (!clip.GetData(samples, sentFrames))
                throw new InvalidOperationException("Unity could not read microphone samples.");
            await backendConnection.SendBinaryAsync(ConvertToMonoPcm16(samples, clip.channels));
            sentFrames += frames;
        }
    }

    private void EnsureConnected()
    {
        if (backendConnection == null || !backendConnection.IsReady)
            throw new InvalidOperationException("The backend connection was lost.");
    }

    private async Task SendRecordingCancellationAsync(string session)
    {
        if (!streamWhileRecording || backendConnection == null || !backendConnection.IsConnected) return;
        try
        {
            await backendConnection.SendJsonAsync("{\"type\":\"audio.cancel\",\"sessionId\":\"" + session + "\"}");
        }
        catch (Exception) { /* Connection shutdown already has its own UI state. */ }
    }

    private async void SendResponseCancellation()
    {
        try
        {
            ResponseCancelMessage message =
                new ResponseCancelMessage();

            await backendConnection.SendJsonAsync(
                JsonUtility.ToJson(message)
            );

            Debug.Log(
                "[EVA] Response cancellation sent to the backend."
            );
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[EVA] Response cancellation failed: " +
                $"{exception.Message}"
            );
        }
    }

    public string ResolveRecordingVoiceProfile()
    {
        if (EVASelectionMenu.Instance != null) return EVASelectionMenu.Instance.SelectedVoiceProfile;
        var appearance = ResolveVoiceSource();
        return appearance != null ? appearance.VoiceProfile : "female";
    }

    public EVAAppearanceController ResolveVoiceSource()
    {
        if (appearanceVoiceSource != null && appearanceVoiceSource.isActiveAndEnabled)
            return appearanceVoiceSource;
        var camera = Camera.main;
        EVAAppearanceController closest = null;
        float shortest = float.PositiveInfinity;
        foreach (var appearance in FindObjectsByType<EVAAppearanceController>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!appearance.isActiveAndEnabled || !appearance.IsConfigured) continue;
            float distance = camera != null
                ? (appearance.transform.position - camera.transform.position).sqrMagnitude : 0f;
            if (closest == null || distance < shortest)
            { closest = appearance; shortest = distance; }
        }
        return closest;
    }

    private static byte[] ConvertToMonoPcm16(
        float[] interleavedSamples,
        int sourceChannels
    )
    {
        int frameCount = interleavedSamples.Length / sourceChannels;
        byte[] pcmData = new byte[frameCount * 2];

        for (int frame = 0; frame < frameCount; frame++)
        {
            float monoSample = 0f;

            for (int channel = 0; channel < sourceChannels; channel++)
            {
                int sampleIndex = frame * sourceChannels + channel;
                monoSample += interleavedSamples[sampleIndex];
            }

            monoSample /= sourceChannels;
            monoSample = Mathf.Clamp(monoSample, -1f, 1f);

            short pcmSample = monoSample < 0f
                ? (short)Mathf.RoundToInt(monoSample * 32768f)
                : (short)Mathf.RoundToInt(monoSample * 32767f);

            int byteIndex = frame * 2;

            pcmData[byteIndex] = (byte)(pcmSample & 0xff);
            pcmData[byteIndex + 1] =
                (byte)((pcmSample >> 8) & 0xff);
        }

        return pcmData;
    }

    public void SetInteractionEnabled(bool enabled)
    {
        InteractionEnabled = enabled;

        if (!enabled)
        {
            CancelActiveRecording();
        }
    }

    private async void CancelActiveRecording()
    {
        // A released recording is already being finalized; do not dispose its
        // clip/token from a second caller while the async send is still running.
        if (!isRecording || recordingClip == null) return;
        isRecording = false;
        isSending = true;
        Microphone.End(activeDevice);
        recordingCancellation?.Cancel();
        AudioClip cancelledClip = recordingClip;
        try
        {
            await recordingStreamTask;
            await SendRecordingCancellationAsync(recordingSessionId);
        }
        finally
        {
            recordingCancellation?.Dispose();
            recordingCancellation = null;
            recordingClip = null;
            activeDevice = null;
            isSending = false;
            Destroy(cancelledClip);
        }
    }

    private void OnDisable()
    {
        pushToTalkHeld = false;
        recordingCancellation?.Cancel();
        CancelActiveRecording();
    }
}
