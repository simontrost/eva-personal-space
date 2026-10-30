using System;
using UnityEngine;

public enum EVAState
{
    Idle,
    Listening,
    Thinking,
    Speaking
}

public sealed class EVAStateController : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private BackendConnection backendConnection;

    [SerializeField]
    private MicrophoneCapture microphoneCapture;

    [SerializeField]
    private StreamingAudioPlayer streamingAudioPlayer;

    [Header("Debugging")]
    [SerializeField]
    private bool logStateChanges = true;

    public EVAState CurrentState { get; private set; } = EVAState.Idle;

    public event Action<EVAState, EVAState> StateChanged;

    [Serializable]
    private sealed class MessageEnvelope
    {
        public string type;
    }

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

        if (streamingAudioPlayer == null)
        {
            streamingAudioPlayer =
                FindFirstObjectByType<StreamingAudioPlayer>();
        }
    }

    private void OnEnable()
    {
        if (microphoneCapture != null)
        {
            microphoneCapture.ListeningStarted +=
                HandleListeningStarted;
            microphoneCapture.ProcessingStarted +=
                HandleProcessingStarted;
            microphoneCapture.InteractionFailed +=
                HandleInteractionFailed;
        }

        if (streamingAudioPlayer != null)
        {
            streamingAudioPlayer.PlaybackStarted +=
                HandlePlaybackStarted;
            streamingAudioPlayer.PlaybackCompleted +=
                HandlePlaybackCompleted;
            streamingAudioPlayer.PlaybackInterrupted +=
                HandlePlaybackInterrupted;
        }

        if (backendConnection != null)
        {
            backendConnection.TextMessageReceived +=
                HandleBackendMessage;
            backendConnection.Disconnected +=
                HandleBackendDisconnected;
        }
    }

    private void Start()
    {
        if (logStateChanges)
        {
            Debug.Log($"[EVA] State initialized: {CurrentState}");
        }
    }

    private void OnDisable()
    {
        if (microphoneCapture != null)
        {
            microphoneCapture.ListeningStarted -=
                HandleListeningStarted;
            microphoneCapture.ProcessingStarted -=
                HandleProcessingStarted;
            microphoneCapture.InteractionFailed -=
                HandleInteractionFailed;
        }

        if (streamingAudioPlayer != null)
        {
            streamingAudioPlayer.PlaybackStarted -=
                HandlePlaybackStarted;
            streamingAudioPlayer.PlaybackCompleted -=
                HandlePlaybackCompleted;
            streamingAudioPlayer.PlaybackInterrupted -=
                HandlePlaybackInterrupted;
        }

        if (backendConnection != null)
        {
            backendConnection.TextMessageReceived -=
                HandleBackendMessage;
            backendConnection.Disconnected -=
                HandleBackendDisconnected;
        }
    }

    private void HandleListeningStarted()
    {
        SetState(EVAState.Listening);
    }

    private void HandleProcessingStarted()
    {
        SetState(EVAState.Thinking);
    }

    private void HandleInteractionFailed()
    {
        SetState(EVAState.Idle);
    }

    private void HandlePlaybackStarted()
    {
        SetState(EVAState.Speaking);
    }

    private void HandlePlaybackCompleted()
    {
        SetState(EVAState.Idle);
    }

    private void HandlePlaybackInterrupted()
    {
        SetState(EVAState.Idle);
    }

    private void HandleBackendDisconnected()
    {
        SetState(EVAState.Idle);
    }

    private void HandleBackendMessage(string json)
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
                $"[EVA] State controller could not parse a backend " +
                $"message: {exception.Message}"
            );
            return;
        }

        if (envelope == null)
        {
            return;
        }

        if (
            envelope.type == "transcription.empty"
            || envelope.type == "error"
        )
        {
            SetState(EVAState.Idle);
        }
    }

    private void SetState(EVAState nextState)
    {
        if (CurrentState == nextState)
        {
            return;
        }

        EVAState previousState = CurrentState;
        CurrentState = nextState;

        if (logStateChanges)
        {
            Debug.Log(
                $"[EVA] State changed: " +
                $"{previousState} -> {nextState}"
            );
        }

        StateChanged?.Invoke(previousState, nextState);
    }
}
