from __future__ import annotations

import shutil
from pathlib import Path

from huggingface_hub import hf_hub_download, snapshot_download

from voice_config import PIPER_FILES


BACKEND_DIRECTORY = Path(__file__).resolve().parent
MODELS_DIRECTORY = BACKEND_DIRECTORY / "models"
VOICES_DIRECTORY = BACKEND_DIRECTORY / "voices"

WHISPER_REPOSITORY = "Systran/faster-whisper-small"
PIPER_REPOSITORY = "rhasspy/piper-voices"


def download_whisper() -> None:
    MODELS_DIRECTORY.mkdir(parents=True, exist_ok=True)
    print("[EVA Setup] Checking faster-whisper small...")
    snapshot_download(
        repo_id=WHISPER_REPOSITORY,
        cache_dir=MODELS_DIRECTORY,
    )
    print("[EVA Setup] faster-whisper small is available.")


def download_piper_voices() -> None:
    VOICES_DIRECTORY.mkdir(parents=True, exist_ok=True)
    cache_directory = MODELS_DIRECTORY / "piper-voice-cache"

    for destination_name, repository_path in PIPER_FILES.items():
        destination = VOICES_DIRECTORY / destination_name

        if destination.is_file() and destination.stat().st_size > 0:
            print(f"[EVA Setup] Piper file is available: {destination_name}")
            continue

        print(f"[EVA Setup] Downloading Piper file: {destination_name}")
        cached_file = Path(
            hf_hub_download(
                repo_id=PIPER_REPOSITORY,
                filename=repository_path,
                cache_dir=cache_directory,
            )
        )
        shutil.copy2(cached_file, destination)

    print("[EVA Setup] English and German Piper voices are available.")


def main() -> None:
    download_whisper()
    download_piper_voices()


if __name__ == "__main__":
    main()
