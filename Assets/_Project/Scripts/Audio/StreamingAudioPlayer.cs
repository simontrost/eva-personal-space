using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;


[RequireComponent(typeof(AudioSource))]
public sealed class StreamingAudioPlayer : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private BackendConnection backendConnection;

    [Header("Buffering")]
    [SerializeField]
    [Range(1f, 120f)]
    private float maximumBufferedSeconds = 30f;

    [SerializeField]
    [Range(1, 5)]
    private int streamingClipSeconds = 2;

    [SerializeField]
    [Range(0f, 0.5f)]
    private float completionSafetySeconds = 0.1f;

    private readonly Queue<float[]> sampleBlocks = new();
    private readonly object sampleQueueLock = new();

    private float[] currentReadBlock;
    private int currentReadBlockIndex;

    private AudioSource audioSource;
    private AudioClip streamingClip;
    private TtsAudioStartMessage pendingAudioMessage;

    private int currentSampleRate;
    private int currentChannels;
    private int queuedSampleValueCount;
    private int pendingUnderflowCount;

    private bool playbackStarted;
    private volatile bool streamActive;
    private volatile bool streamCompleted;

    private double expectedPlaybackEndDspTime;

    public bool IsPlayingResponse =>
        playbackStarted && audioSource != null;

    public bool HasActiveResponse =>
        streamActive
        || playbackStarted
        || pendingAudioMessage != null
        || GetQueuedSampleValueCount() > 0;

    public event Action PlaybackStarted;
    public event Action PlaybackCompleted;
    public event Action PlaybackInterrupted;

    [Serializable]
    private sealed class MessageEnvelope
    {
        public string type;
    }

    [Serializable]
    private sealed class TtsAudioStartMessage
    {
        public string type;
        public string sessionId;
        public int sequence;
        public string language;
        public string voice;
        public string text;
        public string encoding;
        public int sampleRate;
        public int channels;
        public int byteCount;
    }

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();

        audioSource.playOnAwake = false;
        audioSource.loop = true;
        audioSource.spatialBlend = 0f;
    }

    private void OnEnable()
    {
        if (backendConnection == null)
        {
            Debug.LogError(
                "[EVA] BackendConnection reference is missing " +
                "on StreamingAudioPlayer."
            );
            return;
        }

        backendConnection.TextMessageReceived +=
            HandleTextMessage;

        backendConnection.BinaryMessageReceived +=
            HandleBinaryMessage;
    }

    private void OnDisable()
    {
        if (backendConnection != null)
        {
            backendConnection.TextMessageReceived -=
                HandleTextMessage;

            backendConnection.BinaryMessageReceived -=
                HandleBinaryMessage;
        }

        ResetStream();
    }

    private void Update()
    {
        int underflowCount = Interlocked.Exchange(
            ref pendingUnderflowCount,
            0
        );

        if (underflowCount > 0)
        {
            Debug.LogWarning(
                $"[EVA] The PCM stream buffer underflowed " +
                $"{underflowCount} time(s)."
            );
        }

        if (
            playbackStarted
            && streamCompleted
            && GetQueuedSampleValueCount() == 0
            && AudioSettings.dspTime >=
                expectedPlaybackEndDspTime
                + completionSafetySeconds
        )
        {
            CompletePlayback();
        }

        if (
            streamActive
            && streamCompleted
            && !playbackStarted
            && GetQueuedSampleValueCount() == 0
        )
        {
            CompletePlayback();
        }
    }

    private void HandleTextMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        MessageEnvelope envelope;

        try
        {
            envelope = JsonUtility.FromJson<MessageEnvelope>(
                json
            );
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[EVA] Could not parse backend message: " +
                $"{exception.Message}"
            );
            return;
        }

        if (envelope == null)
        {
            return;
        }

        switch (envelope.type)
        {
            case "tts.stream.started":
                BeginStream();
                break;

            case "tts.audio.start":
                HandleAudioStart(json);
                break;

            case "tts.stream.completed":
                streamCompleted = true;

                Debug.Log(
                    "[EVA] TTS audio stream received completely."
                );
                break;

            case "response.cancelled":
                ResetStream();

                Debug.Log(
                    "[EVA] Response interruption acknowledged " +
                    "by the backend."
                );
                break;
        }
    }

    public bool InterruptPlayback()
    {
        bool hadActiveResponse = HasActiveResponse;

        if (!hadActiveResponse)
        {
            return false;
        }

        ResetStream();

        Debug.Log(
            "[EVA] Active response playback interrupted."
        );

        PlaybackInterrupted?.Invoke();

        return true;
    }

    private void BeginStream()
    {
        ResetStream();

        streamActive = true;
        streamCompleted = false;

        Debug.Log("[EVA] TTS audio stream started.");
    }

    private void HandleAudioStart(string json)
    {
        if (!streamActive)
        {
            return;
        }

        TtsAudioStartMessage message;

        try
        {
            message = JsonUtility.FromJson<TtsAudioStartMessage>(
                json
            );
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[EVA] Could not parse TTS audio metadata: " +
                $"{exception.Message}"
            );
            return;
        }

        if (message == null)
        {
            return;
        }

        if (message.encoding != "pcm_s16le")
        {
            Debug.LogError(
                $"[EVA] Unsupported TTS audio encoding: " +
                $"{message.encoding}"
            );
            return;
        }

        if (message.sampleRate <= 0)
        {
            Debug.LogError(
                "[EVA] The TTS sample rate is invalid."
            );
            return;
        }

        if (message.channels <= 0)
        {
            Debug.LogError(
                "[EVA] The TTS channel count is invalid."
            );
            return;
        }

        if (!EnsureAudioFormat(
            message.sampleRate,
            message.channels
        ))
        {
            return;
        }

        pendingAudioMessage = message;

        Debug.Log(
            $"[EVA] Receiving TTS chunk {message.sequence}: " +
            $"{message.text}"
        );
    }

    private bool EnsureAudioFormat(
        int sampleRate,
        int channels
    )
    {
        if (currentSampleRate > 0 || currentChannels > 0)
        {
            if (
                currentSampleRate == sampleRate
                && currentChannels == channels
            )
            {
                return true;
            }

            if (GetQueuedSampleValueCount() > 0)
            {
                Debug.LogError(
                    "[EVA] The TTS audio format changed while " +
                    "PCM data was still buffered."
                );
                return false;
            }

            if (streamingClip != null)
            {
                DestroyStreamingClip();
            }
        }

        currentSampleRate = sampleRate;
        currentChannels = channels;

        Debug.Log(
            $"[EVA] PCM stream format accepted: " +
            $"{sampleRate} Hz, {channels} channel(s)."
        );

        return true;
    }

    private void HandleBinaryMessage(byte[] data)
    {
        if (pendingAudioMessage == null)
        {
            if (!streamActive)
            {
                return;
            }

            Debug.LogWarning(
                "[EVA] Received binary audio without " +
                "tts.audio.start metadata."
            );
            return;
        }

        TtsAudioStartMessage message = pendingAudioMessage;
        pendingAudioMessage = null;

        if (data == null || data.Length == 0)
        {
            Debug.LogWarning(
                $"[EVA] TTS chunk {message.sequence} is empty."
            );
            return;
        }

        if (data.Length % 2 != 0)
        {
            Debug.LogError(
                $"[EVA] TTS chunk {message.sequence} contains " +
                "an invalid number of PCM16 bytes."
            );
            return;
        }

        if (
            message.byteCount > 0
            && data.Length != message.byteCount
        )
        {
            Debug.LogError(
                $"[EVA] TTS chunk {message.sequence} expected " +
                $"{message.byteCount} bytes but received " +
                $"{data.Length} bytes."
            );
            return;
        }

        float[] samples = ConvertPcm16ToFloat(data);
        int maximumSampleValueCount = Mathf.CeilToInt(
            maximumBufferedSeconds
            * currentSampleRate
            * currentChannels
        );

        int queuedFrames;

        lock (sampleQueueLock)
        {
            if (
                queuedSampleValueCount + samples.Length
                > maximumSampleValueCount
            )
            {
                Debug.LogError(
                    "[EVA] The PCM stream buffer reached its " +
                    "configured capacity."
                );
                return;
            }

            sampleBlocks.Enqueue(samples);
            queuedSampleValueCount += samples.Length;
            queuedFrames =
                queuedSampleValueCount / currentChannels;
        }

        double chunkDurationSeconds =
            (double)samples.Length
            / currentChannels
            / currentSampleRate;

        if (!playbackStarted)
        {
            CreateStreamingClip();
            StartBufferedPlayback(queuedFrames);
        }
        else
        {
            expectedPlaybackEndDspTime = Math.Max(
                expectedPlaybackEndDspTime,
                AudioSettings.dspTime
            ) + chunkDurationSeconds;
        }

        Debug.Log(
            $"[EVA] Buffered TTS chunk {message.sequence}. " +
            $"Buffered audio: {GetBufferedSeconds():F3} seconds."
        );
    }

    private void CreateStreamingClip()
    {
        if (streamingClip != null)
        {
            return;
        }

        int frameCount =
            currentSampleRate * streamingClipSeconds;

        streamingClip = AudioClip.Create(
            "EVA_PCM_Stream",
            frameCount,
            currentChannels,
            currentSampleRate,
            true,
            HandleAudioRead,
            HandleAudioSetPosition
        );

        audioSource.clip = streamingClip;

        Debug.Log(
            $"[EVA] PCM streaming clip created: " +
            $"{currentSampleRate} Hz, " +
            $"{currentChannels} channel(s)."
        );
    }

    private static float[] ConvertPcm16ToFloat(
        byte[] pcmData
    )
    {
        int sampleValueCount = pcmData.Length / 2;
        float[] samples = new float[sampleValueCount];

        for (
            int sampleIndex = 0;
            sampleIndex < sampleValueCount;
            sampleIndex++
        )
        {
            int byteIndex = sampleIndex * 2;

            short signedSample = (short)(
                pcmData[byteIndex]
                | pcmData[byteIndex + 1] << 8
            );

            samples[sampleIndex] =
                signedSample / 32768f;
        }

        return samples;
    }

    private void StartBufferedPlayback(int queuedFrames)
    {
        if (audioSource == null || streamingClip == null)
        {
            return;
        }

        double bufferedSeconds =
            (double)queuedFrames / currentSampleRate;

        expectedPlaybackEndDspTime =
            AudioSettings.dspTime + bufferedSeconds;

        audioSource.Play();
        playbackStarted = true;

        Debug.Log(
            $"[EVA] Buffered PCM playback started with " +
            $"{bufferedSeconds:F3} seconds available."
        );

        PlaybackStarted?.Invoke();
    }

    private void CompletePlayback()
    {
        if (!streamActive && !playbackStarted)
        {
            return;
        }

        if (audioSource != null)
        {
            audioSource.Stop();
        }

        playbackStarted = false;
        streamActive = false;
        streamCompleted = false;
        expectedPlaybackEndDspTime = 0.0;

        Debug.Log("[EVA] Buffered PCM playback completed.");

        PlaybackCompleted?.Invoke();
    }

    private void HandleAudioRead(float[] outputSamples)
    {
        int samplesWritten = 0;

        lock (sampleQueueLock)
        {
            while (samplesWritten < outputSamples.Length)
            {
                if (
                    currentReadBlock == null
                    || currentReadBlockIndex
                        >= currentReadBlock.Length
                )
                {
                    if (sampleBlocks.Count == 0)
                    {
                        break;
                    }

                    currentReadBlock = sampleBlocks.Dequeue();
                    currentReadBlockIndex = 0;
                }

                int availableSampleValues =
                    currentReadBlock.Length
                    - currentReadBlockIndex;

                int requestedSampleValues =
                    outputSamples.Length - samplesWritten;

                int copiedSampleValues = Math.Min(
                    availableSampleValues,
                    requestedSampleValues
                );

                Array.Copy(
                    currentReadBlock,
                    currentReadBlockIndex,
                    outputSamples,
                    samplesWritten,
                    copiedSampleValues
                );

                currentReadBlockIndex += copiedSampleValues;
                samplesWritten += copiedSampleValues;
                queuedSampleValueCount -= copiedSampleValues;

                if (
                    currentReadBlockIndex
                    >= currentReadBlock.Length
                )
                {
                    currentReadBlock = null;
                    currentReadBlockIndex = 0;
                }
            }
        }

        if (
            samplesWritten < outputSamples.Length
            && streamActive
            && !streamCompleted
        )
        {
            Interlocked.Increment(
                ref pendingUnderflowCount
            );
        }

        while (samplesWritten < outputSamples.Length)
        {
            outputSamples[samplesWritten] = 0f;
            samplesWritten++;
        }
    }

    private void HandleAudioSetPosition(int position)
    {
    }

    private int GetQueuedSampleValueCount()
    {
        return Volatile.Read(ref queuedSampleValueCount);
    }

    private int GetQueuedFrameCount()
    {
        if (currentChannels <= 0)
        {
            return 0;
        }

        return GetQueuedSampleValueCount() / currentChannels;
    }

    private float GetBufferedSeconds()
    {
        if (
            currentSampleRate <= 0
            || currentChannels <= 0
        )
        {
            return 0f;
        }

        return (float)GetQueuedSampleValueCount()
            / currentChannels
            / currentSampleRate;
    }

    private void ResetStream()
    {
        pendingAudioMessage = null;

        if (audioSource != null)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }

        lock (sampleQueueLock)
        {
            sampleBlocks.Clear();
            currentReadBlock = null;
            currentReadBlockIndex = 0;
            queuedSampleValueCount = 0;
        }

        playbackStarted = false;
        streamActive = false;
        streamCompleted = false;
        expectedPlaybackEndDspTime = 0.0;

        Interlocked.Exchange(
            ref pendingUnderflowCount,
            0
        );

        DestroyStreamingClip();
    }

    private void DestroyStreamingClip()
    {
        if (streamingClip == null)
        {
            return;
        }

        Destroy(streamingClip);
        streamingClip = null;
        currentSampleRate = 0;
        currentChannels = 0;
    }
}
