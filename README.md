# EVA: local voice and avatar project

**Read this file and `Backend/BACKEND_SETUP.md` completely before
installing packages or changing code. Check the target hardware first.**

EVA is a character you can talk to in Unity. Unity records push-to-talk audio
and sends it to a local Python backend over WebSocket. Faster-Whisper recognizes
speech, Ollama generates a reply, and Piper produces speech. Unity plays the
streamed audio with synchronized mouth movements. The prepared models run locally;
initial installation and downloads need internet access.

## Current application

- Automatic startup starts or reuses Ollama and the Python backend. A loading
  screen blocks interaction until the backend and WebSocket handshake are ready.
- The selection menu opens before conversation. One EVA is visible at runtime.
- **01 CHARACTER:** six Rocketbox designs.
- **02 BEHAVIOUR:** Neutral, Friendly or Expressive body animations. These are
  animation styles; they do not change the LLM's personality.
- **03 LANGUAGE:** English or Deutsch. This fixes speech recognition, answer
  text and speech to the selected language. There is no automatic detection or
  switching. Speak in the selected language and reopen settings to change it.
- **04 VOICE:** Kristin/female or Mike/male for English; Ramona/female or
  Thorsten/male for German. Voice is independent of character design.
  Click a name to select it. The small play icon in each card only previews
  that voice; it becomes a loading ring and then a stop control.
- Confirmed settings are saved locally, with a separate voice choice for each
  language. A recording keeps its starting language and voice for the full reply.
- Streamed playback, 15-viseme lip-sync, blinking and head/body tracking remain
  active. Conversation text boxes use the menu design and can be shown with T,
  moved and resized. Speech text appears while you hold B, and answers appear
  word by word during generation. Recognition has a short delay and may revise
  recent words; release B to finalize the request.
- Desktop controls and Meta Quest PC VR are supported. A standalone Quest APK
  is not provided.

## Get the complete project

The repository includes Unity assets and backend source. Avatar
models and textures use **Git LFS**. Install Git for Windows with Git LFS:

```powershell
git lfs install
git clone https://www.socialvrlab.com/gitea/oxidiert/EVA.git
cd EVA
git lfs pull
```

If you already copied the complete folder, do not clone a second copy. Keep
`Assets`, `Packages`, `ProjectSettings`, the complete `Backend` source folder,
`README.md`. Documentation is in `Documentation_EN`.

Do not reuse a copied `Backend/.venv`; create it on the target PC. With Unity
closed, let the target PC recreate `Library`. `.vs`, `Logs`, `Temp` and `obj`
are generated files. `Backend/models` and `Backend/voices` may be copied;
setup checks existing downloads. Missing models and all four voices are downloaded.
Ollama stores its model in the Windows user's own profile.

## Working in your own repository

Clone or fork the project to work on it. Publish changes to a repository in your
own account; the shared source gives other users read access. See
[the fork and independent-repository instructions](SHARING.md) for setup,
Git LFS uploads and receiving updates from the original project.

## Hardware check before setup or migration

The current implementation requires **Windows x64, Python 3.11 x64 and an NVIDIA
CUDA GPU with FP16 support**. CPU fallback is deliberately disabled. AMD and
Intel graphics are not supported by this configuration.

Previous machines: desktop RTX 5070 Ti (16 GB VRAM), Ryzen 7 9700X, 32 GB RAM;
laptop RTX 5070 Laptop GPU (about 8 GB VRAM). A suggested starting point is a
recent 6-core CPU, 16 GB RAM (32 GB recommended), an NVIDIA RTX GPU with 8 GB
VRAM (12–16 GB recommended), and an SSD. Allow at least 12 GB free for backend
setup, plus Unity, project assets and import caches. These are estimates, not
measured minimum requirements. VR needs additional rendering capacity.

## Set up a supported NVIDIA PC

Install a suitable NVIDIA driver, Python **3.11 x64**, Git/Git LFS, Unity Hub,
Unity **6000.3.12f1**, and the Microsoft Visual C++ x64 Runtime. The setup script
can install Ollama using winget; otherwise install Ollama manually first.
Newer Unity versions may work, but have not been tested with this project.

From `Backend`:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\setup_backend.ps1 -RecreateEnvironment -InstallOllama
.\.venv\Scripts\python.exe verify_backend.py
```

The recreate option replaces only `.venv`, keeping model and voice folders.
Setup installs the pinned NVIDIA packages, checks/downloads Whisper Small,
four Piper voices and `llama3.2:3b`, then runs preflight. A separate CUDA
developer toolkit, PyTorch installation or FFmpeg executable is normally
unnecessary. The NVIDIA driver must support the packaged CUDA libraries.

Do not install CPU `onnxruntime` over `onnxruntime-gpu`. Use the environment's
`Scripts\python.exe` for all checks; the old copied Conda layout is not used.

### Different or unsupported hardware

For another NVIDIA card, inspect VRAM, CUDA/FP16 support and preflight results
before changing packages or model sizes. Unity and all AI models share VRAM.
The current server requires Whisper CUDA/FP16, Piper's CUDA provider and at
least 99% Ollama GPU allocation. Allocation is not a utilization benchmark.

**The four cases below are porting notes, not ready-to-use setup options.**
There is currently no working AMD, macOS, Linux or CPU-only profile in this
repository. The NVIDIA installation script deliberately rejects systems without
an NVIDIA GPU. The backend and automatic launcher also contain Windows-only code.
An AMD Ryzen CPU with a suitable NVIDIA GPU on Windows is not an AMD GPU case.

#### 1. Windows with an AMD graphics card

Whisper and Piper currently require NVIDIA/CUDA. Choose CPU execution or
separately supported AMD backends, then adapt their settings and GPU checks in
`Backend/server.py`. Change the dependency profile, skip NVIDIA DLL loading, and
update installation and preflight checks. Changing the graphics driver alone
does not provide this support. Ollama can use some AMD GPUs; check the exact
model and operating system against its
[hardware support list](https://docs.ollama.com/gpu).

#### 2. macOS

The Windows startup restriction, executable paths and process handling must be
adapted in `Assets/_Project/Scripts/EVASystemLauncher.cs`. Replace Windows DLL
loading and the Windows installation steps. Choose suitable Whisper/Piper
execution backends and adjust the checks. Apple Silicon needs ARM64 packages;
Intel Macs need packages for their own architecture. Ollama supports Apple GPU
acceleration through Metal, but this does not make the speech pipeline portable.

#### 3. Linux with an NVIDIA graphics card

CUDA execution can generally remain if the GPU, driver and library versions
are compatible. The main changes are Linux-compatible Python packages, shared
library loading instead of Windows DLL setup, and Linux Python/Ollama paths
and process handling in the launcher. Adapt or replace `setup_backend.ps1` and
update preflight for the Linux environment. The current Windows-only guards
must be changed before the backend and launcher can run.

#### 4. Linux with an AMD graphics card

Combine the Linux installation/startup changes with the AMD changes to Whisper,
Piper and GPU validation in `Backend/server.py`. AMD acceleration depends on the
exact GPU, driver, distribution and supported backend builds. CPU execution is
another possible profile, but is currently disabled and needs implementation
and performance testing.

#### Files to focus on when porting

| File | What needs attention |
| --- | --- |
| [Backend/requirements.txt](Backend/requirements.txt) | Define packages for the target OS, CPU architecture and execution backend. The existing pins describe the Windows/NVIDIA profile. |
| [Backend/cuda_runtime.py](Backend/cuda_runtime.py) | Runs during server import and preflight. It rejects non-Windows systems and loads NVIDIA DLLs; make this conditional or replace it for another profile. |
| [Backend/server.py](Backend/server.py) | Whisper device/compute type, Piper provider and loading, and strict GPU-only checks. Model names and Ollama context length are also defined here. |
| [Backend/setup_backend.ps1](Backend/setup_backend.ps1) | NVIDIA checks, environment creation, package installation, Windows program discovery and model setup. Adapt or replace this flow for another platform. |
| [Backend/verify_backend.py](Backend/verify_backend.py) | Check the chosen runtime/provider and hardware instead of always requiring CUDA/FP16. Keep model and package checks consistent with setup. |
| [Assets/_Project/Scripts/EVASystemLauncher.cs](Assets/_Project/Scripts/EVASystemLauncher.cs) | For macOS/Linux: Windows guard, Python/Ollama executable discovery, process startup and shutdown. Startup timeouts are configured on the Unity component. |
| [Backend/download_backend_models.py](Backend/download_backend_models.py) | Keep downloaded models consistent with runtime and preflight if changing model sizes. Voice file definitions are in [Backend/voice_config.py](Backend/voice_config.py). |

#### Other things to check on every computer

- **Versions and environment:** start with Unity **6000.3.12f1** and Python
  **3.11**. Other versions can change package or import behaviour. Recreate
  `.venv` on each target computer and after moving its location; do not copy it
  between machines or platforms. See the
  [Python environment documentation](https://docs.python.org/3.11/library/venv.html).
- **Drivers and runtime libraries:** use matching driver, CUDA/cuDNN and ONNX
  Runtime versions for the NVIDIA profile. Other profiles need their own
  compatible stack. Do not install conflicting CPU/GPU ONNX Runtime packages
  together in one environment.
- **RAM, VRAM and speed:** Unity, speech models and Ollama run together. A
  supported GPU may still have too little free VRAM. Smaller models or a shorter
  context may help, but keep runtime, downloads, setup and verification in sync.
  CPU-based profiles may respond more slowly and need measured testing.
- **Complete downloads:** Git LFS must retrieve the actual avatar and texture
  files. `Backend/models` and `Backend/voices` are not included in Git; setup
  downloads them separately, and Ollama downloads its own model. Initial setup
  needs internet access. Check offline operation only after all downloads finish.
- **Startup and connections:** slower computers can take longer to warm up.
  Check startup timeouts on `EVASystemLauncher` and read the first backend error
  before increasing them. A leftover EVA backend or another application on
  ports **8765** or **11434** can prevent startup. Stop only confirmed EVA
  processes, not unrelated Python or Ollama applications.
- **Microphone and playback:** check OS microphone permission and the selected
  input/output devices, including headset devices. Test a real recording and
  complete playback; text output alone does not prove audio works. Long replies
  can exceed the scene's 60-second queued-audio limit.
- **VR:** validate desktop operation first. The current workflow is Meta Quest
  PC VR on Windows. Headset/runtime support, OpenXR, controllers, panel placement
  and audio need separate checks on each platform. Desktop operation does not
  prove VR support, and this is not a standalone Quest application.

#### Working together and validating a port

A shared project revision, OS and software versions make setup and debugging
easier. Matching or similar graphics hardware, ideally the same GPU model when
practical, also makes results easier to compare. Different hardware is possible,
but porting takes extra coordination and testing. Work with people who are
willing to test their own hardware and reproduce problems together.

Record the project revision, OS, CPU, GPU, RAM/VRAM, driver and software versions
for each test machine. Require successful preflight, clean Unity compilation,
startup/loading/menu operation, real speech input, both selected languages and
all four voices, complete playback, interruption and lip-sync. Repeat start/stop
and check offline use. Test VR separately. These ports are not validated by
changing requirements or passing package-import checks alone.

## Start Unity

1. Add the project folder in Unity Hub and open it with **6000.3.12f1**.
   Wait for package restoration and asset import. Fix compiler errors first.
2. Open `Assets/_Project/Scenes/Main.unity`. Main is also the configured build
   scene; the old Unity example scene and welcome tutorial were removed.
3. Choose `EVA > Controls > Desktop (WASD + Mouse)` before Play for desktop mode.
4. Press Play. Do not start a second backend. Loading stays visible until the
   models are warm and Unity receives `server.ready` over WebSocket.
5. Choose character, behaviour, language and voice, then **Start conversation**.
   Desktop text boxes start hidden. In VR, text appears on a panel near EVA.
   Hold B to speak on desktop and release to finish the question.

## Controls

| Control | Desktop action |
| --- | --- |
| WASD / mouse | Move at the current camera height / look around |
| Shift | Move faster |
| Esc | Release the mouse to use buttons or text-box handles |
| B, hold/release | Record/send; a new recording interrupts the old answer |
| M | Open settings when idle; continue when the menu is open |
| T | Show/hide question and answer text; drag/resize with the cursor released |

Click empty Game-view space after starting conversation to capture the mouse.
Movement and recording stay blocked during loading and menu selection.
Desktop walking has fixed height and no collision-based navigation.
Control mode is stored in `Assets/_Project/Resources/EVAControlSettings.asset`;
change it outside Play for the next start.

### Meta Quest 3 through Link / Air Link

Unity and Python run on the Windows PC. Install the Meta Quest Link PC app,
set it as the active OpenXR runtime and start Link/Air Link in the headset first.
Choose `EVA > Controls > VR (Meta Quest)` before Play.

In the menu, navigate with the right stick and confirm with A. Release A after
Start before holding it again to record. During conversation, left stick moves,
right stick snap-turns by 30 degrees, and right A records/sends. Right B opens
settings while idle or continues from the menu. Head tracking continues in menus.

| Quest controller | Action |
| --- | --- |
| Right stick / right A in the menu | Navigate / confirm the selected control |
| Right B | Open settings while idle; Start/Continue closes the menu |
| Right A, hold/release during conversation | Record / finish the question and send |
| Left stick / right stick during conversation | Walk / snap-turn by 30 degrees |
| Left X | Show/hide conversation text |
| Left Y | Reposition the menu, text and loading surfaces |

The menu, conversation text and loading screen use World Space surfaces in VR.
The menu sits beside EVA and the text sits below the face, usually about 2-3 m
away. They stay in the scene when you turn your head. Left X shows/hides text;
left Y places the surfaces again from your current position. Opening the menu
also places it again. VR text visibility is saved separately from desktop layout.

To adjust placement, select `Assets/_Project/Resources/EVAControlSettings.asset`
in the Project window. Under **VR panels (metres)**, change distance, menu/text
width, menu side offset or text vertical offset. Press left Y to apply changes
during Play. Save settings outside Play to keep them for the next start.

If Meta Link closes Play immediately, save and fully close Unity, start Link
first and reopen Main. Check OpenXR and headset audio settings. On a laptop,
use the high-performance NVIDIA GPU for Unity and Meta Link. Do not bypass Meta
compatibility checks by modifying vendor files.

Whisper uses `small`, CUDA, float16, GPU 0. Ollama uses `llama3.2:3b`, a
4096-token context and limited conversation history. Piper uses four CUDA voices.
The UI fixes response language and voice for each recording. Animation style
does not change voice, language or the language-model prompt.

## Troubleshooting

- **Exit 103 / No Python at:** recreate the copied environment on this PC.
- **CUDA/DLL error:** use project Python, rerun setup and check driver/runtime.
- **Startup/WebSocket error:** read the first Console error and
  `Backend/backend-runtime.log`. The port alone does not prove readiness.
- **Old backend on port 8765:** stop only the confirmed EVA process tree.
  Stop Play before script edits. Unity stops only processes it owns; independent
  Ollama/backend services remain running. Logs rotate at about 1 MiB with one backup.
- **Wrong language or voice:** select 03 LANGUAGE and click a voice name in
  04 VOICE. The small play icon does not select it.
- **Missing speech:** the scene's audio queue holds 60 seconds; long replies
  can overflow it. Automatic reconnection after a later disconnect is not implemented.

## Documentation and distribution

- [Full project documentation](Documentation_EN/EVA_Project_Documentation_EN.pdf)
- [Two-page cheat sheet](Documentation_EN/EVA_Cheat_Sheet_EN.pdf)
- [Step-by-step setup guide](Documentation_EN/EVA_Setup_Guide_EN.pdf)
- [Editable LaTeX sources and build instructions](Documentation_EN/README.md)

For a Windows player, keep the complete prepared `Backend` folder beside the
executable; create its `.venv` on that target PC. Unity does not package the
Python backend automatically. A clean player installation still needs validation.
Review Piper, individual voice/model, Rocketbox, Unity and other dependency
licenses before distribution. Local operation does not make every component
license-free or guarantee accuracy.
