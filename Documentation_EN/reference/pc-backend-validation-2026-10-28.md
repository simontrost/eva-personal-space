# Desktop repair and post-cleanup validation: 28 October 2026

This record separates checked results from manual acceptance still needed.
It covers the copied-environment repair and the current menu/startup/cleanup
revision. It is not a claim that the complete application was tested on every PC.

## Machine and copied-environment repair

- Windows 11 Home x64, build 26200.
- AMD Ryzen 7 9700X, 8 cores / 16 threads, 32 GB RAM.
- NVIDIA GeForce RTX 5070 Ti, 16 GB graphics memory; driver 617.14.
- Python 3.11.9 x64; CTranslate2 4.8.2; ONNX Runtime GPU 1.26.0.
- Unity target: 6000.3.12f1.

The copied `.venv` referenced the laptop's previous Python path. On the desktop,
that path did not exist: Python printed `No Python at ...` and exited with 103,
while Unity reported a startup timeout. After checking hardware, local Python
3.11 x64 and a fresh environment were installed with the pinned NVIDIA profile.
Existing model/voice files were retained. Editing `pyvenv.cfg` was not used as
the repair. One earlier startup took 97.7 seconds; this is a single observation,
not a startup-time guarantee.

## Source changes covered by the later checks

- Fixed menu language is sent to Whisper, the LLM instruction and Piper.
- Voices are selected independently of design; each card has an inline
  play/loading/stop preview control. Previewing does not select the voice.
- Startup waits for `server.ready`; disable/reload cleanup stops owned process
  trees, including Windows virtual-environment Python children. Independent
  services are retained. Server tracebacks are logged with bounded rotation.
- The preview graphic requires a CanvasRenderer and creation adds it explicitly.
- Shared CUDA setup is in `cuda_runtime.py`; `server.py` remains the sole entry
  point. Old server copies, early smoke tests and Unity welcome/example assets
  were removed. Main is the configured build scene. Active validation tools remain.
- Raw JSON/token/preview-audio Console logging was removed. The standard
  `.venv/Scripts/python.exe` path replaces the old environment-layout fallback.

## Checks after cleanup

| Check | Result and scope |
| --- | --- |
| Runtime and Editor C# compilation | Passed using installed Unity/package references; not a complete Player build |
| `test_response_language`, `test_voice_preview` | Nine model-free unit tests passed |
| Recording profile metadata | Valid/default/uppercase and invalid-value cases passed |
| `verify_backend.py` | Passed; pinned packages, CUDA provider, FP16 and model files checked |
| Real model startup | Whisper, all four Piper voices and Ollama warmed up; WebSocket readiness, hello and ping/pong passed |
| `test_live_voice_preview.py` | Four non-silent PCM samples and repeat-cache checks passed |
| `test_live_voice_profiles.py` | Four language/profile cases passed for voice selection, metadata, complete PCM and viseme timing |

The live conversation cases were English/Mike, German/Thorsten-high,
English/Kristin and German/Ramona. Inputs were generated speech WAVs, not a
Windows microphone. Chunk counts vary with replies and are not acceptance targets.

**Recognition limitation:** some generated German speech was transcribed
incorrectly. The test checked routing, metadata and complete streams, not semantic
accuracy. Passing it does not prove perfect language recognition or answer quality.

An isolated Unity preview-icon check covered both creation paths and play,
loading and stop graphics, including renderer access, generated geometry,
raycast and callback behavior. This verified the renderer fix; it did not exercise
the whole speech application.

After the fresh-backend tests, only their owned process tree was stopped and
port 8765 was free. The independently running Ollama service was retained.
Detailed test logs and generated audio remain local and are excluded from Git.

## Still required on the actual target setup

- Real Unity Play: loading/menu gating, inline preview audio/cancellation,
  character/style selection and saved preferences.
- Actual microphone input in both selected languages and both voices, complete
  speaker playback, lip-sync, interruption and movable/resizable text boxes.
- Several start/stop/script-reload cycles without orphan processes.
- Headset UI, controller input, microphone and output in real PC VR.
- A packaged Windows Player on a clean PC, plus a separate offline test.

Do not treat compilation, synthetic clients or a preflight pass as substitutes
for these acceptance checks. No new full Unity microphone/headset session or
clean-PC build was completed during the cleanup/documentation pass.
