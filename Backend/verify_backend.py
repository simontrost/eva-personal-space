"""Fast, non-destructive preflight check for EVA's GPU backend."""

from __future__ import annotations

import importlib.metadata
import os
import shutil
import subprocess
import sys
from pathlib import Path

from voice_config import PIPER_FILES
from cuda_runtime import configure_cuda_dlls


BACKEND_DIRECTORY = Path(__file__).resolve().parent
MODELS_DIRECTORY = BACKEND_DIRECTORY / "models"
VOICES_DIRECTORY = BACKEND_DIRECTORY / "voices"

REQUIRED_DISTRIBUTIONS = (
    "ctranslate2",
    "faster-whisper",
    "numpy",
    "ollama",
    "onnx",
    "onnxruntime-gpu",
    "piper-tts",
    "websockets",
)

REQUIRED_VOICE_FILES = tuple(PIPER_FILES)

def fail(message: str) -> None:
    raise RuntimeError(message)


def print_package_versions() -> None:
    for distribution in REQUIRED_DISTRIBUTIONS:
        try:
            version = importlib.metadata.version(distribution)
        except importlib.metadata.PackageNotFoundError:
            fail(f"Required Python package is missing: {distribution}")
        print(f"[EVA Check] {distribution}: {version}")

    try:
        importlib.metadata.version("onnxruntime")
    except importlib.metadata.PackageNotFoundError:
        pass
    else:
        fail(
            "The CPU package 'onnxruntime' is installed alongside "
            "'onnxruntime-gpu'. Run setup_backend.ps1 again."
        )


def check_nvidia_driver() -> None:
    executable = shutil.which("nvidia-smi")
    if executable is None:
        fail("nvidia-smi was not found. Install or update the NVIDIA driver.")

    result = subprocess.run(
        [
            executable,
            "--query-gpu=name,driver_version,memory.total",
            "--format=csv,noheader",
        ],
        check=False,
        capture_output=True,
        text=True,
        timeout=15,
    )
    if result.returncode != 0 or not result.stdout.strip():
        fail("The NVIDIA driver did not report an available GPU.")

    print(f"[EVA Check] NVIDIA GPU: {result.stdout.strip()}")


def check_cuda_runtimes() -> None:
    import ctranslate2
    import onnxruntime

    device_count = ctranslate2.get_cuda_device_count()
    if device_count < 1:
        fail("CTranslate2 did not detect a CUDA GPU.")

    compute_types = ctranslate2.get_supported_compute_types("cuda", 0)
    if "float16" not in compute_types:
        fail("CUDA device 0 does not support the required float16 mode.")

    providers = onnxruntime.get_available_providers()
    if "CUDAExecutionProvider" not in providers:
        fail(
            "ONNX Runtime CUDAExecutionProvider is unavailable. "
            "Piper cannot run on the GPU."
        )

    print(f"[EVA Check] CTranslate2 CUDA devices: {device_count}")
    print(f"[EVA Check] CTranslate2 compute types: {sorted(compute_types)}")
    print(f"[EVA Check] ONNX Runtime providers: {providers}")


def check_python_imports() -> None:
    import faster_whisper  # noqa: F401
    import numpy  # noqa: F401
    import ollama  # noqa: F401
    import onnx  # noqa: F401
    import piper  # noqa: F401
    import websockets  # noqa: F401

    print("[EVA Check] All backend Python modules import successfully.")


def check_model_files() -> None:
    whisper_snapshots = list(
        MODELS_DIRECTORY.glob(
            "models--Systran--faster-whisper-small/snapshots/*/model.bin"
        )
    )
    if not whisper_snapshots:
        fail(
            "The local faster-whisper small model is missing. "
            "Run download_backend_models.py."
        )

    for file_name in REQUIRED_VOICE_FILES:
        path = VOICES_DIRECTORY / file_name
        if not path.is_file() or path.stat().st_size == 0:
            fail(f"Required Piper voice file is missing: {path}")

    print("[EVA Check] Whisper and Piper model files are available.")


def check_ollama_installation() -> None:
    candidates = [shutil.which("ollama")]

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        candidates.append(
            str(Path(local_app_data) / "Programs" / "Ollama" / "ollama.exe")
        )

    if not any(candidate and Path(candidate).is_file() for candidate in candidates):
        fail("Ollama is not installed or ollama.exe could not be found.")

    print("[EVA Check] Ollama executable is available.")

    configured_models = os.environ.get("OLLAMA_MODELS")
    model_roots = []
    if configured_models:
        model_roots.append(Path(configured_models))
    model_roots.append(Path.home() / ".ollama" / "models")

    manifest_parts = (
        "manifests",
        "registry.ollama.ai",
        "library",
        "llama3.2",
        "3b",
    )
    manifests = [root.joinpath(*manifest_parts) for root in model_roots]
    if not any(path.is_file() and path.stat().st_size > 0 for path in manifests):
        fail(
            "Ollama model 'llama3.2:3b' is missing. Run "
            "'ollama pull llama3.2:3b'."
        )

    print("[EVA Check] Ollama model llama3.2:3b is available.")


def main() -> int:
    try:
        if sys.version_info[:2] != (3, 11):
            fail(
                "Python 3.11 x64 is required. Current version: "
                f"{sys.version.split()[0]}"
            )
        if sys.maxsize <= 2**32:
            fail("64-bit Python is required.")

        print(f"[EVA Check] Python: {sys.version.split()[0]} ({sys.executable})")
        print_package_versions()
        check_nvidia_driver()
        configure_cuda_dlls()
        check_python_imports()
        check_cuda_runtimes()
        check_model_files()
        check_ollama_installation()
    except Exception as exception:
        print(f"[EVA Check] FAILED: {exception}", file=sys.stderr)
        return 1

    print("[EVA Check] SUCCESS: The local GPU backend is ready.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
