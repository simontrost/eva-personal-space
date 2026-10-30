# EVA backend setup

Updated 29 October 2026. Supported profile: **Windows x64, Python 3.11 x64,
NVIDIA CUDA/FP16**. Read the root README and inspect the target hardware first.
There is no CPU, AMD or Intel fallback in this version.

## First setup or transfer to another PC

Do not reuse a copied `.venv`. Install local Python 3.11 x64, a suitable NVIDIA
driver, Git, the Microsoft Visual C++ x64 Runtime and Unity 6000.3.12f1.
In PowerShell, inside `Backend`:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\hardware_report.ps1
```

Read `hardware-report.json` before installation. Check GPU/VRAM, driver, CPU,
RAM, Windows and free space; do not rely on a report copied from the old PC.
For a supported machine:

```powershell
.\setup_backend.ps1 -RecreateEnvironment -InstallOllama
.\.venv\Scripts\python.exe verify_backend.py
```

Setup recreates only `.venv`, keeping `models` and `voices`. It installs pinned
Python/CUDA packages, prevents CPU ONNX Runtime from replacing the GPU runtime,
checks/downloads Whisper Small and all four voices, installs/checks Ollama and
`llama3.2:3b`, then runs preflight. Internet is needed for missing downloads.
If winget is unavailable, install Ollama manually and rerun setup.

A separate CUDA developer toolkit, PyTorch installation or FFmpeg executable
is normally unnecessary. Python packages supply CUDA/cuDNN runtime libraries;
the NVIDIA driver must support them. To repair an existing environment without
recreating it, run `setup_backend.ps1` without the recreate option.

Success ends with:

```text
[EVA Check] SUCCESS: The local GPU backend is ready.
```

Preflight checks dependencies, CUDA/FP16 support and model files. Actual model
warm-up and WebSocket readiness are checked when the server starts. Use only
`.venv\Scripts\python.exe`, not a copied Conda interpreter or another Python.

### Copied environment: exit 103 / No Python at

A virtual environment stores the path of the Python that created it. A copied
environment can point to another Windows user or a missing installation. Unity
may show a startup timeout while the underlying interpreter exits with code 103.
Stop Play, install Python 3.11 x64 locally, and repeat the recreate command above.
Keep the model/voice folders. Editing `pyvenv.cfg` alone is not the repair.

## Run the application

Normally, open `Assets/_Project/Scenes/Main.unity` and press Play. The Unity
launcher starts or reuses Ollama and the backend, waits for model warm-up and
`server.ready`, then opens the selection menu. Do not start a second GPU server.
Loading and menu selection block recording until Start/Continue conversation.

For manual backend debugging only, outside Play:

```powershell
.\.venv\Scripts\python.exe server.py
```

Ollama must already be serving locally on `127.0.0.1:11434`; `ollama serve` can
start it if no instance is running. The backend listens on `127.0.0.1:8765`
after initialization. `server.py` is the sole backend entry point. Old server
copies and early single-component experiments were removed.

## Language, voices and sample rates

| Menu language | Female | Male |
| --- | --- | --- |
| English | Kristin: `en_US-kristin-medium`, 22,050 Hz | Mike: `en_US-mike-medium`, 22,050 Hz |
| Deutsch | Ramona: `de_DE-ramona-low`, 16,000 Hz | Thorsten: `de_DE-thorsten-high`, 22,050 Hz |

`voice_config.py` defines all four files for downloads, runtime, preflight and
tests. Hz means audio samples per second. Unity uses the rate in each stream's
metadata to play at the correct speed and pitch.

**03 LANGUAGE is explicit.** Whisper receives that language, the LLM receives
a matching per-turn instruction, and Piper uses that language's selected voice.
No automatic language detection or switching occurs. **04 VOICE is independent
of character design and animation style.** Click the name to select it.
The profile and language are fixed when recording begins, for the full answer.

`audio.start` accepts `voiceProfile` (`female`/`male`) and `responseLanguage`
(`en`/`de`). For older clients, omitted voice defaults to female and omitted,
null or empty language defaults to English. Invalid values are rejected before
recognition. Missing language never enables automatic detection. Conversation
protocol version and binary PCM framing are unchanged.

`piper_compat.py` filters phonemes missing from a voice's vocabulary before
model-ID conversion and alignment. This lets Ramona's vocabulary work with
current eSpeak and lip-sync without changing the voice files or installed Piper.
`cuda_runtime.py` shares DLL setup between the server and preflight.

## Live transcription and word display

The ready message advertises `liveTranscription: true`. Unity then sends
`audio.start` when recording begins, with that option enabled, followed by short
contiguous PCM16 chunks. `audio.end` finishes the recording on button release.
No samples are resent. Without the advertised capability, Unity keeps the
previous full-recording upload behavior.

The server returns `transcription.partial` with the recording session ID,
selected language, increasing revision and a complete provisional text snapshot.
One recognition job runs at a time, with at most one newer pending snapshot.
Updates are requested about every 0.65 seconds; speed depends on audio and GPU.
Whisper can correct recent words. Partials do not start the LLM or change history.
After release, full-quality recognition supplies `transcription.result` and only
then starts the normal answer/speech pipeline. `audio.cancel` discards the matching
recording; stopped workers cannot publish late text into another turn.

Both text boxes reveal complete words rather than LLM token fragments. The last
word is flushed on completion. They share the selection menu's theme. Visibility,
moving/resizing, loading gates and VR behavior remain available. No package or
model change is needed.

## Menu voice previews

Click the small play icon in a voice card to preview without selecting it.
The icon becomes a loading ring, then a stop square. Clicking again cancels or
stops it. Changing language/voice, starting conversation or leaving the menu
also stops previews. Cancelled or late results are ignored.

`voice.preview` carries `requestId`, `responseLanguage` and `voiceProfile`.
The backend synthesizes a fixed short sentence using Piper, caches it per
language/profile and returns `voice.preview.audio` with PCM16 mono metadata and
`audioBase64`. Errors use `voice.preview.error`. Preview messages do not change
conversation binary framing. They do not call Whisper/Ollama, add history or
trigger speaking/lip-sync. Unity uses a separate menu AudioSource. Raw PCM and
base64 preview data are not printed to the Unity Console.

## Tests: choose the correct type

**No model loading or microphone:**

```powershell
.\.venv\Scripts\python.exe -m unittest -v test_response_language test_voice_preview test_streaming_transcription
```

**Outside Unity Play:** load real CUDA voices, check synthesis/alignment and
generate English/German WAV fixtures for both profiles:

```powershell
.\.venv\Scripts\python.exe test_voice_profiles.py
```

The female subset is also available as `test_female_voices.py`. Generated WAVs
are local test files. Do not run these model-loading checks alongside Play.

**Backend already ready:** test separate WebSocket connections:

```powershell
.\.venv\Scripts\python.exe test_live_voice_preview.py
.\.venv\Scripts\python.exe test_live_voice_profiles.py
```

The conversation test needs the generated female English/German WAVs. It checks
selected language/voice, PCM metadata, complete byte counts and viseme timing.
The preview test checks all four samples and cache reuse. Neither tests the
Windows microphone or Unity audio output, and neither proves transcription
accuracy. Test both languages/voices with the real microphone in Unity too.

## Startup and connection failures

Read the first Unity Console error and `Backend/backend-runtime.log`. The log
rotates at about 1 MiB with one previous file. Startup errors also include recent
captured process errors. A listening TCP port alone does not prove readiness;
Unity waits for the server's readiness message.

- CUDA provider/DLL error: use the project Python, check driver and Visual C++
  runtime, then rerun setup. Do not mix `onnxruntime` and `onnxruntime-gpu`.
- Immediate close / WebSocket 1011: read the server traceback. An old backend
  may remain after an interrupted script reload. Stop only the confirmed EVA
  process tree occupying port 8765, then restart Play. Keep unrelated Python apps.
- Stop Play before script edits. Unity stops backend/Ollama process trees it
  started, including virtual-environment Python children. Independent services
  remain running. Restart Play after compilation.
- Wrong language/voice: change the menu language and click the voice name;
  a preview click only plays a sample. Speak in the selected language.
- Missing final speech: long replies can overflow the scene's 60-second queued
  audio buffer. Shorter replies help; stream backpressure is not implemented.

## Validation scope and packaging

On 28 October, after code cleanup, nine unit tests, GPU preflight, full model
warm-up and all four live preview/conversation cases passed. Synthetic German
fixtures also showed recognition errors. Real Unity microphone/playback, headset,
offline and clean-player acceptance remain manual. See
`Documentation_EN/reference/pc-backend-validation-2026-10-28.md` for evidence.

For a Windows player, keep the complete prepared backend beside the executable:

```text
EVA.exe
EVA_Data/
Backend/
  server.py
  cuda_runtime.py
  voice_config.py
  piper_compat.py
  requirements.txt
  ...other backend sources and setup scripts...
  .venv/
  models/
  voices/
```

Create `.venv` on the target PC. Unity does not copy/install the backend by itself.
Do not install this NVIDIA profile unchanged on unsupported graphics hardware;
a separate implementation and validation would be required.
