using System;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

public sealed class BackendConnection : MonoBehaviour
{
    [Header("Connection")]
    [SerializeField]
    private string serverUrl = "ws://127.0.0.1:8765";

    [SerializeField]
    private bool connectOnStart = false;

    private WebSocket socket;
    private bool serverReady;
    private bool isShuttingDown;

    public bool IsConnected =>
        socket != null && socket.State == WebSocketState.Open;
    public bool IsReady => IsConnected && serverReady;
    public bool SupportsLiveTranscription { get; private set; }
    public string LastError { get; private set; } = "";

    [Serializable]
    private sealed class ControlMessage
    {
        public string type;
        public int protocolVersion;
        public bool liveTranscription;
    }

    public event Action Connected;
    public event Action Disconnected;
    public event Action<string> TextMessageReceived;
    public event Action<byte[]> BinaryMessageReceived;

    private async void Start()
    {
        Application.runInBackground = true;

        if (connectOnStart)
        {
            await ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        if (socket != null &&
            (socket.State == WebSocketState.Open ||
             socket.State == WebSocketState.Connecting))
        {
            Debug.LogWarning("[EVA] WebSocket is already connected or connecting.");
            return;
        }

        Debug.Log($"[EVA] Connecting to {serverUrl}...");
        serverReady = false;
        LastError = "";
        SupportsLiveTranscription = false;
        isShuttingDown = false;
        var connectingSocket = new WebSocket(serverUrl);
        socket = connectingSocket;
        connectingSocket.OnOpen += () => { if (socket == connectingSocket && !isShuttingDown) HandleOpen(); };
        connectingSocket.OnError += error => { if (socket == connectingSocket && !isShuttingDown) HandleError(error); };
        connectingSocket.OnClose += code => { if (socket == connectingSocket && !isShuttingDown) HandleClose(code); };
        connectingSocket.OnMessage += data => { if (socket == connectingSocket && !isShuttingDown) HandleMessage(data); };

        try
        {
            await connectingSocket.Connect();
        }
        catch (Exception exception)
        {
            if (!isShuttingDown) HandleError(exception.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        serverReady = false;
        if (socket == null)
        {
            return;
        }

        if (socket.State == WebSocketState.Open ||
            socket.State == WebSocketState.Connecting)
        {
            await socket.Close();
        }
    }

    public async Task SendJsonAsync(string json)
    {
        if (!IsConnected)
        {
            Debug.LogWarning(
                "[EVA] Message was not sent because the backend is disconnected.");
            return;
        }

        await socket.SendText(json);
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        if (!IsConnected)
        {
            Debug.LogWarning(
                "[EVA] Binary data was not sent because the backend is disconnected.");
            return;
        }

        await socket.Send(data);
    }

    private async void HandleOpen()
    {
        if (!IsConnected || isShuttingDown) return;
        Debug.Log("[EVA] WebSocket connection opened.");
        Connected?.Invoke();

        try
        {
            await SendJsonAsync(
                "{\"type\":\"client.hello\"," +
                "\"protocolVersion\":1," +
                "\"client\":\"OfflineEVA-Unity\"}");
        }
        catch (Exception exception)
        {
            if (!isShuttingDown) HandleError(exception.Message);
        }
    }

    private void HandleError(string errorMessage)
    {
        LastError = errorMessage;
        Debug.LogError($"[EVA] WebSocket error: {errorMessage}");
    }

    private void HandleClose(WebSocketCloseCode closeCode)
    {
        serverReady = false;
        if (closeCode != WebSocketCloseCode.Normal) LastError = "WebSocket closed: " + closeCode;
        Debug.Log($"[EVA] WebSocket connection closed: {closeCode}");
        Disconnected?.Invoke();
    }

    private void HandleMessage(byte[] data)
    {
        if (LooksLikeJson(data))
        {
            string json = Encoding.UTF8.GetString(data);
            var control = JsonUtility.FromJson<ControlMessage>(json);
            if (control != null && control.type == "server.ready")
            {
                serverReady = control.protocolVersion == 1;
                SupportsLiveTranscription = serverReady && control.liveTranscription;
                if (!serverReady) LastError = "Unsupported backend protocol version: " + control.protocolVersion;
            }
            TextMessageReceived?.Invoke(json);
            return;
        }

        BinaryMessageReceived?.Invoke(data);
    }

    private static bool LooksLikeJson(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return false;
        }

        return data[0] == (byte)'{' || data[0] == (byte)'[';
    }

    private async void OnDestroy()
    {
        isShuttingDown = true;
        await DisconnectAsync();
    }

    private void OnDisable()
    {
        isShuttingDown = true;
        serverReady = false;
        socket?.CancelConnection();
    }
}
