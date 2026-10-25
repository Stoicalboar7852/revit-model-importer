# Revit Model Importer

An open-source Autodesk Revit add-in (Revit 2025, 2026 and 2027) that imports 3D models — OBJ, FBX, glTF/GLB, DAE,
STL, PLY, 3DS, 3MF and about 50 other formats — as DirectShape elements, with their materials and textures, and that
can simplify heavy models on the way in.

The 3D files are read by [Assimp](https://github.com/assimp/assimp) in a **separate worker process**, so a malformed
file that crashes a parser cannot take Revit (and your unsaved work) down with it.

> **Status:** everything that can run without Revit is built and tested on Linux and Windows CI. **Nothing has been
> run inside Revit yet** — see [Verification status](#verification-status) and
> [docs/MANUAL_TEST_PLAN.md](docs/MANUAL_TEST_PLAN.md).

## Features

- **Model Importer** ribbon tab with **Import 3D Model** and **Settings**.
- File dialog whose filter is built from the loader's own list of supported extensions, plus *All supported files*.
- Cancellable analysis, then an **Import dialog** with:
  - statistics: objects and meshes, **faces (triangles after triangulation)**, vertices, materials, textures found
    and missing, and the real-world size in the chosen units (metric and imperial);
  - options: source units (auto-detected when the file says), up axis (Y-up / Z-up, defaulted per format),
    placement (internal origin, project base point, centred on the origin), Revit category (only categories
    `DirectShape.IsValidCategoryId` accepts; Generic Models by default), elements (one per object or one
    merged element, optionally in a Revit model group named after the file — on by default), geometry (solids with mesh fallback, or mesh only), material mode, and **Simplify** with a
    target as a percentage or a face count and a live estimate;
  - an inline **simplification recommendation** (see below);
  - a **Locate textures folder…** button when textures are missing;
  - a warning when the chosen unit would make most faces smaller than Revit can represent.
- Import as **one undoable step** (one `TransactionGroup`, assimilated), with a responsive progress window and
  Cancel (which rolls everything back).
- A **summary**: elements and materials created/reused, faces before and after simplifying, faces removed for
  Revit's tolerances, faces Revit rejected (estimate), missing textures, elapsed time per phase, warnings.
- Settings saved per user in `%AppData%\RevitModelImporter\settings.json`.

### Simplification: recommended, never forced

- "Faces" means triangles after triangulation: a quad counts as 2, an n-gon as n − 2. The dialog shows that number.
- Above **50,000** faces (configurable) an inline notice says how many faces the model has, why that hurts in Revit
  (slow views and regeneration, larger project file) and suggests a target that brings it under the threshold, with
  an **Apply recommendation** button. Above **250,000** (configurable) the wording is stronger.
- Simplify is **never switched on for you**. It starts as you left it last time (off on first use).
- **Import is always enabled** and never asks "are you sure?".
- Any model can be simplified, and the recommendation can be turned off in Settings.

The simplifier is a quadric-error-metric edge collapser (Garland–Heckbert) that keeps open borders and the seams
between materials, never flips or degenerates triangles, and carries UVs through every collapse so baked colours
still land in the right place. A model-wide target is shared between objects in proportion to their size, with a
minimum per object so small parts don't disappear. 1,000,000 triangles → 100,000 takes about 2–3 s (Release build,
4-core container; ~1 s on the GitHub Linux runner).

## Supported formats

Everything this build of Assimp (6.0, via AssimpNetter 6.0.5) imports, minus what Revit imports itself and formats
without static geometry. Covered by automated tests: **OBJ (+MTL), FBX (binary and ASCII), glTF 2.0 (.gltf and
.glb), DAE, STL (ASCII and binary), PLY (ASCII and binary), 3DS, 3MF**.

Also offered (from Assimp's list, not individually tested): 3D, AC/AC3D/ACC, AMF, ASE/ASK, ASSBIN, B3D, BLEND,
BSP/PK3, COB/SCN, ENFF/NFF, HMP, IQM, IRR/IRRMESH, LWO/LXO/LWS, MD2/MD3/MD5MESH/MDC/MDL, MESH, MS3D, NDO, OFF, OGEX,
PMX, PRJ, Q3O/Q3S, RAW, SIB, SMD/VTA, TER, UC, VRM, X, X3D/X3DB, XGL/ZGL, ZAE.

Deliberately **not** offered: SKP, 3DM, IFC, DWG/DXF, SAT, STEP — use Revit's own *Insert ▸ Import CAD / Link*.

Units are read from the file where the format says: glTF/GLB (always metres), FBX (`UnitScaleFactor`), DAE
(`<unit>`), 3MF (`unit` attribute, default millimetres). For other formats you pick the unit; the dialog shows the
resulting size so you can check it, and remembers your choice per format.

## How textures are mapped (read this)

**Revit cannot use a model's UV coordinates.** The Revit 2025–2027 API has no way to pass texture coordinates to
`DirectShape` / `TessellatedShapeBuilder` geometry (verified against the API reference; see
[docs/DESIGN.md](docs/DESIGN.md) §1.1). Revit projects a material's image onto each face by itself, using only the
image's real-world size. A UV-unwrapped texture (a character, a scanned object, a game asset with a texture atlas)
therefore cannot line up in Revit. The importer offers three material modes and picks a default per model:

| Mode | What you get | Good for | Default when |
|---|---|---|---|
| **Textured** | A Revit material with the image. Its real-world size is measured from the mesh (median world size of one UV unit), so tiling textures come out at about the right scale. | brick, wood, tiles, fabrics | the texture coordinates repeat (tiling) |
| **Baked colors** | Each face gets the colour sampled from the texture under it (three corners + centre, times the base colour), reduced to a palette (default 32) of flat materials. | photogrammetry, scans, game assets with atlases | the texture coordinates stay inside one image |
| **Flat colors** | One plain colour per source material: its base colour, or the texture's average colour. | CAD-like models, very large models | there are no textures (or none were found) |

Other texture facts:

- Textures are **copied** to `<project folder>\<project name>_textures\` (or, for an unsaved project, to the folder
  in Settings, by default `%LocalAppData%\RevitModelImporter\textures`). **Revit stores only the path**: keep that
  folder with the project.
- PNG, JPG, BMP and TIFF are copied as is; TGA, DDS, PSD, GIF, HDR and similar are converted to PNG; embedded
  textures (GLB, FBX) are extracted. WebP and KTX2 are not supported (no permissively licensed decoder); those
  materials fall back to a flat colour and the summary says so.
- Missing textures are searched next to the model, in sibling `textures`/`tex`/`maps`/`images` folders, with
  case-insensitive names (files from other operating systems), and in folders you add with **Locate textures
  folder…**.
- Re-importing reuses identical materials and texture files instead of duplicating them.
- Bump/normal maps go into the Generic appearance's bump slot (normal maps render only approximately); transparency,
  glossiness and metalness are mapped. The full PBR → Revit mapping is in [docs/DESIGN.md](docs/DESIGN.md) §1.4.

## Installation

Requirements: Windows 10/11 x64 with Revit 2025, 2026 or 2027. Nothing else — the add-in uses the .NET runtime that
Revit itself runs on.

### Installer (recommended)

Run `RevitModelImporter-<version>-Setup.exe` (GitHub Actions artifact *RevitModelImporter-Setup*, or build it
yourself, below). One installer covers Revit 2025, 2026 and 2027:

- it ticks the Revit versions it finds on the PC (program folder, all-users add-ins folder, user profile or an
  earlier install) and lets you change the choice;
- it installs for the current Windows user only — the same files `Install.ps1` copies, in
  `%AppData%\Autodesk\Revit\Addins\<year>\` — so it needs no administrator rights;
- it asks you to close Revit first, replaces an earlier version completely, and appears in **Settings ▸ Apps ▸
  Installed apps** for uninstalling;
- silent: `RevitModelImporter-<version>-Setup.exe /S` installs for the versions found (exit code 2 if Revit is
  running); `"%LocalAppData%\Programs\Revit Model Importer\Uninstall.exe" /S` removes it.

The installer is not code-signed, so Windows SmartScreen may say "Windows protected your PC": choose **More info ▸
Run anyway**. Revit also asks once whether to load the unsigned add-in: choose **Always Load**.

### Zip and script

1. Download the zip for your Revit version (`RevitModelImporter-<version>-Revit<year>.zip`) from the GitHub Actions
   run (artifacts *RevitModelImporter-Revit2025/2026/2027*) or build it yourself (below).
2. Extract it and run, in that folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install.ps1
   ```

   This copies `RevitModelImporter.addin` and the `RevitModelImporter` folder to
   `%AppData%\Autodesk\Revit\Addins\<year>\` (per user, no administrator rights) and unblocks the downloaded files.
3. Start Revit and choose **Always Load** if Revit asks about the add-in.

Manual install: copy `RevitModelImporter.addin` and the `RevitModelImporter\` folder from the zip into
`%AppData%\Autodesk\Revit\Addins\<year>\` and unblock the files (right-click ▸ Properties ▸ Unblock).

**Uninstall:** use **Settings ▸ Apps** if you used the installer; otherwise close Revit and run `.\Uninstall.ps1` (add `-RemoveSettings` / `-RemoveTextureCache` to also delete
settings and the unsaved-project texture folder). Texture folders next to projects are never deleted.

## Usage

1. Open a project, go to **Model Importer ▸ Import 3D Model** and pick a file.
2. Wait for the analysis (Cancel stops it immediately).
3. In the Import dialog check the **size** (if it is wildly off, the unit is wrong), the up axis and the material
   mode. If the model is heavy, consider the recommendation — or ignore it.
4. Click **Import**. The conversion runs in the worker (cancellable), then elements are created in Revit with a
   progress window (Cancel rolls everything back).
5. Read the summary. One **Undo** removes the whole import.

### Finding and selecting an import

Imported pieces are DirectShape elements, not families, so Revit does not list them under **Families** in the
Project Browser (not even under Generic Models). With **Put them in a Revit group named after the file** ticked
(the default), the import is one model group:

- it is listed in the Project Browser under **Groups ▸ Model ▸ <file name>**;
- clicking any piece selects the whole import, so it moves, copies and deletes as one;
- right-click the group in the Project Browser ▸ **Select All Instances** finds it in a view or the whole project;
- press **Tab** while hovering to pick a single piece, or **Ungroup** to break it apart.

Without the group, drag a selection box around the model and use **Filter** (Modify tab) to keep only its
category.

Each element's `ApplicationId` is `RevitModelImporter/<AddInId>` and its `ApplicationDataId` is
`<source file>#<node path>`, so imported elements can be traced back to their source.

## Settings

**Model Importer ▸ Settings** edits the important ones; everything is in `%AppData%\RevitModelImporter\settings.json`:

| Key | Default | Meaning |
|---|---|---|
| `showSimplifyRecommendation` | `true` | Show the inline recommendation |
| `recommendThreshold` | `50000` | Recommend simplifying above this many faces |
| `strongRecommendThreshold` | `250000` | Stronger wording above this many faces |
| `fallbackTextureFolder` | `null` (→ `%LocalAppData%\RevitModelImporter\textures`) | Texture folder for unsaved projects |
| `alwaysUseFallbackTextureFolder` | `false` | Use that folder even for saved projects |
| `maxTrianglesPerBuild` | `20000` | Faces per `TessellatedShapeBuilder.Build` call (large material groups are split) |
| `shortEdgeWarningFraction` | `0.05` | Warn about wrong units above this fraction of too-small faces |
| `textureSearchFolders` | `[]` | Folders added with *Locate textures folder…* (most recent first) |
| `lastModelFolder` | | Where the file dialog opens |
| `lastUsed.*` | | Last choices: unit and up axis per format, placement, category, grouping, geometry mode, simplify on/off and target, palette size |

A file that cannot be read is kept as `settings.json.bad` and defaults are used.

## Limitations

- **Texture alignment**: see [How textures are mapped](#how-textures-are-mapped-read-this). UV-unwrapped textures
  will not line up in Textured mode; Baked colors trades image detail for correct colour placement.
- **Faces smaller than Revit's tolerances are lost.** Revit cannot represent edges shorter than its short-curve
  tolerance (≈0.8 mm, read at runtime). Such edges are collapsed before import and counted; tiny details of real
  models may disappear, and a model read in the wrong unit can lose almost everything (the dialog warns first).
- **Rejected faces are an estimate.** Revit reports build issues per face set, not per face.
- **Meshes, not editable Revit geometry.** Imported elements are DirectShapes: they can be categorised, scheduled,
  hidden and materials can be changed, but they are not families and cannot be edited with Revit's modelling tools.
  Most real-world meshes become Revit meshes (not solids), which cannot be cut, joined or used in Boolean operations.
- **Instancing is flattened**: every instance of a repeated mesh is a separate copy (DirectShapeLibrary instancing
  was not implemented because its benefit could not be measured without Revit).
- **No animation, skinning, lights, cameras, points or lines**; only surfaces are imported.
- **Material mapping is approximate**: emissive, occlusion and metallic/roughness textures are not mapped; normal
  maps are used as bump maps.
- **WebP/KTX2 textures** are not converted.
- **Very large models**: the worker reads the whole file into memory (a 64-bit process, but still bounded by RAM);
  creating millions of faces in Revit is slow — simplify or use *Mesh only*.
- **Assimp's own limits apply** to each format (e.g. FBX features newer than Assimp supports). A file that crashes
  Assimp ends only the worker; you get an error message instead.

## Building from source

Requirements: .NET SDK 10 (builds every target) and the .NET 8 runtime for running the tests. Windows or Linux —
the WPF add-in builds on Linux thanks to `EnableWindowsTargeting`.

```bash
dotnet build RevitModelImporter.sln -c Release          # Core, Worker, Tests, add-in for Revit 2026 and 2027
dotnet build src/RevitModelImporter.Revit -c Release -f net8.0-windows -p:RevitVersion=2025   # Revit 2025
dotnet test RevitModelImporter.sln -c Release           # 161 tests, Linux and Windows
pwsh ./packaging/Build-Package.ps1                      # dist/RevitModelImporter-<version>-Revit<year>.zip
                                                        # and dist/RevitModelImporter-<version>-Setup.exe
```

The installer needs NSIS 3 (`makensis`; zlib licence, build time only), on Windows or Linux. Without it the zips are
still built and a warning says the installer was skipped. Its icon and wizard images in `packaging/installer/art`
are drawn by `packaging/installer/make-art.py` (Pillow) from the ribbon icon's design.

The Revit API comes from the `Nice3point.Revit.Api.RevitAPI`/`RevitAPIUI` reference-assembly packages (compile time
only, never shipped). No Autodesk DLL is in the repository or the packages.

To keep the generated test models (for manual testing):
`RMI_FIXTURES_DIR=/some/folder dotnet test -c Release --filter FixtureSurvey`.

### Layout

| Project | Target | Role |
|---|---|---|
| `src/RevitModelImporter.Core` | `net8.0`, no dependencies | scene model, units/axes, flattening, cleanup, simplifier, textures & baking, intermediate file format, worker protocol and client, settings, **view models** |
| `src/RevitModelImporter.Worker` | `net8.0` console, `RollForward=LatestMajor` | `formats`, `analyze`, `convert` with Assimp; JSON-lines progress on stdout; meaningful exit codes |
| `src/RevitModelImporter.Revit` | `net8.0-windows;net10.0-windows`, WPF | ribbon, commands, dialogs, Revit materials and DirectShapes |
| `tests/RevitModelImporter.Tests` | `net8.0`, xUnit | Core unit tests, view-model rule tests, worker end-to-end tests on generated fixtures, XAML binding checks |

Design decisions, research findings (with what was checked and how) and deviations from the brief are in
[docs/DESIGN.md](docs/DESIGN.md). Worker usage: `RevitModelImporter.Worker --help`.

## Verification status

**Verified here** (Linux container; plus GitHub Actions on `windows-latest` and `ubuntu-latest`):

- The whole solution builds with 0 warnings (warnings are errors in CI), including the add-in for Revit 2025, 2026
  and 2027 against each year's reference assemblies; the three packages build and contain no Autodesk binaries.
- 159 automated tests pass on Linux and Windows:
  - **Worker end to end** (real worker process, generated fixtures): every required format analyses and converts
    with the right face count and size; embedded textures (GLB, FBX binary/ASCII) are extracted; external textures
    are copied, de-duplicated and sized from UVs; multi-material objects keep their materials; nested transforms
    and instancing are flattened correctly; Y-up and centimetre models are handled; a tiny model read in the wrong
    unit is flagged before and after conversion; the 300k-face sphere triggers the strong recommendation and
    simplifies to the suggestion within ±5 %; missing textures are reported and can be located in another folder
    (case-insensitively); corrupt files return exit code 5; a native crash only ends the worker (a deliberate one on both
    systems, and a real Assimp segfault on a malformed PLY, observed on Linux); Cancel kills the worker.
  - **Simplifier**: target within ±5 %, bounding box within 1 %, borders and material seams kept exactly, no
    degenerate or flipped triangles, UVs carried, 1M → 100k triangles in a few seconds.
  - **View model**: one test per recommendation rule (and more), estimates, units/size, remembered choices.
  - Units/axes, cleanup, allocation, intermediate file format, PNG writer, decoders, texture resolver, palette,
    baking, settings, naming, file filters, summary text, and that every XAML binding exists on its view model.
- The `net8.0` worker runs on both runtimes Revit brings: the worker tests pass with it rolled forward to .NET 10
  (as for Revit 2027; CI's default) and pinned to .NET 8 (as for Revit 2025/2026; a separate CI step). The worker
  reports the runtime it runs on.
- `Install.ps1` / `Uninstall.ps1` were run with PowerShell 7 against a temporary folder (install, reinstall,
  wrong-year refusal, uninstall).

**Reported from Revit 2025.5 by a tester:** the add-in loads, the ribbon tab appears, and a model imports as
DirectShape elements (one per object).

**Needs verification in Revit** (not run — there is no Revit here): the ribbon and icons; dialogs on screen and their
parenting; the add-in actually loading in Revit 2025/2026/2027; DirectShape creation, categories, solids vs. mesh
fallback and rejected-face counts; putting the elements in a model group; appearance assets, texture attachment and real-world texture size; material
reuse via Extensible Storage; one-step undo and cancel rollback; the warning suppression; the background progress
window; timings of element creation and the best `maxTrianglesPerBuild`; running the packaged worker on a machine
with only Revit's .NET runtime. The checklist is [docs/MANUAL_TEST_PLAN.md](docs/MANUAL_TEST_PLAN.md).

## Next steps

- Run the manual test plan in Revit 2025, 2026 and 2027 and fix what it finds.
- Check whether Revit's native OBJ import (`Document.Import` with `OBJImportOptions`, Revit 2022.1+) keeps UV-mapped
  textures; if it does, offer it as a fourth mode for textured models (see docs/DESIGN.md §1.1).
- Measure `DirectShapeLibrary`/`DirectShapeType` instancing for repeated meshes and the build chunk size.
- A 3D preview in the Import dialog (e.g. HelixToolkit, MIT) with the simplified result.
- Import as a loadable family (`.rfa`, `FreeFormElement`/DirectShape in a family document) so models can be placed
  many times and scheduled as types.
- Batch import of several files with shared options.
- Revit 2024 and earlier (.NET Framework 4.8): a `net48` add-in target; Core would need `netstandard2.0`.
- Code signing of the add-in and worker; an MSI/MSIX installer for all users.

## License

MIT — see [LICENSE](LICENSE). Third-party components and their licences: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Autodesk and Revit are trademarks of Autodesk, Inc.; this project is not affiliated with Autodesk.
