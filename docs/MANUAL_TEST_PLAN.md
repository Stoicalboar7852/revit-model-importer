# Manual test plan (Windows + Revit)

Nothing in this plan could be run in the build environment (Linux, no Revit). Everything the automated tests
cover is listed in the README's *Verification status*; this checklist covers what only Revit can show.

Record the Revit version and build (Help ▸ About) for each run. Tick each box, and note anything unexpected
with a screenshot.

## 0. Preparation

- [ ] Windows 10/11 x64 with Revit 2025, 2026 or 2027 (ideally all three, run the plan once per year).
- [ ] Download the package for that year from the CI run (artifact `RevitModelImporter-Revit<year>`) or build it:
      `pwsh ./packaging/Build-Package.ps1 -RevitVersion <year>` → `dist\RevitModelImporter-<version>-Revit<year>.zip`.
- [ ] Generate the test models (built in code, nothing is downloaded): in a clone of the repository run
      `$env:RMI_FIXTURES_DIR = "C:\Temp\RmiFixtures"; dotnet test -c Release --filter FixtureSurvey`.
      The models stay in that folder. Useful files: `cube.obj` (+ `cube.mtl`,
      `textures\brick.png`), `cube.gltf`, `cube.glb`, `cube.dae`, `cube.fbx`, `cube_ascii.fbx`, `cube_ascii.stl`,
      `cube_binary.stl`, `cube_ascii.ply`, `cube_binary.ply`, `cube.3ds`, `cube.3mf`, `multimaterial.glb`,
      `nested.gltf`, `tall_yup.glb`, `centimeters.fbx`, `centimeters.obj`, `tiny_part_in_meters.obj`,
      `sphere_300k.ply`, `missing_texture.obj`, `corrupt_random.fbx`, `corrupt_truncated.glb`, `empty.stl`.
- [ ] Also have a few real-world models: a textured FBX or GLB from a 3D store, a photogrammetry scan (OBJ + JPG
      atlas), a CAD export (STEP→OBJ or STL), something above 1 million triangles.
- [ ] Two projects: one **saved** project (`C:\Temp\ImportTest\ImportTest.rvt`) and one new **unsaved** project.

## 1. Install

- [ ] Extract the zip. Right-click → Properties: no "Unblock" checkbox needed after installing (the script unblocks).
- [ ] Run `powershell -ExecutionPolicy Bypass -File .\Install.ps1`. It reports the target
      `%AppData%\Autodesk\Revit\Addins\<year>`.
- [ ] That folder now contains `RevitModelImporter.addin` and `RevitModelImporter\` with
      `RevitModelImporter.Revit.dll`, `RevitModelImporter.Core.dll` and `Worker\` (with `RevitModelImporter.Worker.exe`
      and `assimp.dll`). No `RevitAPI*.dll` anywhere.
- [ ] `.\Install.ps1 -RevitVersion <other year>` refuses ("This package is built for Revit …").
- [ ] Start Revit. If the security dialog appears, choose **Always Load**. No error dialog at startup.
- [ ] Ribbon tab **Model Importer** exists with **Import 3D Model** and **Settings** buttons, both with icons
      (32 px on the ribbon; check the 16 px version by shrinking the panel if possible).
- [ ] With a project open in any view (including a 3D view), **Import 3D Model** is enabled. With no project open
      it is either greyed out (Revit's default) or shows "No project is open" when clicked.

### 1b. Installer (`RevitModelImporter-<version>-Setup.exe`)

Checked under Wine (64-bit build of the same script; see `docs/DESIGN.md`), not yet on Windows.

- [ ] Double-click it. SmartScreen may warn (unsigned): **More info ▸ Run anyway**. No UAC prompt appears.
- [ ] The welcome page shows the blue side image, the product name and version, crisp text at 100 % and 150 % scaling.
- [ ] **Choose Revit versions**: the installed Revit versions are ticked; the others say "(not found)" and are
      unticked. Hovering a version explains why. Untick everything and click Install → "Tick at least one Revit
      version."
- [ ] Start Revit, then click Install → "Revit is running … click Retry". Cancel stays on the page; close Revit and
      Retry continues.
- [ ] After installing, `%AppData%\Autodesk\Revit\Addins\<year>` holds the same files as the zip install (above) for each
      ticked year, and nothing for unticked years.
- [ ] **Settings ▸ Apps ▸ Installed apps** lists **Revit Model Importer** with the icon, version 1.0.0 and the
      publisher.
- [ ] Revit starts with the **Model Importer** tab (Always Load once).
- [ ] Run the installer again (upgrade): it completes; no duplicate entry in Installed apps.
- [ ] Uninstall from Installed apps: it asks to close Revit if it is running, removes the add-in from every Revit year
      and the Installed apps entry; `%AppData%\RevitModelImporter\settings.json` is kept.
- [ ] Silent: `Setup.exe /S` installs for the found versions (exit code 0; 2 while Revit runs);
      `"%LocalAppData%\Programs\Revit Model Importer\Uninstall.exe" /S` removes it.

## 2. File picker

- [ ] Click **Import 3D Model**. A file dialog opens **in front of Revit**, and Revit's window cannot be clicked
      while it is open (it is owned by Revit's main window).
- [ ] The filter list starts with **All supported files**, then OBJ, FBX, glTF (*.gltf;*.glb), DAE, STL, PLY, 3DS, 3MF,
      then the other formats alphabetically, then *All files*.
- [ ] No IFC, DXF/DWG, SKP, 3DM, SAT or STEP entries.
- [ ] Cancel → nothing happens, no error.
- [ ] The dialog reopens in the folder used last time.

## 3. Analysis progress

- [ ] Pick `sphere_300k.ply`. A small "Analyzing …" progress window appears in front of Revit with a progress bar and
      a **Cancel** button; it closes by itself.
- [ ] Repeat with a very large real model and click **Cancel** during analysis: the window closes within a second,
      no Import dialog appears, Revit stays responsive, and Task Manager shows no leftover
      `RevitModelImporter.Worker.exe`.

## 4. Import dialog — statistics and options

Using `cube.obj` unless noted:

- [ ] The dialog is owned by Revit (centred on it, Revit disabled while it is open).
- [ ] Statistics: **12 faces (triangles)** with "from 6 polygons in the file", 1 object, 1 mesh, 8 vertices,
      materials 1, textures "1 found".
- [ ] Units: OBJ shows "The file does not say which unit it uses"; changing the unit updates **Size** immediately
      (meters → `1 m × 1 m × 1 m`, millimeters → `1 mm × 1 mm × 1 mm`) and the imperial line.
- [ ] `cube.glb`: unit is Meters "From the file: glTF is always in meters". `centimeters.fbx`: Centimeters
      "FBX UnitScaleFactor = 1 cm per unit", size `2 m × 0.5 m × 1 m`.
- [ ] Up axis: OBJ/glTF/FBX default Y-up, STL/PLY/3MF/3DS default Z-up. With `tall_yup.glb`, Y-up shows height 3 m,
      Z-up shows height 1 m.
- [ ] Placement offers Internal origin / Project base point / Centered on the origin.
- [ ] Category list contains only model categories (Generic Models selected by default; e.g. Furniture, Planting,
      Site, Mass … present; no annotation categories).
- [ ] Elements: One element per object / Single merged element, and **Put them in a Revit group named after the
      file** ticked by default (also for a settings file from an earlier version). Geometry: Solids where possible /
      Mesh only.
- [ ] Material mode defaults: `cube.obj` (UVs inside 0–1) → **Baked colors** with a one-line explanation and a
      "Default for this model" reason; `multimaterial.glb` (no textures) → **Flat colors**. Palette size field appears
      only for Baked colors.
- [ ] Texture-folder note: for the saved project it names `C:\Temp\ImportTest\ImportTest_textures` and says to keep
      it with the project; for the unsaved project it names `%LocalAppData%\RevitModelImporter\textures`.
- [ ] `tiny_part_in_meters.obj` with **Millimeters**: a red notice says nearly all faces are below Revit's minimum
      and the unit is probably wrong; switching to **Meters** removes it.

## 5. Simplification recommendation (must be ignorable)

- [ ] `cube.obj` (12 faces): no recommendation notice. Simplify is unchecked (first run).
- [ ] `sphere_300k.ply` (~299,040 faces): an **inline** notice (not a pop-up) in the strong style (red/pink) titled
      "Simplifying is strongly recommended", stating the face count, why (slow views/regeneration, bigger file),
      and "simplify to about 45,000 faces"; an **Apply recommendation** button.
- [ ] Simplify is still **unchecked**. The **Import** button is enabled.
- [ ] Click **Import** without simplifying: no "Are you sure?" dialog; the import proceeds (verify in §7).
- [ ] Open the importer again on the sphere, click **Apply recommendation**: Simplify becomes checked, "Target
      45000 faces" is selected, the estimate says about 45,000 faces. Uncheck Simplify again: the estimate returns to
      299,040. Nothing else changed.
- [ ] A model between 50,000 and 250,000 faces shows the normal (yellow) notice with milder wording.
- [ ] Simplify can be turned on for `cube.obj` too; the estimate updates live while dragging the slider, and when
      you leave the percentage or face-count box (Tab or click elsewhere).
- [ ] Face-count box: select all and type `2500` — it stays `2500` while typing (the leading `2` is not changed to 4).
      Type `2` and press Tab → it becomes 4. Type `3000` and press **Enter** right away → the import uses 3,000.
- [ ] Settings button: set **Recommend above** to 10 and **Strongly recommend above** to 20, Save. `cube.obj`
      (12 faces) now shows the normal notice. Set strong < normal → Save is disabled with a message.
- [ ] Settings: uncheck **Show a recommendation …**, Save. The sphere shows no notice. Re-enable afterwards.
- [ ] Settings persist across Revit restarts (`%AppData%\RevitModelImporter\settings.json` contains the values).
- [ ] Simplify starts in the last-used state: import once with Simplify on → next import starts with it on; import
      with it off → next starts off.

## 6. Each format

For each file, import with defaults into the saved project, Generic Models, one element per object. Check the
element appears, has the right size (Measure tool), the right orientation (up is up) and colours/materials.

| File | Expected |
|---|---|
| [ ] `cube.obj` | 1 element, 1 m cube, brick colours/texture per material mode |
| [ ] `cube.gltf` | same, glTF unit detected |
| [ ] `cube.glb` | same, texture embedded → extracted into the texture folder |
| [ ] `cube.dae` | 1 m cube |
| [ ] `cube.fbx` / `cube_ascii.fbx` | 1 **cm** cube (the FBX says cm), embedded texture extracted |
| [ ] `cube_ascii.stl` / `cube_binary.stl` | cube, size per chosen unit (default mm → 1 mm) |
| [ ] `cube_ascii.ply` / `cube_binary.ply` | cube |
| [ ] `cube.3ds` | cube (Z-up) |
| [ ] `cube.3mf` | 1 mm cube (3MF default unit) |
| [ ] `multimaterial.glb` | 1 element, red half and blue half |
| [ ] `nested.gltf` | 2 elements; one 2 m cube rotated and offset ~15–17 m in X, one unit cube at the origin |
| [ ] Real textured FBX/GLB | plausible look; note how Revit maps the texture |
| [ ] Real scan (OBJ + JPG) | Baked colors default; colours land on the right areas |

- [ ] Select an imported element: Properties shows the chosen category; Revit Lookup (if installed) shows
      `ApplicationId` = `RevitModelImporter/d31302fc-…` and `ApplicationDataId` = `<source path>#<node path>`.
- [ ] Element names match the object names in the file (Revit Lookup / schedules).

## 7. Element creation, undo and cancel

- [ ] During a large import (sphere without simplifying, or a >1M-face model with Mesh only), a progress window
      "Creating elements …" stays responsive and shows progress while Revit is busy. (It is topmost and centred on
      Revit rather than owned by it — by design, see `BackgroundProgress.cs`.)
- [ ] After the import: one **Undo** entry "Import 3D model (<file>)". Ctrl+Z removes all elements and materials of
      that import in one step; Redo restores them.
- [ ] Start a large import and press **Cancel** in the "Creating elements" window: within one element's time the
      import stops, the summary says "Import cancelled. Nothing was added to the project.", and the project contains
      no new elements or materials (check Manage ▸ Materials). The Undo list has no entry.
- [ ] Cancel during **Converting** (worker running): stops immediately; no worker process remains.
- [ ] No Revit warning dialogs pop up during import, even for models with many faces Revit dislikes; the summary
      reports "Revit warnings suppressed" when there were some.

## 8. Summary

- [ ] After each import a summary window (owned by Revit) lists: elements created; materials created/reused;
      faces in the file; faces after simplifying; faces removed for Revit tolerances; faces sent to Revit; faces Revit
      rejected (estimate); missing textures; elapsed time split into analysis / conversion / creating elements;
      warnings; the texture folder with the "keep it with the project" note.
- [ ] **Copy to clipboard** copies the same text.
- [ ] For `sphere_300k.ply` simplified to the recommendation: faces after simplifying ≈ 45,000 (±5 %).
- [ ] Note the timings for the sphere (unsimplified, Solids mode and Mesh-only mode) and for a >1M model in the
      results, to tune `MaxTrianglesPerBuild` in Settings ▸ Advanced (default 20,000). Try 5,000 and 100,000 and
      record creation time and rejected faces.

## 9. Textures and materials

- [ ] **Textured** mode on `cube.obj`: material "cube - Brick" has a Generic appearance whose Color/Image shows the
      brick image; Realistic view shows the checker. The image path points into the project's `_textures` folder,
      file name `brick_<hash>.png`. Texture size (Appearance ▸ Image ▸ Scale) is ~1 m × 1 m (3' 3 3/8").
- [ ] **Baked colors** on a textured model: materials named "<model> baked #RRGGBB", at most *palette size* of them,
      faces coloured plausibly in Shaded and Realistic views.
- [ ] **Flat colors**: one material per source material; textured materials get the texture's average colour.
- [ ] Re-import the same file with the same options: the summary says materials **reused**, no "Brick (2)"
      materials appear, and no new texture file is written.
- [ ] Transparent material (glTF alpha < 1 or OBJ `d 0.5`): Transparency set on the material and appearance.
- [ ] Material names with `{ } [ ] | ; < > ? ` ~` in the source file are imported with those characters replaced.
- [ ] Missing texture: `missing_texture.obj` shows "1 missing" and the file name in red, plus **Locate textures
      folder…**. Choose a folder containing a copy of `brick.png` renamed to `MISSING_TEXTURE.PNG` (different case):
      the dialog re-analyses and reports the texture found; the default material mode switches accordingly.
- [ ] Missing texture left missing: import still succeeds; the summary lists the missing texture.
- [ ] Unsaved project: textures go to the fallback folder (or the folder chosen in Settings). Save the project,
      import again: textures now go next to the project.
- [ ] Revit's material library unavailable (if you can test it — e.g. a machine without the Autodesk Material
      Library): import still works, materials have colours only, and the summary warns that textures could not be
      attached.
- [ ] TGA/DDS/PSD textures (e.g. from a game asset) are converted to PNG in the texture folder; WebP textures are
      reported as unsupported and fall back to flat colour.

## 10. Placement and grouping

- [ ] **Internal origin**: `nested.gltf` instance cube corner at (0,0,0) internal.
- [ ] **Project base point**: move the project base point, import → the model origin sits at the base point.
- [ ] **Centered on the origin**: the model's plan centre is at the internal origin and its base at elevation 0.
- [ ] **Single merged element**: `nested.gltf` gives one element containing both cubes.
- [ ] **Group** (default): after importing `nested.gltf` the Project Browser shows **Groups ▸ Model ▸ nested**; clicking
      either cube selects both; the summary has a "Group" line. Import it again → **nested (2)**. Ctrl+Z removes the
      elements and the group in one step. Tab picks a single cube; Ungroup leaves both cubes in the project.
- [ ] Untick the group option: no new group, the elements are still created, and the option stays unticked next time.
- [ ] Choose **Furniture** as category: elements are Furniture, scheduled as such.

## 11. Corrupted and odd files

- [ ] `corrupt_random.fbx`, `corrupt_truncated.glb`, `empty.stl`: a Revit task dialog "The model could not be
      imported" explains the file could not be read; **Revit keeps running**, the project is unchanged.
- [ ] A file that crashes the reader (e.g. `crash_assimp_malformed.ply` from the fixtures — it segfaults Assimp on
      Linux; behaviour on Windows may differ): Revit shows "The converter process stopped unexpectedly … Revit and
      your project are not affected" and keeps running.
- [ ] A `.txt` renamed to `.obj`: error or empty-model message, no crash.
- [ ] A file on a network share or with non-ASCII characters in its path (e.g. `C:\Temp\Modèle 3D\würfel.obj`)
      imports correctly.
- [ ] Rename `Worker\RevitModelImporter.Worker.exe` temporarily: the command reports that the converter is missing
      or cannot start; restore it afterwards.

## 12. Uninstall

- [ ] Close Revit. Run `powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1`. It reports what it removed.
- [ ] `%AppData%\Autodesk\Revit\Addins\<year>` no longer contains `RevitModelImporter.addin` or the folder.
- [ ] Start Revit: no Model Importer tab, no error. Projects that contain imported elements still open and display
      them (DirectShapes and materials live in the project; textures still load from the `_textures` folder).
- [ ] `.\Uninstall.ps1 -RemoveSettings -RemoveTextureCache` also deletes `%AppData%\RevitModelImporter` and
      `%LocalAppData%\RevitModelImporter`. Project `_textures` folders are never deleted.
