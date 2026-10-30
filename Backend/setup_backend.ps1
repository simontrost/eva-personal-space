[CmdletBinding()]
param(
    [switch]$InstallOllama,
    [switch]$RecreateEnvironment,
    [switch]$SkipModelDownloads,
    [switch]$SkipOllamaModel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$BackendDirectory = $PSScriptRoot
$EnvironmentDirectory = Join-Path $BackendDirectory ".venv"
$RequirementsFile = Join-Path $BackendDirectory "requirements.txt"
$DownloadScript = Join-Path $BackendDirectory "download_backend_models.py"
$VerificationScript = Join-Path $BackendDirectory "verify_backend.py"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$CommandArguments
    )

    & $Executable @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Executable"
    }
}

function Find-EnvironmentPython {
    $Candidates = @(
        (Join-Path $EnvironmentDirectory "python.exe"),
        (Join-Path $EnvironmentDirectory "Scripts\python.exe")
    )

    foreach ($Candidate in $Candidates) {
        if (Test-Path -LiteralPath $Candidate -PathType Leaf) {
            return $Candidate
        }
    }

    return $null
}

function Remove-PythonEnvironment {
    if (-not (Test-Path -LiteralPath $EnvironmentDirectory)) {
        return
    }

    $ExpectedPath = [System.IO.Path]::GetFullPath(
        (Join-Path $BackendDirectory ".venv")
    ).TrimEnd("\")
    $ResolvedPath = [System.IO.Path]::GetFullPath(
        $EnvironmentDirectory
    ).TrimEnd("\")

    if (-not [string]::Equals(
        $ExpectedPath,
        $ResolvedPath,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Refusing to remove an unexpected environment path: $ResolvedPath"
    }

    Write-Host "[EVA Setup] Removing the copied Python environment..."
    Remove-Item -LiteralPath $ResolvedPath -Recurse -Force
}

function Find-NvidiaSmi {
    $Command = Get-Command "nvidia-smi.exe" -ErrorAction SilentlyContinue
    if ($null -ne $Command) {
        return $Command.Source
    }

    $DefaultPath = Join-Path $env:WINDIR "System32\nvidia-smi.exe"
    if (Test-Path -LiteralPath $DefaultPath -PathType Leaf) {
        return $DefaultPath
    }

    return $null
}

function Assert-NvidiaCudaHardware {
    $NvidiaSmi = Find-NvidiaSmi
    if ($null -eq $NvidiaSmi) {
        throw (
            "No NVIDIA driver/GPU was detected. This requirements profile " +
            "is CUDA-only. Read the hardware migration section in README.md " +
            "before installing packages for an AMD, Intel, or CPU-only laptop."
        )
    }

    $GpuInformation = & $NvidiaSmi @(
        "--query-gpu=name,driver_version,memory.total",
        "--format=csv,noheader"
    )
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($GpuInformation)) {
        throw "The NVIDIA driver is installed, but no usable CUDA GPU was reported."
    }

    Write-Host "[EVA Setup] NVIDIA GPU: $GpuInformation"
}

function New-PythonEnvironment {
    $PyLauncher = Get-Command "py.exe" -ErrorAction SilentlyContinue
    if ($null -ne $PyLauncher) {
        & $PyLauncher.Source -3.11 -c "import struct, sys; assert sys.version_info[:2] == (3, 11) and struct.calcsize('P') == 8"
        if ($LASTEXITCODE -eq 0) {
            Invoke-CheckedCommand $PyLauncher.Source @(
                "-3.11",
                "-m",
                "venv",
                $EnvironmentDirectory
            )
            return
        }
    }

    $PythonCommand = Get-Command "python.exe" -ErrorAction SilentlyContinue
    if ($null -eq $PythonCommand) {
        throw "Python 3.11 x64 was not found. Install it and run this script again."
    }

    & $PythonCommand.Source -c "import struct, sys; assert sys.version_info[:2] == (3, 11) and struct.calcsize('P') == 8"
    if ($LASTEXITCODE -ne 0) {
        throw "Python 3.11 x64 is required to create Backend\.venv."
    }

    Invoke-CheckedCommand $PythonCommand.Source @(
        "-m",
        "venv",
        $EnvironmentDirectory
    )
}

function Find-OllamaExecutable {
    $Command = Get-Command "ollama.exe" -ErrorAction SilentlyContinue
    if ($null -ne $Command) {
        return $Command.Source
    }

    $DefaultPath = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
    if (Test-Path -LiteralPath $DefaultPath -PathType Leaf) {
        return $DefaultPath
    }

    return $null
}

function Test-OllamaApi {
    try {
        $RequestParameters = @{
            Uri = "http://127.0.0.1:11434/api/tags"
            Method = "Get"
            TimeoutSec = 2
        }
        $null = Invoke-RestMethod @RequestParameters
        return $true
    }
    catch {
        return $false
    }
}

function Wait-ForOllama {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OllamaExecutable
    )

    if (Test-OllamaApi) {
        return
    }

    Write-Host "[EVA Setup] Starting Ollama..."
    $StartParameters = @{
        FilePath = $OllamaExecutable
        ArgumentList = "serve"
        WindowStyle = "Hidden"
    }
    $null = Start-Process @StartParameters

    $Deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $Deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-OllamaApi) {
            return
        }
    }

    throw "Ollama did not become ready on http://127.0.0.1:11434."
}

if (-not (Test-Path -LiteralPath $RequirementsFile -PathType Leaf)) {
    throw "requirements.txt was not found beside setup_backend.ps1."
}

if (-not (Test-Path -LiteralPath $DownloadScript -PathType Leaf)) {
    throw "download_backend_models.py was not found beside setup_backend.ps1."
}

if (-not (Test-Path -LiteralPath $VerificationScript -PathType Leaf)) {
    throw "verify_backend.py was not found beside setup_backend.ps1."
}

Assert-NvidiaCudaHardware

if ($RecreateEnvironment) {
    Remove-PythonEnvironment
}

$EnvironmentPython = Find-EnvironmentPython
if ($null -eq $EnvironmentPython) {
    Write-Host "[EVA Setup] Creating a Python 3.11 environment..."
    New-PythonEnvironment
    $EnvironmentPython = Find-EnvironmentPython
}

if ($null -eq $EnvironmentPython) {
    throw "Backend\.venv was created, but its python.exe could not be found."
}

Write-Host "[EVA Setup] Using Python: $EnvironmentPython"
Invoke-CheckedCommand $EnvironmentPython @(
    "-c",
    "import struct, sys; assert sys.version_info[:2] == (3, 11) and struct.calcsize('P') == 8"
)

Write-Host "[EVA Setup] Updating Python packaging tools..."
Invoke-CheckedCommand $EnvironmentPython @(
    "-m",
    "pip",
    "install",
    "--upgrade",
    "pip",
    "setuptools",
    "wheel"
)

Write-Host "[EVA Setup] Installing the pinned EVA GPU packages..."
Invoke-CheckedCommand $EnvironmentPython @(
    "-m",
    "pip",
    "install",
    "--no-deps",
    "--requirement",
    $RequirementsFile
)

# Missing CPU ONNX Runtime is expected in this GPU-only environment.
# Query distribution metadata without pip's stderr warning, which terminates
# Windows PowerShell 5.1 when ErrorActionPreference is Stop.
$CpuOnnxRuntimeInstalled = & $EnvironmentPython -c @'
import importlib.metadata as metadata
installed_names = (
    distribution.metadata.get('Name', '').lower().replace('_', '-')
    for distribution in metadata.distributions()
)
print('onnxruntime' in installed_names)
'@
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the installed Python distributions."
}
if ($CpuOnnxRuntimeInstalled -eq 'True') {
    Write-Host "[EVA Setup] Removing the conflicting CPU ONNX Runtime..."
    Invoke-CheckedCommand $EnvironmentPython @(
        "-m",
        "pip",
        "uninstall",
        "--yes",
        "onnxruntime"
    )
}

Write-Host "[EVA Setup] Verifying the GPU ONNX Runtime installation..."
Invoke-CheckedCommand $EnvironmentPython @(
    "-m",
    "pip",
    "install",
    "--force-reinstall",
    "--no-deps",
    "onnxruntime-gpu==1.26.0"
)

if (-not $SkipModelDownloads) {
    Write-Host "[EVA Setup] Preparing Whisper and Piper model files..."
    Invoke-CheckedCommand $EnvironmentPython @($DownloadScript)
}

$OllamaExecutable = Find-OllamaExecutable
if ($null -eq $OllamaExecutable -and $InstallOllama) {
    $WingetCommand = Get-Command "winget.exe" -ErrorAction SilentlyContinue
    if ($null -eq $WingetCommand) {
        throw "winget is unavailable, so Ollama could not be installed automatically."
    }

    Write-Host "[EVA Setup] Installing Ollama..."
    Invoke-CheckedCommand $WingetCommand.Source @(
        "install",
        "--id",
        "Ollama.Ollama",
        "--exact",
        "--accept-package-agreements",
        "--accept-source-agreements"
    )
    $OllamaExecutable = Find-OllamaExecutable
}

if ($null -eq $OllamaExecutable) {
    throw (
        "Ollama was not found. Install Ollama, or run this script with " +
        "-InstallOllama."
    )
}

if (-not $SkipOllamaModel) {
    Wait-ForOllama $OllamaExecutable
    Write-Host "[EVA Setup] Downloading/checking llama3.2:3b..."
    Invoke-CheckedCommand $OllamaExecutable @("pull", "llama3.2:3b")
}

Write-Host "[EVA Setup] Running the final GPU preflight check..."
Invoke-CheckedCommand $EnvironmentPython @($VerificationScript)

Write-Host ""
Write-Host "[EVA Setup] SUCCESS: The EVA backend is ready."
Write-Host "[EVA Setup] Unity can now start Ollama and server.py automatically."
