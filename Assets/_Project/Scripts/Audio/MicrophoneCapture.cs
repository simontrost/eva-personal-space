using System;
using System.Collections;
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

    private const int MicrophoneBufferSeconds = 4;
    private AudioClip microphoneClip;
    private float[] microphoneReadBuffer;
    private float[] recordingSamples;
    private bool preparingMicrophone;
    private bool microphoneReady;
    private int recordingReadPosition;
    private float lastCaptureTime;
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
    public bool IsMicrophoneReady => microphoneReady && microphoneClip != null;
    public string MicrophoneError { get; private set; }
    public bool CanRecord => InteractionEnabled && IsMicrophoneReady && !EVASelectionMenu.BlocksInteraction;
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

    private void OnEnable()
    {
        var launcher = FindFirstObjectByType<EVASystemLauncher>();
        if (launcher == null || launcher.IsReady) StartCoroutine(PrepareMicrophone());
    }

    public IEnumerator PrepareMicrophone()
    {
        while (preparingMicrophone) yield return null;
        if (IsMicrophoneReady) yield break;
        if (!isActiveAndEnabled)
        {
            MicrophoneError = "The microphone component is disabled.";
            yield break;
        }
        preparingMicrophone = true;
        try
        {
            if (!OpenMicrophone()) yield break;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (microphoneClip != null && Microphone.GetPosition(activeDevice) <= 0 &&
                Time.realtimeSinceStartup < deadline)
                yield return null;

            microphoneReady = microphoneClip != null && Microphone.IsRecording(activeDevice) &&
                Microphone.GetPosition(activeDevice) > 0;
            if (!microphoneReady)
            {
                MicrophoneError = "The microphone did not provide audio. Check its device and permissions.";
                Debug.LogError("[EVA] " + MicrophoneError);
                ReleaseMicrophone();
                yield break;
            }
            MicrophoneError = null;
            waitForPushToTalkRelease = true;
            Debug.Log("[EVA] Microphone prepared; push-to-talk can start without reopening the audio device.");
        }
        finally { preparingMicrophone = false; }
    }

    private bool OpenMicrophone()
    {
        ReleaseMicrophone();
        MicrophoneError = null;
        try
        {
            if (Microphone.devices.Length == 0)
                throw new InvalidOperationException("Windows did not report an available microphone.");
            activeDevice = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
            microphoneClip = Microphone.Start(activeDevice, true, MicrophoneBufferSeconds, requestedSampleRate);
            if (microphoneClip == null)
                throw new InvalidOperationException("The microphone could not be opened.");
            microphoneReadBuffer = new float[Mathf.Max(1, microphoneClip.frequency / 16) * microphoneClip.channels];
            recordingSamples = new float[microphoneClip.frequency * maximumRecordingSeconds * microphoneClip.channels];
            return true;
        }
        catch (Exception exception)
        {
            MicrophoneError = exception.Message;
            Debug.LogError("[EVA] Microphone preparation failed: " + MicrophoneError);
            ReleaseMicrophone();
            return false;
        }
    }

    private void ReleaseMicrophone()
    {
        microphoneReady = false;
        if (microphoneClip == null) return;
        Microphone.End(activeDevice);
        Destroy(microphoneClip);
        microphoneClip = null;
        microphoneReadBuffer = null;
        activeDevice = null;
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
        if (isRecording && recordingStreamError == null)
        {
            try { GetRecordedFrames(); }
            catch (Exception exception) { recordingStreamError = exception; }
        }
        if (isRecording && recordingStreamError != null)
        {
            Debug.LogError("[EVA] Recording failed: " + recordingStreamError.Message);
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

        if (!IsMicrophoneReady || !Microphone.IsRecording(activeDevice))
        {
            Debug.LogWarning("[EVA] The microphone is not ready. Check the selected device and restart Play Mode.");
            return;
        }
        if (streamingAudioPlayer != null && streamingAudioPlayer.InterruptPlayback())
            SendResponseCancellation();

        recordingReadPosition = Microphone.GetPosition(activeDevice);
        isRecording = true;
        recordingSessionId = Guid.NewGuid().ToString("N");
        recordingCancellation = new CancellationTokenSource();
        recordingStreamError = null;
        sentFrames = observedFrames = 0;
        recordingStartedAt = lastCaptureTime = Time.unscaledTime;
        streamWhileRecording = backendConnection.SupportsLiveTranscription;
        recordingVoiceProfile = ResolveRecordingVoiceProfile();
        recordingResponseLanguage = EVASelectionMenu.Instance != null
            ? EVASelectionMenu.Instance.ResponseLanguage : "en";

        ListeningStarted?.Invoke();
        recordingStreamTask = streamWhileRecording
            ? StreamRecordingAsync(recordingCancellation.Token)
            : Task.CompletedTask;

        string displayedDevice = activeDevice ?? "Windows default";

        Debug.Log(
            $"[EVA] Push-to-talk started. Device: {displayedDevice}; voice profile: {recordingVoiceProfile}"
        );
    }

    public async void EndRecordingAndSend()
    {
        if (!CanRecord) { CancelActiveRecording(); return; }
        if (!isRecording || microphoneClip == null) return;

        isSending = true;
        bool completed = false;
        try
        {
            int recordedFrames = GetRecordedFrames();
            int sampleRate = microphoneClip.frequency;
            int channels = microphoneClip.channels;
            isRecording = false;
            ProcessingStarted?.Invoke();
            await recordingStreamTask;
            if (recordingStreamError != null) throw recordingStreamError;
            if (recordedFrames <= 0) throw new InvalidOperationException("The recording contains no samples.");
            if (!streamWhileRecording)
                await SendRecordingStartAsync(sampleRate, recordedFrames * 2, false);
            await SendFramesAsync(recordedFrames, sampleRate, channels, recordingCancellation.Token);
            EnsureConnected();
            await backendConnection.SendJsonAsync(JsonUtility.ToJson(
                new AudioEndMessage { sessionId = recordingSessionId }));
            completed = true;
            Debug.Log($"[EVA] Recording sent: {(float)recordedFrames / sampleRate:F2}s, {sentFrames * 2} PCM bytes.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Debug.LogError("[EVA] Audio transmission failed: " + exception.Message);
            InteractionFailed?.Invoke();
        }
        finally
        {
            isRecording = false;
            if (!completed) await SendRecordingCancellationAsync(recordingSessionId);
            recordingCancellation?.Dispose();
            recordingCancellation = null;
            isSending = false;
        }
    }

    private async Task StreamRecordingAsync(CancellationToken cancellation)
    {
        try
        {
            int sampleRate = microphoneClip.frequency;
            int channels = microphoneClip.channels;
            await SendRecordingStartAsync(sampleRate, 0, true);
            while (isRecording)
            {
                cancellation.ThrowIfCancellationRequested();
                int frames = GetRecordedFrames();
                if (frames - sentFrames >= sampleRate / 5)
                    await SendFramesAsync(frames, sampleRate, channels, cancellation);
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

    private int GetRecordedFrames()
    {
        if (!isRecording) return observedFrames;
        if (microphoneClip == null || !Microphone.IsRecording(activeDevice))
            throw new InvalidOperationException("The microphone was disconnected or stopped.");
        CaptureMicrophoneFrames(Microphone.GetPosition(activeDevice), Time.unscaledTime);
        return observedFrames;
    }

    private void CaptureMicrophoneFrames(int position, float now)
    {
        if (position < 0 || position >= microphoneClip.samples)
            throw new InvalidOperationException("The microphone returned an invalid audio position.");
        if (now - lastCaptureTime >= (float)microphoneClip.samples / microphoneClip.frequency)
            throw new InvalidOperationException("Microphone audio could not be read in time. Please repeat this turn.");
        lastCaptureTime = now;
        int channels = microphoneClip.channels;
        int available = (position - recordingReadPosition + microphoneClip.samples) % microphoneClip.samples;
        int remaining = Mathf.Min(available, recordingSamples.Length / channels - observedFrames);
        while (remaining > 0)
        {
            int frames = Mathf.Min(remaining, microphoneReadBuffer.Length / channels);
            if (!microphoneClip.GetData(microphoneReadBuffer, recordingReadPosition))
                throw new InvalidOperationException("Unity could not read microphone samples.");
            Array.Copy(microphoneReadBuffer, 0, recordingSamples, observedFrames * channels, frames * channels);
            recordingReadPosition = (recordingReadPosition + frames) % microphoneClip.samples;
            observedFrames += frames;
            remaining -= frames;
        }
    }

    private async Task SendFramesAsync(int endFrame, int sampleRate, int channels, CancellationToken cancellation)
    {
        while (sentFrames < endFrame)
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureConnected();
            int frames = Mathf.Min(endFrame - sentFrames, sampleRate / 4);
            float[] samples = new float[frames * channels];
            Array.Copy(recordingSamples, sentFrames * channels, samples, 0, samples.Length);
            await backendConnection.SendBinaryAsync(ConvertToMonoPcm16(samples, channels));
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
        if (!isRecording) return;
        isRecording = false;
        isSending = true;
        recordingCancellation?.Cancel();
        try
        {
            await recordingStreamTask;
            await SendRecordingCancellationAsync(recordingSessionId);
        }
        finally
        {
            recordingCancellation?.Dispose();
            recordingCancellation = null;
            isSending = false;
        }
    }

    private void OnDisable()
    {
        pushToTalkHeld = false;
        recordingCancellation?.Cancel();
        CancelActiveRecording();
        StopAllCoroutines();
        preparingMicrophone = false;
        ReleaseMicrophone();
    }
}
