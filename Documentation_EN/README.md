# EVA documentation in simple English

Updated 29 October 2026 for the current menu, VR controls and panel placement.
The report retains Overleaf's Technical Document Template and its original class.

## What to open

- **EVA_Project_Documentation_EN.pdf**: the full report, tools and reasons,
  development lessons, current limits, maintenance notes, package list and sources.
- **EVA_Cheat_Sheet_EN.pdf**: the shared two-page setup and control guide.
- **EVA_Setup_Guide_EN.pdf**: from the copied/cloned folder to the first conversation.
- **INTERFACE_SETUP.md**: focused menu setup, operation and acceptance checks.

All describe one visible EVA, six designs, three animation styles, fixed manual
language, independent voice selection and inline play/stop previews. The old
automatic-language and Inspector-only usage instructions were replaced.
There is no Goal Summary section.

The guides also explain right-stick/A menu navigation, right B for settings,
right A for recording, left X for conversation text and left Y for repositioning
the VR surfaces. Menu and text stay near EVA instead of following head rotation.
Placement is adjusted in EVAControlSettings under VR panels (metres).

## Edit and build in Visual Studio Code

1. Open this `Documentation_EN` folder in **VS Code**.
2. Install MiKTeX or TeX Live if `pdflatex` is missing. LaTeX Workshop can provide
   an editor PDF preview. Visual Studio and VS Code have different build commands.
3. Edit `main.tex` or files in `sections/`; edit `sections/setup_guide.tex` for
   the detailed setup guide. Save before building.
4. Press **Ctrl+Shift+B** and select **Build EVA documentation (all PDFs)**.
   Alternatively run PowerShell in this folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The three PDFs above are replaced with the new output. Temporary files go to
`build/`. Open the new PDFs or refresh their preview after the build completes.

The original template needs Roboto, Roboto Mono, sfmath, fullpage and parskip.
The document also uses standard geometry, listings, tabularx, booktabs, enumitem,
hyperref, microtype and babel packages. Use the TeX distribution's package manager
for missing LaTeX packages; these are separate from EVA's Python requirements.
The build does not use minted, shell escape, Pygments, Perl or latexmk.

## Path examples

Use the neutral example path `C:\Projects\EVA` in all setup instructions.
Readers should replace it with their own project location. Do not include
personal usernames or paths to a developer's Desktop or home directory.

## Shared sources and evidence

`sections/cheat_sheet.tex` feeds both the standalone cheat sheet and report
Section 1. Keep it within two pages. `ol-softwaremanual.cls` is unchanged.

`reference/source-inventory.json` records the reviewed current code and scene.
`reference/requirements-snapshot.txt` and `requirements-pins.txt` are refreshed
copies of the real dependency file. Install with `Backend/setup_backend.ps1`,
not these documentation snapshots.

`reference/pc-backend-validation-2026-10-28.md` records what passed and what was
not tested. Automated/backend checks do not prove real microphone accuracy,
Unity speaker output, complete headset interaction or a clean packaged build.
The latest synthetic German test contained transcription errors.

Scene values currently include a 60-second audio queue and 50% maximum viseme
weight; script defaults can differ. See `TEMPLATE_NOTES.md` for template credits.
