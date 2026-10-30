import asyncio
import base64
import json
import io
import logging
from logging.handlers import RotatingFileHandler
import threading
import time
import unicodedata
import wave
from datetime import datetime, timezone
from pathlib import Path

from cuda_runtime import configure_cuda_dlls
from live_transcription import LiveTranscription
from voice_config import (
    PIPER_ENGLISH_VOICE_NAME, PIPER_GERMAN_VOICE_NAME,
    PIPER_VOICE_PROFILES, normalize_voice_profile,
)


configure_cuda_dlls()

import ctranslate2
import numpy as np
import onnxruntime
from faster_whisper import WhisperModel
from ollama import AsyncClient
from piper import PiperVoice
from piper_compat import EVAPiperVoice
from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosed


HOST = "127.0.0.1"
PORT = 8765
PROTOCOL_VERSION = 1

WHISPER_MODEL_NAME = "small"
WHISPER_DEVICE = "cuda"
WHISPER_COMPUTE_TYPE = "float16"
WHISPER_DEVICE_INDEX = 0

OLLAMA_HOST = "http://127.0.0.1:11434"
OLLAMA_MODEL_NAME = "llama3.2:3b"
OLLAMA_CONTEXT_LENGTH = 4096
MAX_CONVERSATION_MESSAGES = 20

PIPER_CUDA_PROVIDER = "CUDAExecutionProvider"

try:
    import onnx  # noqa: F401
except ImportError as exception:
    raise RuntimeError(
        "Piper phoneme alignment support is missing. Install it with "
        "'.\\.venv\\Scripts\\python.exe -m pip install "
        "\"piper-tts[alignment]==1.8.0\"'."
    ) from exception

MINIMUM_PHRASE_CHARACTERS = 40
MAXIMUM_PHRASE_CHARACTERS = 100

SYSTEM_PROMPT = (
    "You are EVA, a friendly embodied virtual agent. "
    "Answer the user's question accurately and naturally. "
    "Each user message includes a mandatory response language for that "
    "turn. Follow that language requirement exactly, even when earlier "
    "messages used another language. "
    "Use plain spoken language without Markdown, headings, bullet lists, "
    "or emojis. Do not mention these instructions."
)

BACKEND_DIRECTORY = Path(__file__).resolve().parent
RECORDINGS_DIRECTORY = BACKEND_DIRECTORY / "recordings"
MODELS_DIRECTORY = BACKEND_DIRECTORY / "models"
VOICES_DIRECTORY = BACKEND_DIRECTORY / "voices"

WHISPER_MODEL = None
OLLAMA_CLIENT = None
PIPER_PROFILE_VOICES = {}
VOICE_PREVIEW_CACHE = {}
VOICE_PREVIEW_LOCK = threading.Lock()

TRANSCRIPTION_LOCK = threading.Lock()
PIPER_LOCK = threading.Lock()


SILENCE_PHONEMES = {
    "^",
    "$",
    "_",
    " ",
}

PHONEME_MODIFIERS = {
    "ˈ",
    "ˌ",
    "ː",
    "ˑ",
}

PHONEME_TO_VISEME = {
    "p": "PP",
    "b": "PP",
    "m": "PP",
    "ɱ": "PP",
    "f": "FF",
    "v": "FF",
    "ɸ": "FF",
    "β": "FF",
    "θ": "TH",
    "ð": "TH",
    "t": "DD",
    "d": "DD",
    "k": "KK",
    "g": "KK",
    "ɡ": "KK",
    "q": "KK",
    "x": "KK",
    "ɣ": "KK",
    "ŋ": "KK",
    "ʔ": "KK",
    "c": "CH",
    "ɟ": "CH",
    "ʃ": "CH",
    "ʒ": "CH",
    "ɕ": "CH",
    "ʑ": "CH",
    "s": "SS",
    "z": "SS",
    "ʂ": "SS",
    "ʐ": "SS",
    "n": "nn",
    "l": "nn",
    "ɲ": "nn",
    "ɳ": "nn",
    "ɴ": "nn",
    "r": "RR",
    "ɹ": "RR",
    "ɾ": "RR",
    "ɻ": "RR",
    "ʀ": "RR",
    "ʁ": "RR",
    "a": "aa",
    "ɑ": "aa",
    "ɒ": "aa",
    "æ": "aa",
    "ɐ": "aa",
    "e": "E",
    "ɛ": "E",
    "ə": "E",
    "ɜ": "E",
    "ɞ": "E",
    "ɚ": "E",
    "ʌ": "E",
    "ɘ": "E",
    "ɤ": "E",
    "i": "I",
    "ɪ": "I",
    "ɨ": "I",
    "ɩ": "I",
    "j": "I",
    "o": "O",
    "ɔ": "O",
    "ø": "O",
    "œ": "O",
    "u": "U",
    "ʊ": "U",
    "y": "U",
    "ʏ": "U",
    "ɯ": "U",
    "w": "U",
    "h": "aa",
    "ɦ": "aa",
}


def map_phoneme_to_viseme(phoneme: str):
    if not phoneme:
        return "Sil"

    if phoneme in SILENCE_PHONEMES:
        return "Sil"

    if all(
        character.isspace()
        or unicodedata.category(character).startswith("P")
        for character in phoneme
    ):
        return "Sil"

    meaningful_characters = [
        character
        for character in phoneme
        if character not in PHONEME_MODIFIERS
        and not unicodedata.combining(character)
    ]

    if not meaningful_characters:
        return None

    for character in meaningful_characters:
        viseme = PHONEME_TO_VISEME.get(character)

        if viseme is not None:
            return viseme

    return "aa"


def append_viseme_cue(
    cues: list,
    viseme,
    start_time_seconds: float,
    duration_seconds: float,
) -> None:
    if duration_seconds <= 0:
        return

    cue_end_time = start_time_seconds + duration_seconds

    if viseme is None:
        if cues:
            previous_cue = cues[-1]
            previous_cue["durationSeconds"] = (
                cue_end_time
                - previous_cue["startTimeSeconds"]
            )
            return

        viseme = "Sil"

    if cues:
        previous_cue = cues[-1]
        previous_end_time = (
            previous_cue["startTimeSeconds"]
            + previous_cue["durationSeconds"]
        )

        if (
            previous_cue["viseme"] == viseme
            and abs(previous_end_time - start_time_seconds) < 0.001
        ):
            previous_cue["durationSeconds"] = (
                cue_end_time
                - previous_cue["startTimeSeconds"]
            )
            return

    cues.append(
        {
            "viseme": viseme,
            "startTimeSeconds": start_time_seconds,
            "durationSeconds": duration_seconds,
        }
    )


def build_viseme_timeline(
    audio_chunks: list,
    sample_rate: int,
) -> list:
    cues = []
    stream_sample_offset = 0

    for audio_chunk in audio_chunks:
        alignments = audio_chunk.phoneme_alignments

        if not alignments:
            raise RuntimeError(
                "Piper did not return phoneme alignments. Make sure "
                "piper-tts alignment support is installed and enabled."
            )

        chunk_sample_count = len(audio_chunk.audio_int16_array)
        alignment_sample_offset = 0

        for alignment in alignments:
            alignment_sample_count = max(
                0,
                int(alignment.num_samples),
            )

            available_sample_count = max(
                0,
                chunk_sample_count - alignment_sample_offset,
            )

            cue_sample_count = min(
                alignment_sample_count,
                available_sample_count,
            )

            if cue_sample_count > 0:
                cue_start_time = (
                    stream_sample_offset
                    + alignment_sample_offset
                ) / sample_rate

                cue_duration = cue_sample_count / sample_rate

                append_viseme_cue(
                    cues,
                    map_phoneme_to_viseme(alignment.phoneme),
                    cue_start_time,
                    cue_duration,
                )

            alignment_sample_offset += alignment_sample_count

        if alignment_sample_offset < chunk_sample_count:
            tail_start_time = (
                stream_sample_offset + alignment_sample_offset
            ) / sample_rate

            tail_duration = (
                chunk_sample_count - alignment_sample_offset
            ) / sample_rate

            append_viseme_cue(
                cues,
                "Sil",
                tail_start_time,
                tail_duration,
            )

        stream_sample_offset += chunk_sample_count

    return [
        {
            "viseme": cue["viseme"],
            "startTimeSeconds": round(
                cue["startTimeSeconds"],
                4,
            ),
            "durationSeconds": round(
                cue["durationSeconds"],
                4,
            ),
        }
        for cue in cues
    ]


class PhraseChunker:
    def __init__(
        self,
        minimum_characters: int,
        maximum_characters: int,
    ):
        if minimum_characters < 1:
            raise ValueError(
                "minimum_characters must be at least 1."
            )

        if maximum_characters < minimum_characters:
            raise ValueError(
                "maximum_characters must be greater than or equal "
                "to minimum_characters."
            )

        self.minimum_characters = minimum_characters
        self.maximum_characters = maximum_characters
        self.buffer = ""

    def add(self, text: str) -> list[str]:
        if text:
            self.buffer += text

        return self._extract_ready_phrases(final=False)

    def finish(self) -> list[str]:
        return self._extract_ready_phrases(final=True)

    def _extract_ready_phrases(
        self,
        final: bool,
    ) -> list[str]:
        phrases = []

        while self.buffer:
            self.buffer = self.buffer.lstrip()

            if not self.buffer:
                break

            if (
                not final
                and len(self.buffer) < self.minimum_characters
            ):
                break

            boundary = self._find_sentence_boundary()

            if boundary is None:
                if len(self.buffer) >= self.maximum_characters:
                    boundary = self._find_word_boundary()
                elif final:
                    boundary = len(self.buffer)
                else:
                    break

            phrase = self.buffer[:boundary].strip()
            self.buffer = self.buffer[boundary:].lstrip()

            if phrase:
                phrases.append(phrase)

        return phrases

    def _find_sentence_boundary(self):
        search_end = min(
            len(self.buffer),
            self.maximum_characters,
        )

        for index in range(
            self.minimum_characters - 1,
            search_end,
        ):
            if self.buffer[index] not in ".!?;:":
                continue

            boundary = index + 1

            while (
                boundary < search_end
                and self.buffer[boundary] in "\"')]}"
            ):
                boundary += 1

            if (
                boundary == len(self.buffer)
                or self.buffer[boundary].isspace()
            ):
                return boundary

        return None

    def _find_word_boundary(self) -> int:
        boundary = self.buffer.rfind(
            " ",
            self.minimum_characters,
            self.maximum_characters + 1,
        )

        if boundary >= self.minimum_characters:
            return boundary

        return self.maximum_characters


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat()


def load_whisper_model() -> None:
    global WHISPER_MODEL

    print(f"[EVA] CTranslate2 version: {ctranslate2.__version__}")

    device_count = ctranslate2.get_cuda_device_count()
    print(f"[EVA] Detected CUDA devices: {device_count}")

    if device_count < 1:
        raise RuntimeError(
            "No CUDA GPU was detected. CPU fallback is disabled."
        )

    compute_types = ctranslate2.get_supported_compute_types(
        "cuda",
        WHISPER_DEVICE_INDEX,
    )

    print(
        f"[EVA] Supported CUDA compute types: "
        f"{sorted(compute_types)}"
    )

    if WHISPER_COMPUTE_TYPE not in compute_types:
        raise RuntimeError(
            f"CUDA device {WHISPER_DEVICE_INDEX} does not support "
            f"{WHISPER_COMPUTE_TYPE}. CPU fallback is disabled."
        )

    MODELS_DIRECTORY.mkdir(parents=True, exist_ok=True)

    print(
        f"[EVA] Loading faster-whisper model "
        f"'{WHISPER_MODEL_NAME}' on CUDA with "
        f"{WHISPER_COMPUTE_TYPE}..."
    )

    WHISPER_MODEL = WhisperModel(
        WHISPER_MODEL_NAME,
        device=WHISPER_DEVICE,
        device_index=WHISPER_DEVICE_INDEX,
        compute_type=WHISPER_COMPUTE_TYPE,
        download_root=str(MODELS_DIRECTORY),
        local_files_only=True,
    )

    print("[EVA] Running Whisper CUDA warm-up...")

    silence = np.zeros(16000, dtype=np.float32)

    warmup_segments, _ = WHISPER_MODEL.transcribe(
        silence,
        language="en",
        beam_size=1,
        vad_filter=False,
        condition_on_previous_text=False,
    )

    list(warmup_segments)

    print(
        f"[EVA] Whisper is ready on CUDA with "
        f"{WHISPER_COMPUTE_TYPE}."
    )


async def initialize_ollama() -> None:
    global OLLAMA_CLIENT

    OLLAMA_CLIENT = AsyncClient(host=OLLAMA_HOST)

    print(
        f"[EVA] Loading Ollama model "
        f"'{OLLAMA_MODEL_NAME}'..."
    )
    print("[EVA] Running Ollama warm-up...")

    await OLLAMA_CLIENT.chat(
        model=OLLAMA_MODEL_NAME,
        messages=[
            {
                "role": "system",
                "content": SYSTEM_PROMPT,
            },
            {
                "role": "user",
                "content": "Reply only with OK.",
            },
        ],
        stream=False,
        keep_alive=-1,
        options={
            "num_ctx": OLLAMA_CONTEXT_LENGTH,
        },
    )

    running_models = await OLLAMA_CLIENT.ps()
    loaded_model = None

    for model in running_models.models:
        current_name = (
            getattr(model, "model", "")
            or getattr(model, "name", "")
        )

        if current_name == OLLAMA_MODEL_NAME:
            loaded_model = model
            break

    if loaded_model is None:
        raise RuntimeError(
            f"Ollama model '{OLLAMA_MODEL_NAME}' is not loaded."
        )

    model_size = int(getattr(loaded_model, "size", 0) or 0)
    vram_size = int(
        getattr(loaded_model, "size_vram", 0) or 0
    )

    if model_size <= 0 or vram_size <= 0:
        raise RuntimeError(
            "Ollama did not report valid GPU memory usage."
        )

    gpu_ratio = vram_size / model_size
    gpu_percentage = min(100.0, gpu_ratio * 100.0)

    print(
        f"[EVA] Ollama GPU allocation: "
        f"{gpu_percentage:.1f}%"
    )

    if gpu_ratio < 0.99:
        raise RuntimeError(
            "The Ollama model is not fully loaded on the GPU. "
            "CPU fallback is disabled for this project."
        )

    print(
        f"[EVA] Ollama is ready with "
        f"'{OLLAMA_MODEL_NAME}' on the GPU."
    )


def load_piper_voices() -> None:
    global PIPER_PROFILE_VOICES

    if hasattr(onnxruntime, "preload_dlls"):
        onnxruntime.preload_dlls(directory="")

    print(
        f"[EVA] ONNX Runtime version: "
        f"{onnxruntime.__version__}"
    )

    available_providers = onnxruntime.get_available_providers()

    print(
        f"[EVA] Available ONNX Runtime providers: "
        f"{available_providers}"
    )

    if PIPER_CUDA_PROVIDER not in available_providers:
        raise RuntimeError(
            "CUDAExecutionProvider is unavailable for Piper. "
            "CPU fallback is disabled for this project."
        )

    voice_definitions = [
        (profile, language, voice_name,
         "Hallo, ich bin EVA." if language == "de" else "Hello, I am EVA.")
        for profile, voices in PIPER_VOICE_PROFILES.items()
        for language, voice_name in voices.items()
    ]

    loaded_voices = {}

    for profile, language, voice_name, warmup_text in voice_definitions:
        model_path = VOICES_DIRECTORY / f"{voice_name}.onnx"

        if not model_path.is_file():
            raise FileNotFoundError(
                f"Piper voice model not found: {model_path}"
            )

        print(
            f"[EVA] Loading Piper voice "
            f"'{voice_name}' on CUDA..."
        )

        voice = EVAPiperVoice.load(
            model_path,
            use_cuda=True,
            include_alignments=True,
        )

        session_providers = voice.session.get_providers()

        print(
            f"[EVA] Piper providers for '{voice_name}': "
            f"{session_providers}"
        )

        if (
            not session_providers
            or session_providers[0] != PIPER_CUDA_PROVIDER
        ):
            raise RuntimeError(
                f"Piper voice '{voice_name}' is not using CUDA. "
                "CPU fallback is disabled for this project."
            )

        print(
            f"[EVA] Warming up Piper voice "
            f"'{voice_name}'..."
        )

        warmup_chunks = list(
            voice.synthesize(
                warmup_text,
                include_alignments=True,
            )
        )

        if (
            not warmup_chunks
            or any(
                not chunk.phoneme_alignments
                for chunk in warmup_chunks
            )
        ):
            raise RuntimeError(
                f"Piper voice '{voice_name}' did not provide "
                "phoneme alignments."
            )

        print(
            f"[EVA] Piper phoneme alignments are ready for "
            f"'{voice_name}'."
        )

        loaded_voices.setdefault(profile, {})[language] = voice

    PIPER_PROFILE_VOICES = loaded_voices

    print(
        "[EVA] All four Piper voices and phoneme alignments "
        "are ready on CUDA."
    )


def transcribe_audio(file_path: Path, language: str = "en") -> dict:
    return transcribe_source(str(file_path), language)


def transcribe_pcm(pcm: bytes, sample_rate: int, language: str) -> str:
    # An in-memory WAV lets PyAV use the same resampler as final transcription.
    source = io.BytesIO()
    with wave.open(source, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(pcm)
    source.seek(0)
    return transcribe_source(source, language, partial=True)["text"]


def transcribe_source(source, language: str, partial: bool = False) -> dict:
    if WHISPER_MODEL is None:
        raise RuntimeError("The Whisper model is not loaded.")

    started_at = time.perf_counter()
    selected_language = normalize_response_language(language)

    with TRANSCRIPTION_LOCK:
        segments, information = WHISPER_MODEL.transcribe(
            source,
            language=selected_language,
            beam_size=1 if partial else 5,
            vad_filter=True,
            vad_parameters={
                "min_silence_duration_ms": 200 if partial else 500,
            },
            condition_on_previous_text=False,
        )

        text_parts = []
        segment_results = []

        for segment in segments:
            segment_text = segment.text.strip()

            if not segment_text:
                continue

            text_parts.append(segment_text)

            segment_results.append(
                {
                    "start": round(segment.start, 3),
                    "end": round(segment.end, 3),
                    "text": segment_text,
                }
            )

    elapsed_seconds = time.perf_counter() - started_at

    language_probability = getattr(
        information,
        "language_probability",
        None,
    )

    return {
        "text": " ".join(text_parts).strip(),
        "language": selected_language,
        "languageProbability": (
            round(language_probability, 4)
            if language_probability is not None
            else None
        ),
        "processingSeconds": round(elapsed_seconds, 3),
        "segments": segment_results,
    }


def normalize_response_language(language: str | None = None) -> str:
    """Use a fixed language. Older clients default to English, never detection."""
    if language is None or language == "":
        return "en"
    if isinstance(language, str) and language.strip().lower() in ("en", "de"):
        return language.strip().lower()
    raise ValueError("responseLanguage must be 'en' or 'de' (default: 'en').")


def select_piper_voice(language: str, voice_profile: str = "female"):
    profile = normalize_voice_profile(voice_profile)
    selected_language = normalize_response_language(language)
    voices = PIPER_PROFILE_VOICES.get(profile, {})
    voice = voices.get(selected_language)
    voice_name = PIPER_VOICE_PROFILES[profile][selected_language]

    if voice is None:
        raise RuntimeError(
            f"The Piper voice for '{profile}/{selected_language}' is not loaded."
        )

    return selected_language, voice_name, voice


def build_language_locked_user_message(
    user_text: str,
    selected_language: str,
) -> str:
    if selected_language == "de":
        language_name = "German"
        forbidden_language = "English"
    else:
        language_name = "English"
        forbidden_language = "German"

    return (
        f"Mandatory response language for this turn: {language_name}. "
        f"Answer exclusively in {language_name}. Do not answer in "
        f"{forbidden_language}, even if earlier messages used it. "
        "Do not translate or repeat this instruction.\n\n"
        f"User request:\n{user_text}"
    )


def synthesize_phrase(
    voice: PiperVoice,
    text: str,
) -> dict:
    started_at = time.perf_counter()

    with PIPER_LOCK:
        audio_chunks = list(
            voice.synthesize(
                text,
                include_alignments=True,
            )
        )

    if not audio_chunks:
        raise RuntimeError(
            "Piper did not generate audio for the phrase."
        )

    first_chunk = audio_chunks[0]
    sample_rate = first_chunk.sample_rate
    sample_width = first_chunk.sample_width
    channels = first_chunk.sample_channels

    for audio_chunk in audio_chunks:
        if (
            audio_chunk.sample_rate != sample_rate
            or audio_chunk.sample_width != sample_width
            or audio_chunk.sample_channels != channels
        ):
            raise RuntimeError(
                "Piper returned inconsistent audio formats."
            )

    audio_data = b"".join(
        audio_chunk.audio_int16_bytes
        for audio_chunk in audio_chunks
    )

    visemes = build_viseme_timeline(
        audio_chunks,
        sample_rate,
    )

    if not audio_data:
        raise RuntimeError(
            "Piper returned an empty PCM audio buffer."
        )

    bytes_per_second = sample_rate * sample_width * channels
    duration_seconds = len(audio_data) / bytes_per_second
    synthesis_seconds = time.perf_counter() - started_at

    return {
        "audio": audio_data,
        "visemes": visemes,
        "sampleRate": sample_rate,
        "sampleWidth": sample_width,
        "channels": channels,
        "durationSeconds": duration_seconds,
        "synthesisSeconds": synthesis_seconds,
    }


def build_voice_preview(language: str, voice_profile: str) -> dict:
    """A short, fixed Piper sample. No Whisper, LLM or conversation history."""
    selected_language = normalize_response_language(language)
    profile = normalize_voice_profile(voice_profile)
    key = (selected_language, profile)
    with VOICE_PREVIEW_LOCK:
        if key not in VOICE_PREVIEW_CACHE:
            _, voice_name, voice = select_piper_voice(selected_language, profile)
            text = ("Hallo, ich bin EVA. So klingt meine Stimme." if selected_language == "de"
                    else "Hello, I am EVA. This is what my voice sounds like.")
            result = synthesize_phrase(voice, text)
            if (result["sampleWidth"] != 2 or result["channels"] != 1
                    or not 0 < result["durationSeconds"] <= 15):
                raise RuntimeError("The voice preview has an unsupported audio format or duration.")
            VOICE_PREVIEW_CACHE[key] = {
                "language": selected_language, "voiceProfile": profile, "voice": voice_name,
                "encoding": "pcm_s16le", "sampleRate": result["sampleRate"], "channels": 1,
                "byteCount": len(result["audio"]), "durationSeconds": result["durationSeconds"],
                "audioBase64": base64.b64encode(result["audio"]).decode("ascii"),
            }
        return VOICE_PREVIEW_CACHE[key].copy()


def trim_conversation_history(
    conversation_history: list,
) -> None:
    maximum_length = MAX_CONVERSATION_MESSAGES + 1

    if len(conversation_history) <= maximum_length:
        return

    messages_to_remove = (
        len(conversation_history) - maximum_length
    )

    del conversation_history[
        1:1 + messages_to_remove
    ]


async def send_json(connection, payload: dict) -> None:
    message = json.dumps(
        payload,
        ensure_ascii=False,
        separators=(",", ":"),
    )

    await connection.send(message)


async def synthesize_and_send_phrase(
    connection,
    session_id: str,
    sequence: int,
    language: str,
    voice_name: str,
    voice: PiperVoice,
    phrase: str,
    stream_offset_seconds: float,
) -> dict:
    print(
        f"[EVA] Synthesizing TTS chunk {sequence}: "
        f"{phrase}"
    )

    result = await asyncio.to_thread(
        synthesize_phrase,
        voice,
        phrase,
    )

    audio_data = result.pop("audio")
    visemes = result.pop("visemes")

    await send_json(
        connection,
        {
            "type": "tts.audio.start",
            "sessionId": session_id,
            "sequence": sequence,
            "language": language,
            "voice": voice_name,
            "text": phrase,
            "encoding": "pcm_s16le",
            "sampleRate": result["sampleRate"],
            "channels": result["channels"],
            "byteCount": len(audio_data),
            "durationSeconds": round(
                result["durationSeconds"],
                3,
            ),
            "synthesisSeconds": round(
                result["synthesisSeconds"],
                3,
            ),
            "streamOffsetSeconds": round(
                stream_offset_seconds,
                4,
            ),
            "visemes": visemes,
        },
    )

    await connection.send(audio_data)

    await send_json(
        connection,
        {
            "type": "tts.audio.end",
            "sessionId": session_id,
            "sequence": sequence,
        },
    )

    print(
        f"[EVA] Sent TTS chunk {sequence}: "
        f"{len(audio_data)} PCM bytes, "
        f"{result['durationSeconds']:.3f} seconds of audio."
    )

    return result


async def stream_ollama_response(
    connection,
    session_id: str,
    user_text: str,
    conversation_history: list,
    voice_profile: str = "female",
    response_language: str | None = None,
) -> None:
    if OLLAMA_CLIENT is None:
        raise RuntimeError("The Ollama client is not initialized.")

    (
        selected_language,
        voice_name,
        voice,
    ) = select_piper_voice(
        normalize_response_language(response_language),
        voice_profile,
    )

    conversation_history.append(
        {
            "role": "user",
            "content": user_text,
        }
    )

    trim_conversation_history(conversation_history)

    ollama_messages = [
        message.copy()
        for message in conversation_history
    ]
    ollama_messages[-1]["content"] = (
        build_language_locked_user_message(
            user_text,
            selected_language,
        )
    )

    await send_json(
        connection,
        {
            "type": "llm.started",
            "sessionId": session_id,
            "model": OLLAMA_MODEL_NAME,
            "language": selected_language,
        },
    )

    await send_json(
        connection,
        {
            "type": "tts.stream.started",
            "sessionId": session_id,
            "language": selected_language,
            "voice": voice_name,
            "minimumPhraseCharacters": (
                MINIMUM_PHRASE_CHARACTERS
            ),
            "maximumPhraseCharacters": (
                MAXIMUM_PHRASE_CHARACTERS
            ),
        },
    )

    print(f"[EVA] Sending transcript to Ollama: {user_text}")
    print(
        f"[EVA] Selected Piper voice: "
        f"{voice_name}"
    )
    print(
        f"[EVA] Required response language: "
        f"{selected_language}"
    )

    phrase_chunker = PhraseChunker(
        MINIMUM_PHRASE_CHARACTERS,
        MAXIMUM_PHRASE_CHARACTERS,
    )

    started_at = time.perf_counter()
    first_token_at = None
    first_audio_at = None
    token_index = 0
    tts_chunk_index = 0
    total_audio_seconds = 0.0
    total_synthesis_seconds = 0.0
    response_parts = []

    try:
        stream = await OLLAMA_CLIENT.chat(
            model=OLLAMA_MODEL_NAME,
            messages=ollama_messages,
            stream=True,
            keep_alive=-1,
            options={
                "num_ctx": OLLAMA_CONTEXT_LENGTH,
            },
        )

        async for part in stream:
            token = part.message.content

            if not token:
                continue

            if first_token_at is None:
                first_token_at = time.perf_counter()

            response_parts.append(token)

            await send_json(
                connection,
                {
                    "type": "llm.token",
                    "sessionId": session_id,
                    "index": token_index,
                    "text": token,
                },
            )

            token_index += 1

            ready_phrases = phrase_chunker.add(token)

            for phrase in ready_phrases:
                result = await synthesize_and_send_phrase(
                    connection,
                    session_id,
                    tts_chunk_index,
                    selected_language,
                    voice_name,
                    voice,
                    phrase,
                    total_audio_seconds,
                )

                if first_audio_at is None:
                    first_audio_at = time.perf_counter()

                total_audio_seconds += result["durationSeconds"]
                total_synthesis_seconds += result[
                    "synthesisSeconds"
                ]
                tts_chunk_index += 1

        final_phrases = phrase_chunker.finish()

        for phrase in final_phrases:
            result = await synthesize_and_send_phrase(
                connection,
                session_id,
                tts_chunk_index,
                selected_language,
                voice_name,
                voice,
                phrase,
                total_audio_seconds,
            )

            if first_audio_at is None:
                first_audio_at = time.perf_counter()

            total_audio_seconds += result["durationSeconds"]
            total_synthesis_seconds += result[
                "synthesisSeconds"
            ]
            tts_chunk_index += 1

    except asyncio.CancelledError:
        if (
            conversation_history
            and conversation_history[-1]["role"] == "user"
        ):
            conversation_history.pop()

        raise

    except Exception:
        if (
            conversation_history
            and conversation_history[-1]["role"] == "user"
        ):
            conversation_history.pop()

        raise

    finished_at = time.perf_counter()
    complete_response = "".join(response_parts).strip()

    conversation_history.append(
        {
            "role": "assistant",
            "content": complete_response,
        }
    )

    trim_conversation_history(conversation_history)

    first_token_seconds = (
        first_token_at - started_at
        if first_token_at is not None
        else None
    )

    first_audio_seconds = (
        first_audio_at - started_at
        if first_audio_at is not None
        else None
    )

    total_seconds = finished_at - started_at

    print(
        f"[EVA] Ollama and TTS streaming completed in "
        f"{total_seconds:.3f} seconds."
    )
    print(f"[EVA] Response: {complete_response}")

    await send_json(
        connection,
        {
            "type": "llm.completed",
            "sessionId": session_id,
            "model": OLLAMA_MODEL_NAME,
            "language": selected_language,
            "text": complete_response,
            "tokenChunkCount": token_index,
            "timeToFirstTokenSeconds": (
                round(first_token_seconds, 3)
                if first_token_seconds is not None
                else None
            ),
            "totalGenerationSeconds": round(
                total_seconds,
                3,
            ),
        },
    )

    await send_json(
        connection,
        {
            "type": "tts.stream.completed",
            "sessionId": session_id,
            "language": selected_language,
            "voice": voice_name,
            "audioChunkCount": tts_chunk_index,
            "timeToFirstAudioSeconds": (
                round(first_audio_seconds, 3)
                if first_audio_seconds is not None
                else None
            ),
            "totalSynthesisSeconds": round(
                total_synthesis_seconds,
                3,
            ),
            "totalAudioSeconds": round(
                total_audio_seconds,
                3,
            ),
        },
    )


async def run_response_pipeline(
    connection,
    session_id: str,
    user_text: str,
    conversation_history: list,
    voice_profile: str = "female",
    response_language: str | None = None,
) -> None:
    try:
        await stream_ollama_response(
            connection,
            session_id,
            user_text,
            conversation_history,
            voice_profile,
            response_language,
        )
    except asyncio.CancelledError:
        print(
            f"[EVA] Response generation cancelled: "
            f"{session_id}"
        )
        raise
    except ConnectionClosed:
        return
    except Exception as exception:
        print(
            f"[EVA] Response pipeline failed: "
            f"{exception}"
        )

        await send_json(
            connection,
            {
                "type": "error",
                "code": "response_pipeline_failed",
                "sessionId": session_id,
                "message": str(exception),
            },
        )


async def handle_connection(connection) -> None:
    remote_address = connection.remote_address
    print(f"[EVA] Unity connected: {remote_address}")

    audio_info = None
    audio_buffer = bytearray()
    response_task = None
    response_session_id = None
    live_transcription = None

    conversation_history = [
        {
            "role": "system",
            "content": SYSTEM_PROMPT,
        }
    ]

    await send_json(
        connection,
        {
            "type": "server.ready",
            "protocolVersion": PROTOCOL_VERSION,
            "liveTranscription": True,
            "timestamp": utc_timestamp(),
            "transcriptionDevice": WHISPER_DEVICE,
            "transcriptionComputeType": (
                WHISPER_COMPUTE_TYPE
            ),
            "transcriptionModel": WHISPER_MODEL_NAME,
            "llmModel": OLLAMA_MODEL_NAME,
            "ttsDevice": "cuda",
            "ttsEnglishVoice": PIPER_ENGLISH_VOICE_NAME,
            "ttsGermanVoice": PIPER_GERMAN_VOICE_NAME,
            "ttsVoiceProfiles": PIPER_VOICE_PROFILES,
        },
    )

    try:
        async for message in connection:
            if isinstance(message, bytes):
                if audio_info is None:
                    await send_json(
                        connection,
                        {
                            "type": "error",
                            "code": "unexpected_binary_data",
                            "message": (
                                "audio.start must be sent first."
                            ),
                        },
                    )
                    continue

                if len(message) % 2 or len(audio_buffer) + len(message) > audio_info["sampleRate"] * 2 * 120:
                    if live_transcription is not None:
                        await live_transcription.close()
                        live_transcription = None
                    session_id = audio_info["sessionId"]
                    audio_info = None
                    audio_buffer.clear()
                    await send_json(connection, {"type": "error", "code": "invalid_audio_size",
                        "sessionId": session_id, "message": "PCM16 must be sample-aligned and at most 120 seconds."})
                    continue
                audio_buffer.extend(message)
                if live_transcription is not None:
                    live_transcription.offer(audio_buffer)

                await send_json(
                    connection,
                    {
                        "type": "audio.chunk.received",
                        "byteCount": len(message),
                        "totalByteCount": len(audio_buffer),
                    },
                )
                continue

            print(f"[EVA] Received text: {message}")

            try:
                data = json.loads(message)
            except json.JSONDecodeError:
                await send_json(
                    connection,
                    {
                        "type": "error",
                        "code": "invalid_json",
                        "message": (
                            "The message is not valid JSON."
                        ),
                    },
                )
                continue

            message_type = data.get("type")

            if message_type == "client.hello":
                await send_json(
                    connection,
                    {
                        "type": "server.hello",
                        "protocolVersion": PROTOCOL_VERSION,
                        "message": (
                            "Unity is connected to the EVA backend."
                        ),
                        "timestamp": utc_timestamp(),
                    },
                )

            elif message_type == "ping":
                await send_json(
                    connection,
                    {
                        "type": "pong",
                        "timestamp": utc_timestamp(),
                    },
                )

            elif message_type == "voice.preview":
                request_id = data.get("requestId")
                if not isinstance(request_id, str) or not 1 <= len(request_id) <= 64:
                    await send_json(connection, {"type": "voice.preview.error", "requestId": "",
                        "message": "A preview requestId of 1 to 64 characters is required."})
                    continue
                if audio_info is not None or (response_task is not None and not response_task.done()):
                    await send_json(connection, {"type": "voice.preview.error", "requestId": request_id,
                        "message": "Finish the current recording or answer before previewing a voice."})
                    continue
                try:
                    language = normalize_response_language(data.get("responseLanguage"))
                    profile = normalize_voice_profile(data.get("voiceProfile"))
                    preview = await asyncio.to_thread(build_voice_preview, language, profile)
                    await send_json(connection, {"type": "voice.preview.audio", "requestId": request_id, **preview})
                except ConnectionClosed:
                    raise
                except Exception as exception:
                    await send_json(connection, {"type": "voice.preview.error", "requestId": request_id,
                        "message": str(exception)})

            elif message_type == "response.cancel":
                cancelled_session_id = (
                    response_session_id
                    or data.get("sessionId")
                    or "unknown"
                )
                generation_was_active = (
                    response_task is not None
                    and not response_task.done()
                )

                if generation_was_active:
                    response_task.cancel()

                    try:
                        await response_task
                    except asyncio.CancelledError:
                        pass

                response_task = None
                response_session_id = None

                await send_json(
                    connection,
                    {
                        "type": "response.cancelled",
                        "sessionId": cancelled_session_id,
                        "generationWasActive": (
                            generation_was_active
                        ),
                    },
                )

                print(
                    f"[EVA] Response interruption acknowledged: "
                    f"{cancelled_session_id}"
                )

            elif message_type == "audio.cancel":
                if audio_info is not None and data.get("sessionId") == audio_info["sessionId"]:
                    if live_transcription is not None:
                        await live_transcription.close()
                        live_transcription = None
                    audio_info = None
                    audio_buffer.clear()

            elif message_type == "audio.start":
                try:
                    voice_profile = normalize_voice_profile(data.get("voiceProfile"))
                except ValueError as exception:
                    await send_json(connection, {
                        "type": "error", "code": "invalid_voice_profile",
                        "message": str(exception),
                    })
                    continue
                try:
                    response_language = normalize_response_language(data.get("responseLanguage"))
                except ValueError as exception:
                    await send_json(connection, {
                        "type": "error", "code": "invalid_response_language",
                        "message": str(exception),
                    })
                    continue
                sample_rate = int(data.get("sampleRate", 0))
                channels = int(data.get("channels", 0))
                encoding = data.get("encoding")
                session_id = data.get(
                    "sessionId",
                    "unknown",
                )

                if sample_rate < 8000 or sample_rate > 192000:
                    await send_json(
                        connection,
                        {
                            "type": "error",
                            "code": "invalid_sample_rate",
                            "message": (
                                f"Invalid sample rate: "
                                f"{sample_rate}"
                            ),
                        },
                    )
                    continue

                if channels != 1:
                    await send_json(
                        connection,
                        {
                            "type": "error",
                            "code": "invalid_channel_count",
                            "message": (
                                "Only mono audio is currently "
                                "supported."
                            ),
                        },
                    )
                    continue

                if encoding != "pcm_s16le":
                    await send_json(
                        connection,
                        {
                            "type": "error",
                            "code": "invalid_audio_encoding",
                            "message": (
                                f"Unsupported encoding: "
                                f"{encoding}"
                            ),
                        },
                    )
                    continue

                if live_transcription is not None:
                    await live_transcription.close()
                    live_transcription = None
                if response_task is not None and not response_task.done():
                    response_task.cancel()
                    try:
                        await response_task
                    except asyncio.CancelledError:
                        pass
                audio_info = {
                    "sessionId": session_id,
                    "voiceProfile": voice_profile,
                    "responseLanguage": response_language,
                    "sampleRate": sample_rate,
                    "channels": channels,
                    "sampleWidth": 2,
                }

                audio_buffer.clear()
                if data.get("liveTranscription") is True:
                    live_transcription = LiveTranscription(session_id, sample_rate, response_language,
                        transcribe_pcm, lambda payload: send_json(connection, payload))

                print(
                    f"[EVA] Audio recording started: "
                    f"{sample_rate} Hz, mono, PCM16, voice profile: {voice_profile}."
                )

                await send_json(
                    connection,
                    {
                        "type": "audio.ready",
                        "sessionId": session_id,
                    },
                )

            elif message_type == "audio.end":
                if audio_info is None:
                    await send_json(
                        connection,
                        {
                            "type": "error",
                            "code": "no_active_recording",
                            "message": (
                                "No recording is active."
                            ),
                        },
                    )
                    continue

                if data.get("sessionId", audio_info["sessionId"]) != audio_info["sessionId"]:
                    await send_json(connection, {"type": "error", "code": "recording_session_mismatch",
                        "sessionId": data.get("sessionId"), "message": "audio.end does not match the active recording."})
                    continue
                if live_transcription is not None:
                    await live_transcription.close()
                    live_transcription = None

                if len(audio_buffer) == 0:
                    await send_json(
                        connection,
                        {
                            "type": "error",
                            "code": "empty_recording",
                            "message": (
                                "No audio data was received."
                            ),
                        },
                    )

                    audio_info = None
                    continue

                RECORDINGS_DIRECTORY.mkdir(
                    parents=True,
                    exist_ok=True,
                )

                file_timestamp = datetime.now().strftime(
                    "%Y%m%d_%H%M%S_%f"
                )

                file_name = (
                    f"recording_{file_timestamp}.wav"
                )
                file_path = (
                    RECORDINGS_DIRECTORY / file_name
                )
                session_id = audio_info["sessionId"]
                # Snapshot the profile with the recording, not with a later turn.
                voice_profile = audio_info["voiceProfile"]
                response_language = audio_info["responseLanguage"]

                with wave.open(str(file_path), "wb") as wav_file:
                    wav_file.setnchannels(
                        audio_info["channels"]
                    )
                    wav_file.setsampwidth(
                        audio_info["sampleWidth"]
                    )
                    wav_file.setframerate(
                        audio_info["sampleRate"]
                    )
                    wav_file.writeframes(audio_buffer)

                bytes_per_second = (
                    audio_info["sampleRate"]
                    * audio_info["channels"]
                    * audio_info["sampleWidth"]
                )

                duration_seconds = (
                    len(audio_buffer) / bytes_per_second
                )

                byte_count = len(audio_buffer)

                print(f"[EVA] WAV saved: {file_path}")
                print(
                    f"[EVA] Audio duration: "
                    f"{duration_seconds:.2f} seconds"
                )

                await send_json(
                    connection,
                    {
                        "type": "audio.saved",
                        "sessionId": session_id,
                        "fileName": file_name,
                        "byteCount": byte_count,
                        "durationSeconds": round(
                            duration_seconds,
                            3,
                        ),
                    },
                )

                audio_info = None
                audio_buffer.clear()

                print(
                    f"[EVA] Starting transcription: "
                    f"{file_name}"
                )

                await send_json(
                    connection,
                    {
                        "type": "transcription.started",
                        "sessionId": session_id,
                        "fileName": file_name,
                    },
                )

                try:
                    result = await asyncio.to_thread(
                        transcribe_audio,
                        file_path,
                        response_language,
                    )
                except Exception as exception:
                    print(
                        f"[EVA] Transcription failed: "
                        f"{exception}"
                    )

                    await send_json(
                        connection,
                        {
                            "type": "error",
                            "code": "transcription_failed",
                            "sessionId": session_id,
                            "message": str(exception),
                        },
                    )
                    continue

                print(
                    f"[EVA] Transcription completed in "
                    f"{result['processingSeconds']:.3f} "
                    f"seconds."
                )
                print(
                    f"[EVA] Selected language: "
                    f"{result['language']}"
                )
                print(
                    f"[EVA] Transcript: {result['text']}"
                )

                await send_json(
                    connection,
                    {
                        "type": "transcription.result",
                        "sessionId": session_id,
                        "fileName": file_name,
                        **result,
                    },
                )

                if not result["text"]:
                    print(
                        "[EVA] No speech was detected. "
                        "Skipping Ollama and Piper."
                    )

                    await send_json(
                        connection,
                        {
                            "type": "transcription.empty",
                            "sessionId": session_id,
                            "fileName": file_name,
                        },
                    )
                    continue

                if (
                    response_task is not None
                    and not response_task.done()
                ):
                    response_task.cancel()

                    try:
                        await response_task
                    except asyncio.CancelledError:
                        pass

                response_session_id = session_id
                response_task = asyncio.create_task(
                    run_response_pipeline(
                        connection,
                        session_id,
                        result["text"],
                        conversation_history,
                        voice_profile,
                        response_language,
                    )
                )

            else:
                await send_json(
                    connection,
                    {
                        "type": "error",
                        "code": "unknown_message_type",
                        "message": (
                            f"Unknown message type: "
                            f"{message_type}"
                        ),
                    },
                )

    except ConnectionClosed as exception:
        print(
            f"[EVA] Connection closed: "
            f"{exception.code} {exception.reason}"
        )

    finally:
        if live_transcription is not None:
            await live_transcription.close()
        if (
            response_task is not None
            and not response_task.done()
        ):
            response_task.cancel()

            try:
                await response_task
            except asyncio.CancelledError:
                pass

        print(
            f"[EVA] Unity disconnected: "
            f"{remote_address}"
        )


def configure_backend_logging() -> logging.Logger:
    # Keep server tracebacks available even when Unity hides process output.
    logger = logging.getLogger("eva.backend")
    logger.setLevel(logging.INFO)
    handler = RotatingFileHandler(
        BACKEND_DIRECTORY / "backend-runtime.log", maxBytes=1024 * 1024,
        backupCount=1, encoding="utf-8",
    )
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logger.addHandler(handler)
    return logger


async def main() -> None:
    backend_logger = configure_backend_logging()
    print("[EVA] Initializing CUDA transcription...")
    load_whisper_model()

    print("[EVA] Initializing Piper CUDA voices...")
    load_piper_voices()

    print("[EVA] Initializing Ollama...")
    await initialize_ollama()

    print(f"[EVA] Starting backend on ws://{HOST}:{PORT}")

    async with serve(
        handle_connection,
        HOST,
        PORT,
        compression=None,
        max_size=16 * 1024 * 1024,
        ping_interval=20,
        ping_timeout=20,
        logger=backend_logger,
    ) as server:
        print(
            "[EVA] Backend is ready. "
            "Press Ctrl+C to stop it."
        )
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n[EVA] Backend stopped.")
