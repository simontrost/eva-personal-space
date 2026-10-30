import os
import sys
from pathlib import Path


_DLL_HANDLES = []


def configure_cuda_dlls() -> None:
    if _DLL_HANDLES:
        return
    if os.name != "nt":
        raise RuntimeError("EVA's current GPU backend is configured for Windows only.")

    root = Path(sys.prefix) / "Lib" / "site-packages" / "nvidia"
    directories = [root / package / "bin" for package in ("cuda_runtime", "cublas", "cufft", "cudnn")]
    missing = [str(directory) for directory in directories if not directory.is_dir()]
    if missing:
        raise FileNotFoundError("Missing NVIDIA DLL directories:\n" + "\n".join(missing))

    for directory in directories:
        os.environ["PATH"] = str(directory) + os.pathsep + os.environ.get("PATH", "")
        _DLL_HANDLES.append(os.add_dll_directory(str(directory)))
