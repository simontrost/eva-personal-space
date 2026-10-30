using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

[DefaultExecutionOrder(-1000)]
[DisallowMultipleComponent]
public sealed class EVASystemLauncher : MonoBehaviour
{
    private const string Host = "127.0.0.1";
    private const int OllamaPort = 11434;
    private const int BackendPort = 8765;
    private const int MaximumDisplayedLinesPerFrame = 50;

    [Header("References")]
    [SerializeField]
    private BackendConnection backendConnection;

    [Header("Startup")]
    [SerializeField]
    private bool startAutomatically = true;

    [SerializeField]
    [Min(5f)]
    private float ollamaStartupTimeoutSeconds = 30f;

    [SerializeField]
    [Min(30f)]
    private float backendStartupTimeoutSeconds = 180f;

    [SerializeField]
    [Min(1f)]
    private float connectionTimeoutSeconds = 10f;

    [Header("Optional Path Overrides")]
    [Tooltip(
        "Leave empty to use the Backend folder in the Unity " +
        "project directory."
    )]
    [SerializeField]
    private string backendDirectoryOverride = "";

    [Tooltip(
        "Leave empty to use the standard per-user Ollama " +
        "installation path."
    )]
    [SerializeField]
    private string ollamaExecutableOverride = "";

    [Header("Shutdown")]
    [SerializeField]
    private bool stopManagedBackendOnExit = true;

    [SerializeField]
    private bool stopManagedOllamaOnExit = true;

    [Header("Debugging")]
    [SerializeField]
    private bool showProcessOutput = false;

    public bool IsReady { get; private set; }
    public string CurrentStatus { get; private set; } = "Idle";

    public event Action<string> StatusChanged;

    private static EVASystemLauncher activeInstance;

    private readonly ConcurrentQueue<string> processOutput = new();
    private readonly Queue<string> recentProcessErrors = new();
    private readonly object errorQueueLock = new();

    private Coroutine startupCoroutine;
    private Process backendProcess;
    private Process ollamaProcess;
    private bool ownsBackendProcess;
    private bool ownsOllamaProcess;
    private bool isShuttingDown;

    private void Awake()
    {
        if (activeInstance != null && activeInstance != this)
        {
            Debug.LogError(
                "[EVA] Only one EVASystemLauncher may be active."
            );
            enabled = false;
            return;
        }

        activeInstance = this;
        Application.runInBackground = true;

        if (backendConnection == null)
        {
            backendConnection =
                FindFirstObjectByType<BackendConnection>();
        }
    }

    private void Start()
    {
        if (startAutomatically)
        {
            RetryStartup();
        }
    }

    private void Update()
    {
        int displayedLineCount = 0;

        while (
            displayedLineCount < MaximumDisplayedLinesPerFrame
            && processOutput.TryDequeue(out string line)
        )
        {
            Debug.Log(line);
            displayedLineCount++;
        }
    }

    public void RetryStartup()
    {
        if (isShuttingDown || startupCoroutine != null)
        {
            return;
        }

        IsReady = false;
        startupCoroutine = StartCoroutine(StartupRoutine());
    }

    private IEnumerator StartupRoutine()
    {
#if !UNITY_EDITOR_WIN && !UNITY_STANDALONE_WIN
        FailStartup(
            "Automatic process startup is currently configured " +
            "for Windows only."
        );
        yield break;
#else
        if (backendConnection == null)
        {
            FailStartup(
                "BackendConnection was not assigned and could " +
                "not be found in the scene."
            );
            yield break;
        }

        SetStatus("Checking Ollama");

        bool ollamaReady = false;
        yield return CheckOllamaReady(
            result => ollamaReady = result
        );

        if (isShuttingDown)
        {
            FinishCancelledStartup();
            yield break;
        }

        if (ollamaReady)
        {
            Debug.Log(
                "[EVA] Ollama is already running. " +
                "The existing process will be reused."
            );
        }
        else
        {
            string ollamaExecutable =
                ResolveOllamaExecutablePath();

            if (string.IsNullOrEmpty(ollamaExecutable))
            {
                FailStartup(
                    "Ollama was not found. Install Ollama or set " +
                    "Ollama Executable Override in the launcher."
                );
                yield break;
            }

            SetStatus("Starting Ollama");

            ollamaProcess = StartManagedProcess(
                "Ollama",
                ollamaExecutable,
                "serve",
                Path.GetDirectoryName(ollamaExecutable),
                false
            );

            if (ollamaProcess == null)
            {
                FailStartup("Ollama could not be started.");
                yield break;
            }

            ownsOllamaProcess = true;

            yield return WaitForOllama(
                ollamaStartupTimeoutSeconds,
                result => ollamaReady = result
            );

            if (!ollamaReady)
            {
                FailStartup(
                    BuildProcessFailureMessage(
                        "Ollama did not become ready before the " +
                        "startup timeout.",
                        ollamaProcess
                    )
                );
                yield break;
            }
        }

        if (isShuttingDown)
        {
            FinishCancelledStartup();
            yield break;
        }

        Debug.Log("[EVA] Ollama is ready.");
        SetStatus("Checking backend");

        bool backendReady = false;
        yield return CheckPortOnce(
            BackendPort,
            result => backendReady = result
        );

        if (backendReady)
        {
            Debug.Log(
                "[EVA] The backend is already running. " +
                "The existing process will be reused."
            );
        }
        else
        {
            string backendDirectory =
                ResolveBackendDirectory();

            string pythonExecutable =
                ResolvePythonExecutable(backendDirectory);

            string serverScript = Path.Combine(
                backendDirectory,
                "server.py"
            );

            if (string.IsNullOrEmpty(pythonExecutable))
            {
                FailStartup(
                    "Backend Python was not found. Expected either " +
                    "Backend\\.venv\\python.exe or " +
                    "Backend\\.venv\\Scripts\\python.exe. Run " +
                    "Backend\\setup_backend.ps1 first."
                );
                yield break;
            }

            if (!File.Exists(serverScript))
            {
                FailStartup(
                    "Backend server.py was not found at: " +
                    serverScript
                );
                yield break;
            }

            SetStatus("Starting backend CUDA warm-up");

            Debug.Log(
                "[EVA] Starting the Python backend. CUDA model " +
                "warm-up may take a while."
            );

            backendProcess = StartManagedProcess(
                "Backend",
                pythonExecutable,
                $"-u \"{serverScript}\"",
                backendDirectory,
                true
            );

            if (backendProcess == null)
            {
                FailStartup("The Python backend could not be started.");
                yield break;
            }

            ownsBackendProcess = true;

            yield return WaitForPort(
                "backend",
                BackendPort,
                backendStartupTimeoutSeconds,
                backendProcess,
                result => backendReady = result
            );

            if (!backendReady)
            {
                FailStartup(
                    BuildProcessFailureMessage(
                        "The backend did not become ready before " +
                        "the startup timeout.",
                        backendProcess
                    )
                );
                yield break;
            }
        }

        if (isShuttingDown)
        {
            FinishCancelledStartup();
            yield break;
        }

        Debug.Log("[EVA] Backend port is ready.");
        SetStatus("Connecting Unity to backend");

        yield return ConnectBackend();

        if (!backendConnection.IsReady)
        {
            FailStartup(
                BuildProcessFailureMessage(
                    "The backend did not complete its WebSocket readiness handshake. " +
                    backendConnection.LastError + " See Backend/backend-runtime.log.",
                    backendProcess
                )
            );
            yield break;
        }

        IsReady = true;
        SetStatus("Ready");
        startupCoroutine = null;

        Debug.Log(
            "[EVA] Automatic system startup completed successfully."
        );
#endif
    }

    private IEnumerator ConnectBackend()
    {
        float deadline =
            Time.realtimeSinceStartup + connectionTimeoutSeconds;

        while (
            !backendConnection.IsReady
            && Time.realtimeSinceStartup < deadline
            && !isShuttingDown
        )
        {
            Task connectionTask = backendConnection.ConnectAsync();

            while (
                !connectionTask.IsCompleted
                && !backendConnection.IsReady
                && Time.realtimeSinceStartup < deadline
                && !isShuttingDown
            )
            {
                yield return null;
            }

            if (backendConnection.IsReady)
            {
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    private IEnumerator WaitForOllama(
        float timeoutSeconds,
        Action<bool> completed
    )
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;

        while (
            Time.realtimeSinceStartup < deadline
            && !isShuttingDown
        )
        {
            bool ready = false;
            yield return CheckOllamaReady(result => ready = result);

            if (ready)
            {
                completed(true);
                yield break;
            }

            if (HasExited(ollamaProcess))
            {
                completed(false);
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }

        completed(false);
    }

    private static IEnumerator CheckOllamaReady(
        Action<bool> completed
    )
    {
        using (
            UnityWebRequest request = UnityWebRequest.Get(
                $"http://{Host}:{OllamaPort}/api/version"
            )
        )
        {
            request.timeout = 2;
            yield return request.SendWebRequest();

            completed(
                request.result == UnityWebRequest.Result.Success
                && request.responseCode == 200
            );
        }
    }

    private IEnumerator WaitForPort(
        string serviceName,
        int port,
        float timeoutSeconds,
        Process ownedProcess,
        Action<bool> completed
    )
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;

        while (
            Time.realtimeSinceStartup < deadline
            && !isShuttingDown
        )
        {
            bool ready = false;
            yield return CheckPortOnce(port, result => ready = result);

            if (ready)
            {
                completed(true);
                yield break;
            }

            if (HasExited(ownedProcess))
            {
                Debug.LogError(
                    $"[EVA] The managed {serviceName} process " +
                    "exited during startup."
                );
                completed(false);
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }

        completed(false);
    }

    private static IEnumerator CheckPortOnce(
        int port,
        Action<bool> completed
    )
    {
        Task<bool> probeTask = ProbePortAsync(port);

        while (!probeTask.IsCompleted)
        {
            yield return null;
        }

        completed(
            probeTask.Status == TaskStatus.RanToCompletion
            && probeTask.Result
        );
    }

    private static Task<bool> ProbePortAsync(int port)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        IAsyncResult connection = client.BeginConnect(
                            Host,
                            port,
                            null,
                            null
                        );

                        using (
                            WaitHandle waitHandle =
                                connection.AsyncWaitHandle
                        )
                        {
                            if (!waitHandle.WaitOne(500))
                            {
                                return false;
                            }

                            client.EndConnect(connection);
                            return client.Connected;
                        }
                    }
                }
                catch
                {
                    return false;
                }
            }
        );
    }

    private Process StartManagedProcess(
        string displayName,
        string executable,
        string arguments,
        string workingDirectory,
        bool configurePython
    )
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (configurePython)
            {
                startInfo.EnvironmentVariables[
                    "PYTHONUNBUFFERED"
                ] = "1";

                // Redirected Python output otherwise uses the Windows
                // code page, which cannot encode every generated response.
                startInfo.EnvironmentVariables[
                    "PYTHONIOENCODING"
                ] = "utf-8:backslashreplace";
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
            }

            Process process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (
                    showProcessOutput
                    && !string.IsNullOrWhiteSpace(eventArgs.Data)
                )
                {
                    processOutput.Enqueue(
                        $"[EVA/{displayName}] {eventArgs.Data}"
                    );
                }
            };

            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    return;
                }

                StoreRecentProcessError(
                    $"[{displayName}] {eventArgs.Data}"
                );

                if (showProcessOutput)
                {
                    processOutput.Enqueue(
                        $"[EVA/{displayName}] {eventArgs.Data}"
                    );
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Debug.Log(
                $"[EVA] Started {displayName} process " +
                $"with PID {process.Id}."
            );

            return process;
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[EVA] Failed to start {displayName}: " +
                exception.Message
            );
            return null;
        }
    }

    private string ResolveBackendDirectory()
    {
        if (!string.IsNullOrWhiteSpace(backendDirectoryOverride))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    backendDirectoryOverride.Trim()
                )
            );
        }

        DirectoryInfo projectOrPlayerDirectory =
            Directory.GetParent(Application.dataPath);

        return Path.Combine(
            projectOrPlayerDirectory.FullName,
            "Backend"
        );
    }

    private static string ResolvePythonExecutable(
        string backendDirectory
    )
    {
        string executable = Path.Combine(backendDirectory, ".venv", "Scripts", "python.exe");
        return File.Exists(executable) ? executable : null;
    }

    private string ResolveOllamaExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(ollamaExecutableOverride))
        {
            string overridePath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    ollamaExecutableOverride.Trim()
                )
            );

            return File.Exists(overridePath) ? overridePath : null;
        }

        string defaultPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            ),
            "Programs",
            "Ollama",
            "ollama.exe"
        );

        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private void SetStatus(string status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(status);
        Debug.Log($"[EVA] System status: {status}");
    }

    private void FailStartup(string message)
    {
        IsReady = false;
        CurrentStatus = "Failed";
        StatusChanged?.Invoke(CurrentStatus);
        startupCoroutine = null;
        Debug.LogError("[EVA] Automatic startup failed: " + message);
    }

    private void FinishCancelledStartup()
    {
        CurrentStatus = "Stopped";
        startupCoroutine = null;
    }

    private string BuildProcessFailureMessage(
        string summary,
        Process process
    )
    {
        string processDetails = "";

        if (process != null && HasExited(process))
        {
            try
            {
                processDetails = $" Exit code: {process.ExitCode}.";
            }
            catch
            {
                processDetails = " The process exited unexpectedly.";
            }
        }

        string errors = GetRecentProcessErrors();

        if (!string.IsNullOrWhiteSpace(errors))
        {
            processDetails += " Recent process errors:\n" + errors;
        }

        return summary + processDetails;
    }

    private void StoreRecentProcessError(string line)
    {
        lock (errorQueueLock)
        {
            recentProcessErrors.Enqueue(line);

            while (recentProcessErrors.Count > 20)
            {
                recentProcessErrors.Dequeue();
            }
        }
    }

    private string GetRecentProcessErrors()
    {
        lock (errorQueueLock)
        {
            return string.Join("\n", recentProcessErrors);
        }
    }

    private static bool HasExited(Process process)
    {
        if (process == null)
        {
            return false;
        }

        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private void OnApplicationQuit()
    {
        ShutDownManagedProcesses();
    }

    private void OnDisable()
    {
        // Unity calls OnDisable before an in-Play script/domain reload, without
        // necessarily calling OnDestroy. Close owned pipes/processes first.
        ShutDownManagedProcesses();
    }

    private void OnDestroy()
    {
        ShutDownManagedProcesses();

        if (activeInstance == this)
        {
            activeInstance = null;
        }
    }

    private void ShutDownManagedProcesses()
    {
        if (isShuttingDown)
        {
            return;
        }

        isShuttingDown = true;
        IsReady = false;

        if (startupCoroutine != null)
        {
            StopCoroutine(startupCoroutine);
            startupCoroutine = null;
        }

        if (stopManagedBackendOnExit && ownsBackendProcess)
        {
            StopManagedProcess("Backend", backendProcess);
        }

        if (stopManagedOllamaOnExit && ownsOllamaProcess)
        {
            StopManagedProcess("Ollama", ollamaProcess);
        }
    }

    private static void StopManagedProcess(
        string displayName,
        Process process
    )
    {
        if (process == null || HasExited(process))
        {
            return;
        }

        try
        {
            int processId = process.Id;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            // A Windows venv python.exe can launch another python.exe. Killing
            // only its parent leaves the backend listening on the old port.
            var killInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                Arguments = $"/PID {processId} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var terminate = Process.Start(killInfo))
            {
                if (terminate != null && !terminate.WaitForExit(3000)) terminate.Kill();
            }
#endif
            if (!HasExited(process)) process.Kill();

            Debug.Log(
                $"[EVA] Stopped managed {displayName} process " +
                $"with PID {processId}."
            );
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"[EVA] Could not stop managed {displayName}: " +
                exception.Message
            );
        }
    }
}
