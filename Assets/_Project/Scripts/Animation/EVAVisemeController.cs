using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class EVAVisemeController : MonoBehaviour
{
    private static readonly string[] VisemeBlendShapeNames =
    {
        "AA_VI_00_Sil",
        "AA_VI_01_PP",
        "AA_VI_02_FF",
        "AA_VI_03_TH",
        "AA_VI_04_DD",
        "AA_VI_05_KK",
        "AA_VI_06_CH",
        "AA_VI_07_SS",
        "AA_VI_08_nn",
        "AA_VI_09_RR",
        "AA_VI_10_aa",
        "AA_VI_11_E",
        "AA_VI_12_I",
        "AA_VI_13_O",
        "AA_VI_14_U"
    };

    [Header("References")]
    [SerializeField]
    private BackendConnection backendConnection;

    [SerializeField]
    private StreamingAudioPlayer streamingAudioPlayer;

    [SerializeField]
    private SkinnedMeshRenderer faceRenderer;

    [Header("Lip Sync")]
    [SerializeField]
    [Range(0f, 100f)]
    private float maximumVisemeWeight = 100f;

    [SerializeField]
    [Range(0.01f, 0.2f)]
    private float blendDurationSeconds = 0.06f;

    [SerializeField]
    [Range(-0.2f, 0.2f)]
    private float timingOffsetSeconds;

    [Header("Debugging")]
    [SerializeField]
    private bool logVisemeMessages;

    private readonly List<ScheduledViseme> scheduledVisemes = new();

    private int[] blendShapeIndices;
    private float[] currentWeights;
    private int currentCueIndex;
    private bool blendShapesReady;
    private bool playbackActive;
    private double playbackStartedDspTime;

    public SkinnedMeshRenderer FaceRenderer => faceRenderer;

    public void SetFaceRenderer(SkinnedMeshRenderer replacement)
    {
        if (!RendererContainsAllVisemes(replacement))
        {
            throw new ArgumentException("The selected appearance needs all 15 EVA visemes.",
                nameof(replacement));
        }

        // Keep pending cues and the audio clock when changing during speech.
        ResetBlendShapeWeights();
        blendShapesReady = false;
        faceRenderer = replacement;
        ResolveBlendShapes();
    }

    [Serializable]
    private sealed class MessageEnvelope
    {
        public string type;
    }

    [Serializable]
    private sealed class TtsAudioStartMessage
    {
        public string type;
        public int sequence;
        public float streamOffsetSeconds;
        public VisemeCueMessage[] visemes;
    }

    [Serializable]
    private sealed class VisemeCueMessage
    {
        public string viseme;
        public float startTimeSeconds;
        public float durationSeconds;
    }

    private readonly struct ScheduledViseme
    {
        public ScheduledViseme(
            int visemeIndex,
            double startTimeSeconds,
            double durationSeconds
        )
        {
            VisemeIndex = visemeIndex;
            StartTimeSeconds = startTimeSeconds;
            EndTimeSeconds = startTimeSeconds + durationSeconds;
        }

        public int VisemeIndex { get; }
        public double StartTimeSeconds { get; }
        public double EndTimeSeconds { get; }
    }

    private void Awake()
    {
        if (!RendererContainsAllVisemes(faceRenderer))
        {
            faceRenderer = FindFaceRenderer();
        }

        if (backendConnection == null)
        {
            backendConnection =
                FindFirstObjectByType<BackendConnection>();
        }

        if (streamingAudioPlayer == null)
        {
            streamingAudioPlayer =
                FindFirstObjectByType<StreamingAudioPlayer>();
        }

        ResolveBlendShapes();
    }

    private void OnEnable()
    {
        if (backendConnection != null)
        {
            backendConnection.TextMessageReceived +=
                HandleTextMessage;
        }
        else
        {
            Debug.LogError(
                "[EVA] Viseme controller could not find " +
                "BackendConnection."
            );
        }

        if (streamingAudioPlayer != null)
        {
            streamingAudioPlayer.PlaybackStarted +=
                HandlePlaybackStarted;
            streamingAudioPlayer.PlaybackCompleted +=
                HandlePlaybackStopped;
            streamingAudioPlayer.PlaybackInterrupted +=
                HandlePlaybackStopped;
        }
        else
        {
            Debug.LogError(
                "[EVA] Viseme controller could not find " +
                "StreamingAudioPlayer."
            );
        }
    }

    private void Update()
    {
        if (!blendShapesReady)
        {
            return;
        }

        int targetVisemeIndex = -1;

        if (playbackActive)
        {
            double playbackTimeSeconds =
                AudioSettings.dspTime
                - playbackStartedDspTime
                + timingOffsetSeconds;

            while (
                currentCueIndex < scheduledVisemes.Count
                && playbackTimeSeconds
                    >= scheduledVisemes[currentCueIndex]
                        .EndTimeSeconds
            )
            {
                currentCueIndex++;
            }

            if (
                currentCueIndex < scheduledVisemes.Count
                && playbackTimeSeconds
                    >= scheduledVisemes[currentCueIndex]
                        .StartTimeSeconds
            )
            {
                targetVisemeIndex =
                    scheduledVisemes[currentCueIndex].VisemeIndex;
            }
            else
            {
                targetVisemeIndex = 0;
            }
        }

        UpdateBlendShapeWeights(targetVisemeIndex);
    }

    private void OnDisable()
    {
        if (backendConnection != null)
        {
            backendConnection.TextMessageReceived -=
                HandleTextMessage;
        }

        if (streamingAudioPlayer != null)
        {
            streamingAudioPlayer.PlaybackStarted -=
                HandlePlaybackStarted;
            streamingAudioPlayer.PlaybackCompleted -=
                HandlePlaybackStopped;
            streamingAudioPlayer.PlaybackInterrupted -=
                HandlePlaybackStopped;
        }

        StopAndReset();
    }

    private void ResolveBlendShapes()
    {
        if (
            faceRenderer == null
            || faceRenderer.sharedMesh == null
        )
        {
            Debug.LogError(
                "[EVA] Viseme controller requires a valid " +
                "SkinnedMeshRenderer."
            );
            return;
        }

        blendShapeIndices = new int[
            VisemeBlendShapeNames.Length
        ];

        currentWeights = new float[
            VisemeBlendShapeNames.Length
        ];

        bool allBlendShapesFound = true;

        for (
            int visemeIndex = 0;
            visemeIndex < VisemeBlendShapeNames.Length;
            visemeIndex++
        )
        {
            string blendShapeName =
                VisemeBlendShapeNames[visemeIndex];

            int blendShapeIndex = FindBlendShapeIndex(
                faceRenderer.sharedMesh,
                blendShapeName
            );

            blendShapeIndices[visemeIndex] = blendShapeIndex;

            if (blendShapeIndex >= 0)
            {
                continue;
            }

            allBlendShapesFound = false;

            Debug.LogError(
                $"[EVA] Required viseme blend shape was not " +
                $"found: {blendShapeName}"
            );
        }

        blendShapesReady = allBlendShapesFound;

        if (blendShapesReady)
        {
            ResetBlendShapeWeights();

            if (Application.isPlaying) Debug.Log(
                "[EVA] All 15 Rocketbox viseme blend shapes " +
                $"were found on '{faceRenderer.name}'."
            );
        }
    }

    private SkinnedMeshRenderer FindFaceRenderer()
    {
        SkinnedMeshRenderer rendererOnThisObject =
            GetComponent<SkinnedMeshRenderer>();

        if (RendererContainsAllVisemes(rendererOnThisObject))
        {
            return rendererOnThisObject;
        }

        Animator parentAnimator = GetComponentInParent<Animator>();
        Transform searchRoot = parentAnimator != null
            ? parentAnimator.transform
            : transform.root;

        SkinnedMeshRenderer[] renderers =
            searchRoot.GetComponentsInChildren<SkinnedMeshRenderer>(
                true
            );

        foreach (SkinnedMeshRenderer candidate in renderers)
        {
            if (RendererContainsAllVisemes(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool RendererContainsAllVisemes(
        SkinnedMeshRenderer renderer
    )
    {
        if (renderer == null || renderer.sharedMesh == null)
        {
            return false;
        }

        foreach (string requiredName in VisemeBlendShapeNames)
        {
            if (
                FindBlendShapeIndex(
                    renderer.sharedMesh,
                    requiredName
                ) < 0
            )
            {
                return false;
            }
        }

        return true;
    }

    private static int FindBlendShapeIndex(
        Mesh mesh,
        string requiredName
    )
    {
        if (mesh == null)
        {
            return -1;
        }

        int exactIndex = mesh.GetBlendShapeIndex(requiredName);

        if (exactIndex >= 0)
        {
            return exactIndex;
        }

        string requiredSuffix = "." + requiredName;

        for (
            int blendShapeIndex = 0;
            blendShapeIndex < mesh.blendShapeCount;
            blendShapeIndex++
        )
        {
            string importedName =
                mesh.GetBlendShapeName(blendShapeIndex);

            if (
                importedName.EndsWith(
                    requiredSuffix,
                    StringComparison.Ordinal
                )
            )
            {
                return blendShapeIndex;
            }
        }

        return -1;
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
            envelope = JsonUtility.FromJson<MessageEnvelope>(json);
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"[EVA] Viseme controller could not parse a " +
                $"backend message: {exception.Message}"
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
                StopAndReset();
                break;

            case "tts.audio.start":
                AppendVisemeCues(json);
                break;

            case "response.cancelled":
            case "error":
                StopAndReset();
                break;
        }
    }

    private void AppendVisemeCues(string json)
    {
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
                $"[EVA] Could not parse viseme data: " +
                $"{exception.Message}"
            );
            return;
        }

        if (
            message == null
            || message.visemes == null
            || message.visemes.Length == 0
        )
        {
            Debug.LogWarning(
                $"[EVA] TTS chunk {message?.sequence ?? -1} " +
                "did not contain viseme cues."
            );
            return;
        }

        int addedCueCount = 0;

        foreach (VisemeCueMessage cue in message.visemes)
        {
            int visemeIndex = GetVisemeNumber(cue.viseme);

            if (visemeIndex < 0)
            {
                Debug.LogWarning(
                    $"[EVA] Unknown viseme received: " +
                    $"{cue.viseme}"
                );
                continue;
            }

            double cueStartTime =
                message.streamOffsetSeconds
                + Math.Max(0f, cue.startTimeSeconds);

            double cueDuration = Math.Max(
                0.001f,
                cue.durationSeconds
            );

            scheduledVisemes.Add(
                new ScheduledViseme(
                    visemeIndex,
                    cueStartTime,
                    cueDuration
                )
            );

            addedCueCount++;
        }

        if (logVisemeMessages)
        {
            Debug.Log(
                $"[EVA] Scheduled {addedCueCount} viseme cues " +
                $"for TTS chunk {message.sequence}."
            );
        }
    }

    private static int GetVisemeNumber(string viseme)
    {
        switch (viseme)
        {
            case "Sil": return 0;
            case "PP": return 1;
            case "FF": return 2;
            case "TH": return 3;
            case "DD": return 4;
            case "KK": return 5;
            case "CH": return 6;
            case "SS": return 7;
            case "nn": return 8;
            case "RR": return 9;
            case "aa": return 10;
            case "E": return 11;
            case "I": return 12;
            case "O": return 13;
            case "U": return 14;
            default: return -1;
        }
    }

    private void HandlePlaybackStarted()
    {
        playbackStartedDspTime = AudioSettings.dspTime;
        playbackActive = true;
        currentCueIndex = 0;
    }

    private void HandlePlaybackStopped()
    {
        StopAndReset();
    }

    private void UpdateBlendShapeWeights(
        int targetVisemeIndex
    )
    {
        float weightChangePerSecond =
            maximumVisemeWeight / blendDurationSeconds;

        float maximumWeightChange =
            weightChangePerSecond * Time.deltaTime;

        for (
            int visemeIndex = 0;
            visemeIndex < blendShapeIndices.Length;
            visemeIndex++
        )
        {
            float targetWeight =
                visemeIndex == targetVisemeIndex
                    ? maximumVisemeWeight
                    : 0f;

            currentWeights[visemeIndex] = Mathf.MoveTowards(
                currentWeights[visemeIndex],
                targetWeight,
                maximumWeightChange
            );

            faceRenderer.SetBlendShapeWeight(
                blendShapeIndices[visemeIndex],
                currentWeights[visemeIndex]
            );
        }
    }

    private void StopAndReset()
    {
        playbackActive = false;
        playbackStartedDspTime = 0.0;
        currentCueIndex = 0;
        scheduledVisemes.Clear();

        if (blendShapesReady)
        {
            ResetBlendShapeWeights();
        }
    }

    private void ResetBlendShapeWeights()
    {
        if (
            faceRenderer == null
            || blendShapeIndices == null
            || currentWeights == null
        )
        {
            return;
        }

        for (
            int visemeIndex = 0;
            visemeIndex < blendShapeIndices.Length;
            visemeIndex++
        )
        {
            int blendShapeIndex = blendShapeIndices[visemeIndex];

            if (blendShapeIndex < 0)
            {
                continue;
            }

            currentWeights[visemeIndex] = 0f;

            faceRenderer.SetBlendShapeWeight(
                blendShapeIndex,
                0f
            );
        }
    }
}
