# EVA character selection interface

Updated 29 October 2026. Use the existing Main scene and current scripts.

## What happens when you press Play

1. The existing loading screen stays visible until Ollama, the GPU backend and
   the WebSocket connection are ready.
2. The character menu opens. Conversation text boxes are hidden. Recording,
   desktop movement and VR stick movement are blocked while the menu is open.
3. Choose one of the six character designs. The scene shows one active EVA and
   updates its appearance immediately. Any retained extra hosts are disabled
   during this Play session. Only one correctly configured host is needed.
4. Choose Neutral, Friendly or Expressive. This changes the existing body
   animation style. It does not change the LLM prompt or train a new personality.
5. Under 03 LANGUAGE, choose English or Deutsch. This sets EVA's response
   language and the matching Piper voice. Whisper also uses this fixed language
   for input recognition. Speak in the selected language; EVA never selects
   another language automatically. Reopen the menu to change languages.
6. Under 04 VOICE, choose the female or male voice independently of the design.
   English offers Kristin and Mike; Deutsch offers Ramona and Thorsten.
   Click a voice name to select it. The small play icon inside each card previews
   that voice without changing the selection. It becomes a spinner while loading,
   then a stop square; click it again to cancel/stop the sample.
7. Select Start conversation. Use the existing push-to-talk controls. Text boxes
   start hidden on desktop; press T to show them. In VR, they start visible near
   EVA; press left X to show/hide them. Their mouse move/resize controls remain.
   They share the menu colors and show complete words progressively: your text
   during recording, EVA's text during generation. Recognition has a short delay
   and may revise recent words; release the recording button to finalize.

The menu's voice selection overrides the appearance's male/female default. Streaming
audio, visemes, blinking and gaze use the existing components. Confirmed
character/style/language choices and a separate voice choice per language are
saved locally. Language and voice are frozen when recording starts and apply
to the full answer. A new installation starts with the appearance's voice default.

Voice previews use a separate AudioSource. They do not record the microphone,
call Whisper/Ollama, change conversation text/history, or start an avatar speaking
animation. Changing language/voice, starting conversation or closing the menu
stops the preview. A cancelled or late response never starts playback afterwards.

## Set up in Unity

The menu creates itself in the existing Main scene at runtime. You do not need
to build a Canvas or wire up its buttons. The saved scene includes its controller.

1. Stop Play. Wait until Unity finishes importing and compiling the scripts.
2. Open Assets/_Project/Scenes/Main.unity.
3. Normally just press Play: the menu uses its saved Primary Avatar reference.
   If none is assigned, it can choose a suitable host using the Main Camera view.
4. To choose that host explicitly, select its EVA root in the Hierarchy and use
   EVA > Interface > Configure Selection Menu in Scene. Save with Ctrl+S.
   The new controller's Primary Avatar field keeps that choice. The design and
   behaviour are still selected through the runtime menu.
5. In the menu controller's Inspector, Remember Selection controls whether
   confirmed choices are restored next time. When disabled, Initial Character
   and Initial Behaviour supply the starting selection, together with Initial
   Language (English by default).

## Use the menu again

- Desktop: press M, or release the mouse with Esc and click Character settings.
- Desktop mouse or keyboard: click a button, or use arrow keys and Enter.
- VR: use the right stick to move between controls and A to confirm. Right B
  opens the menu again; it also continues from the open menu. Head tracking stays
  active while stick movement is blocked. Keyboard M remains available.
- Finish the current recording/answer before reopening character settings.
- Release the controller A button after confirming Start, then press it again
  to record. The confirmation press does not start a microphone recording.

In VR, the menu is a World Space surface beside EVA. Conversation text is on a
separate surface below the face. Loading also uses a surface in front of you.
They are usually about 2-3 m away and do not follow head rotation. Left Y places
them again from your current position; reopening the menu also repositions it.
Left X toggles conversation text after loading and menu selection are finished.
The desktop layout and its saved position/size are independent of the VR layout.

Select Assets/_Project/Resources/EVAControlSettings.asset to adjust **VR panels
(metres)** in the Inspector: distance, menu/text width, menu side offset and text
vertical offset. Press left Y after changing values during Play. Edit and save
the asset outside Play to keep values for future sessions. The default setting
shows VR conversation text; a saved VR visibility choice takes priority.

## Test before using it in a demo

1. Start Play and confirm that loading comes before character selection.
2. Confirm that only one EVA is visible and T/B cannot bypass the open menu.
3. Try all six designs, all three animation styles, both languages and both voices.
   Test both inline play/stop icons, changing language during a preview and
   starting a conversation during a preview. Only the requested voice may play.
4. While the menu is open, run EVA > Interface > Validate Selection Menu.
   This checks the 6 x 3 x 2 x 2 combinations, Animator references, voice profiles,
   one active host and recording/text gating. It restores the selection and
   writes Logs/selection-menu-validation.txt without recording the microphone.
5. Start conversation, test a German and an English question, and check complete
   playback, lip-sync and the voice matching the chosen voice selection and response
   language. Select Deutsch and speak German, then select English and speak
   English. EVA must not switch languages by itself.
6. Show, hide, move and resize text boxes. Reopen the menu with M and repeat.
7. Repeat the input and recording checks with the real headset if VR is needed.
   Check both eyes, standing and seated: menu and text should be readable near
   EVA. Turn your head and confirm they stay in the scene. Test left X, left Y,
   right B and right-stick/A selection, including after moving. Loading/menu
   gates must still block recording. Simulated eye geometry tests do not replace
   this headset check.
8. Stop and restart Play several times, including after script compilation.
   Confirm loading, selection and complete playback work again without stale
   backend processes or shutdown errors.

Scene checks outside Play are available from the same validation menu item.
They do not prove that rendered UI, real microphone input or VR input works.

## Code files

- Assets/_Project/Scripts/EVASelectionMenu.cs builds and controls the interface.
- EVAAnimationController.SetStyle selects the existing animation controller.
- MicrophoneCapture.CanRecord combines loading readiness with menu gating.
- MicrophoneCapture.ResolveRecordingVoiceProfile prioritises the menu selection;
  without a menu, it retains the existing appearance-based fallback.
- MicrophoneCapture sends responseLanguage (en/de) with audio.start. The backend
  uses it for Whisper recognition, the LLM instruction and Piper voice. Older
  clients without this optional field default to fixed English.
- EVAConversationDisplay.SetMenuSuppressed hides text and its controls together.
- Desktop and VR movement respect the same menu gate.
- Assets/_Project/Scripts/VR/EVAVRPanelAnchor.cs positions and sizes VR surfaces.
- EVAVRController handles left X/Y for text visibility and panel repositioning.
- Assets/Editor/EVASelectionMenuSetup.cs provides scene setup and validation.

Run Backend/test_response_language.py with the project's Python environment to
check protocol validation, explicit Whisper language, switching across history,
matching voice/stream metadata, and the English default for older clients. It uses mocked models
and does not record audio or load GPU models; real spoken responses still need
a Play Mode test.

Backend/test_voice_preview.py tests preview protocol/cache/error isolation with
mocked models. Backend/test_live_voice_preview.py checks all four real previews
on an already running backend, including PCM format, audible signal and caching.
It does not play audio through Unity's speakers; test that in the Game view.

The preview icon now requires a CanvasRenderer, and menu creation also adds
one explicitly before adding the icon. A separate Unity check covered creation
and rendering/raycasting in play, loading and stop states. If an old
MissingComponentException appears, finish compilation and restart Play; do not
patch a temporary runtime button by hand. The separate check does not replace
a full microphone/menu/headset acceptance test.

## Startup connection errors

The launcher waits for the server.ready WebSocket message before releasing the
loading screen. It does not wait for the socket's entire receive task to finish.
If the server closes immediately with error 1011, inspect Backend/backend-runtime.log
and the process errors included in the Unity startup message. A listening port
alone does not prove that the backend can handle a connection.

Stop Play before changing scripts, then restart Play after compilation. The
launcher also cleans up its owned process trees on disable/script reload, so a
Windows Python child process cannot keep the old backend port occupied. It does
not stop an Ollama/backend process that was already running independently.
