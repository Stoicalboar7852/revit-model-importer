# Task: Build "Revit Model Importer", a Revit add-in that imports 3D models with their textures and can optionally simplify them

You are a senior .NET and Revit API engineer. The repository (`revit-model-importer`) is empty. Build a production-quality Autodesk Revit add-in from scratch. Verify everything you can in this environment. When you finish, a Windows user with Revit should be able to build, install and test it.

## Assumptions (defaults; edit before running if they're wrong)

- **Target Revit versions:** 2025 and 2026 (.NET 8, `net8.0-windows`) and 2027 (.NET 10, `net10.0-windows`). Revit 2024 and earlier (.NET Framework 4.8) are out of scope.
- **Licensing:** open source. Every dependency must use a permissive license (MIT, BSD, Apache-2.0, zlib or public domain). That rules out the Autodesk FBX SDK and SixLabors.ImageSharp (Six Labors Split License).
- **Face-count thresholds:** recommend simplifying above 50,000 triangles, and recommend it more strongly above 250,000. The user can change both.
- **UI language:** English.

## What the user gets

1. A ribbon tab, "Model Importer", with an **Import 3D Model** button.
2. A file picker. Its filter lists every format the importer actually supports, built from the loader's own extension list rather than hard-coded, plus "All supported files".
3. The file is analyzed behind a progress indicator that can be cancelled. An **Import dialog** then shows:
   - **Stats:** objects and meshes, triangle count, vertices, materials, textures found and missing, and bounding-box size in the chosen units.
   - **Options:**
     - Source units: auto-detected when the format says, otherwise the user picks. Show the resulting real-world size so the user can check the scale.
     - Up axis: Y-up or Z-up, defaulted per format.
     - Placement: internal origin, project base point, or centered on the origin.
     - Revit category: Generic Models by default. Offer only categories where `DirectShape.IsValidCategoryId` is true.
     - Grouping: one element per object, or a single merged element.
     - Material mode: see the texture section.
     - **Simplify:** on or off, with a target given as a percentage of faces or as a triangle count. Show the estimated resulting count live.
   - The **simplification recommendation** described below.
4. The import runs as a single undoable step (one `TransactionGroup`, assimilated). Afterwards, a summary shows:
   - elements and materials created
   - triangle counts before and after simplifying
   - triangles Revit rejected
   - missing textures
   - elapsed time and warnings
5. Thresholds, last-used options and the texture folder are saved per user in a JSON file under `%AppData%`.

Parent all dialogs to Revit's main window.

## Hard requirement: recommend simplifying, never force it

- "Face count" means triangles after triangulation. A quad counts as two, and an n-gon counts as however many triangles it becomes. Show this number.
- When the count is above the threshold, show a clearly visible **inline** notice in the Import dialog, not a blocking pop-up. The notice must:
  - say how many faces the model has
  - say why that's a problem in Revit (slow views and regeneration, larger project files)
  - suggest a target, for example one that brings the model under the threshold
  - offer an **"Apply recommendation"** button that turns on Simplify with the suggested target
- Simplify is **never** switched on silently. It starts in the user's last-used state, which is off by default.
- The Import button is always enabled. Importing without simplifying never triggers an "Are you sure?" confirmation.
- Above the second threshold the wording gets stronger, but the notice still doesn't block anything.
- The user can simplify any model, including one below the threshold, and can turn the recommendation off in settings.
- Put these rules in the dialog's view model and give each one a unit test.

## Architecture (decided; to deviate, write down the reason in `docs/DESIGN.md`)

Parsing and simplifying run **in a separate worker process**. A native crash in a 3D-format parser on a malformed file would otherwise take down Revit along with the user's unsaved work. A separate process also:
- keeps huge models out of Revit's memory
- lets Cancel stop the work immediately by killing the process
- keeps native libraries away from the assemblies Revit and other add-ins load

**Projects:**

- **`RevitModelImporter.Core`** (`net8.0`; no Revit, no native code):
  - a format-neutral scene model: meshes, materials, textures, node transforms
  - unit and axis conversion, and mesh cleanup (vertex welding, removing degenerate triangles)
  - statistics and the recommendation logic
  - the simplifier and the texture-to-color baking
  - a versioned intermediate file format (JSON header plus binary buffers) shared by the worker and the add-in
- **`RevitModelImporter.Worker`** (`net8.0` console app): loads files with Assimp and writes the intermediate file.
  - Command line: `analyze <file>` prints stats as JSON. `convert <file> --out <path> [--simplify <ratio|count>] [--units …] [--up …] [--material-mode …]` writes the intermediate file.
  - Progress goes to stdout as JSON lines. Failures return a meaningful exit code.
  - It must run on a machine that only has the .NET runtime Revit installed. Use `RollForward=LatestMajor` or a self-contained win-x64 publish; pick one and justify it.
- **`RevitModelImporter.Revit`** (multi-target `net8.0-windows;net10.0-windows`, WPF, MVVM):
  - covers the ribbon, the command, the dialogs, managing the worker process, and creating Revit materials and geometry from the intermediate file
  - keep it thin, with as few dependencies as possible (ideally only Core and the base class library), to avoid assembly conflicts with Revit and other add-ins
- **`RevitModelImporter.Tests`** (xUnit): tests for Core, end-to-end tests of the worker, and view-model tests. All must run on Linux.

## Formats

Use Assimp through a maintained .NET binding that ships native binaries for both **win-x64 and linux-x64**. Compare candidates such as AssimpNetter and Silk.NET.Assimp on:
- how actively they're maintained
- which Assimp version they bundle
- their license
- which runtime identifiers (platforms) they ship natives for

Supported formats are whatever that Assimp build can import.

These formats must work and be covered by tests:
- OBJ with MTL
- FBX
- glTF 2.0 (`.gltf` and `.glb`)
- DAE
- STL (ASCII and binary)
- PLY
- 3DS
- 3MF

Leave SKP, 3DM, IFC, DWG/DXF and SAT out of the filter. Revit imports those natively, so don't compete with it.

Handle all of the following:
- the node hierarchy and its transforms (flattened into world space), including instanced meshes
- several materials on one mesh
- embedded textures (GLB, FBX)
- external textures, resolved relative to the model file. Fall back to:
  - sibling `textures/` folders
  - case-insensitive name matching (files often come from other operating systems)
  - a "Locate textures folder…" option for anything still missing
- unit metadata where the format has it (for example FBX's unit scale; glTF is always meters)

## Revit-specific guidance

Check each point against the Revit 2025–2027 API documentation before relying on it, rather than trusting memory.

- **Geometry:**
  - Build geometry with `TessellatedShapeBuilder` and put it into a `DirectShape` (`CreateElement`, `SetShape`, `SetName`). Store the source file path in `ApplicationId`/`ApplicationDataId`.
  - Target `AnyGeometry` with a `Mesh` fallback, and offer a faster "mesh only" option.
  - `TessellatedFace` takes a material ID per face.
  - Read the builder result's reported issues so you can count rejected faces.
  - Use `DirectShapeLibrary`/`DirectShapeType` for repeated meshes if that measurably helps.
  - Split very large meshes into chunks and measure the effect.
- **Tolerances and units:**
  - Revit's internal unit is decimal feet. After converting units, weld vertices within `Application.VertexTolerance`.
  - Deal with triangles whose edges are shorter than `Application.ShortCurveTolerance` (under a millimetre; read it at runtime). Otherwise Revit silently drops or rejects them, which wrecks small or wrongly scaled models.
  - Count and report what was dropped. If a large fraction would be lost, warn that the units are probably wrong.
- **Threading and transactions:**
  - The Revit API may only be called on Revit's thread, from a valid API context. Run the worker while the dialog is open. Create elements in the command's context, or through `ExternalEvent` if the UI is modeless.
  - Use an `IFailuresPreprocessor` so hundreds of warnings don't each pop up a dialog.
  - Cancelling rolls everything back.
  - Keep the user informed while large imports create elements, if that's feasible.
- **Materials:**
  - Create materials with `Material.Create`.
  - Create the appearance with `AppearanceAssetElement`, duplicated from an existing generic appearance asset. Look first in `Application.GetAssets(AssetType.Appearance)`, then fall back to one already in the document. Handle the case where neither exists.
  - Edit the appearance in an `AppearanceAssetEditScope`. Attach the diffuse texture by calling `AddConnectedAsset("UnifiedBitmapSchema")` on the generic diffuse property and setting the unified bitmap's bitmap path. The path must point to an existing file.
  - Map transparency and bump/normal maps where you can, and document the PBR-to-Revit mapping in a table.
  - Sanitize material names, since Revit rejects characters such as `{ } [ ] | ; < > ?` and `~`, and make them unique.
  - On re-import, reuse identical materials instead of duplicating them.
- **Textures on disk:**
  - Revit stores paths to bitmaps, not the image data. Copy textures to a stable folder: by default `<project folder>\<project name>_textures\`, or a user-configurable folder if the project hasn't been saved yet. Warn that this folder must travel with the project.
  - In the worker, convert formats Revit can't render (TGA, DDS, WebP, KTX2, and similar) to PNG, using permissively licensed decoders such as StbImageSharp or Pfim.
  - Extract embedded textures into the same folder.
- **UV mapping (a known risk; investigate it first):**
  - As far as I know, the Revit API has no way to pass UV coordinates for `DirectShape`/`TessellatedShapeBuilder` geometry. Revit projects textures onto faces itself, so a model's UV-unwrapped textures won't line up. Confirm or refute this from the 2025–2027 API docs before anything else.
  - If it's true, offer these material modes and pick a sensible default for each model. Explain the trade-off in one line in the dialog.
    - **Textured:** a Revit material with the texture. Set the texture's real-world scale from the mesh's measured world units per UV unit, so tiling textures (brick, wood, tile) come out at about the right size.
    - **Baked colors:** sample the texture for each triangle (at the vertex UVs and the centroid, multiplied by the base color). Reduce the colors to a palette whose size the user sets (default 32), and give each face the matching flat-color material. This suits photogrammetry and game assets that use texture atlases.
    - **Flat colors:** the base color, or the texture's average color.
  - If you find a real way to keep UVs, use it and say so.
- **Packaging:**
  - Ship an `.addin` manifest per Revit version, with a unique, stable AddInId GUID and a VendorId.
  - Add a PowerShell install and uninstall script that copies the add-in to `%AppData%\Autodesk\Revit\Addins\<year>\`. Ship the worker alongside the add-in.
  - Never commit Autodesk DLLs. Compile against the reference-assembly NuGet packages (for example `Nice3point.Revit.Api.RevitAPI` and `RevitAPIUI`), with the version matched to each Revit year and runtime assets excluded (`ExcludeAssets=runtime`).
  - Decide whether to use the add-in dependency isolation manifest settings added in Revit 2026/2027. The worker-process design probably makes them unnecessary; record the decision.

## Simplification

Implement quadric error metric edge collapse (Garland–Heckbert) in Core, in managed code, behind an `IMeshSimplifier` interface. Porting an MIT-licensed implementation such as Fast-Quadric-Mesh-Simplification is fine; keep its license notice.

**Requirements:**
- The target can be a ratio or a triangle count. Spread a global target across meshes in proportion to their triangle counts, with a minimum per mesh so small parts don't disappear.
- Preserve open borders and the seams between materials.
- Make baked-color sampling still work after simplifying: either carry UVs through each collapse, or bake colors before simplifying. Choose one and document it.
- The output has no degenerate or flipped triangles.

**Tests:**
- The result is within ±5% of the target where that's achievable.
- The bounding box stays within 1% of the original.
- Borders and material seams are kept.
- A 1M-triangle mesh is reduced to 10% within a few seconds. Record the actual timings.

## Environment and verification

You're in a Linux container, so you can't run Revit or show WPF windows. Work like this:

- If the .NET 10 SDK is missing, install it with the `dotnet-install` script. It can also build the net8 targets. If the network blocks the install, stop and say exactly what's needed.
- Build the Windows/WPF targets on Linux with `EnableWindowsTargeting=true`. The whole solution must build with no new warnings. Treat warnings as errors in Core and the worker.
- Generate test fixtures in code rather than downloading them. Build scenes in Core and export them with Assimp's exporters into every format the build can export (OBJ/MTL with PNG, glTF/GLB, DAE, STL, PLY, 3DS, FBX, 3MF). Include:
  - a textured cube
  - a multi-material object
  - nested transforms
  - a Y-up model
  - a model in centimeters
  - a tiny-scale model that trips `ShortCurveTolerance`
  - a sphere of about 300k triangles that triggers the recommendation
  - a file with a missing texture
  - a corrupted file
- Test the worker end to end against those fixtures. That includes checking that a corrupted file returns an error and doesn't crash the calling process.
- Add a GitHub Actions workflow on `windows-latest`. It builds every target, runs the tests, and uploads a zipped add-in for each Revit version. Confirm it passes after you push.
- Write `docs/MANUAL_TEST_PLAN.md`, a checklist for someone with Revit on Windows. It covers:
  - installing
  - each format
  - the recommendation appearing and being ignorable
  - simplifying
  - textures
  - undo and cancel
  - a corrupted file
  - uninstalling

## How to work

1. **Start with a short research spike.** Check:
   - the UV question
   - the material and appearance API
   - `TessellatedShapeBuilder` limits
   - which Assimp binding to use
   - the reference-assembly packages and target framework for each Revit year

   Record the findings, with links and the decisions they lead to, in `docs/DESIGN.md`.
2. **Build a vertical slice first:** OBJ and glTF → worker → analyze → dialog with the recommendation → DirectShape with flat-color materials. Then add the other formats, textures, baking, simplification and polish.
3. **Commit after each milestone** with a clear message, and push.
4. **Don't stop to ask questions the assumptions above already answer.** If something is genuinely the user's call (for example, a dependency with a non-permissive license), take the safe option, note it, and keep going.

## Done means

- The solution builds on Linux and in Windows CI, and all tests pass.
- The README covers features, supported formats, installation, usage and settings. It explains the limitations plainly, especially how textures are mapped.
- `docs/DESIGN.md`, `docs/MANUAL_TEST_PLAN.md` and `THIRD_PARTY_NOTICES.md` exist.
- There is a final report that:
  - separates **verified here** (and how) from **needs verification in Revit**. Never claim something works in Revit unless it has run in Revit.
  - lists known limitations
  - suggests next steps, such as a 3D preview in the dialog, importing as a loadable family (`.rfa`), batch import, and Revit 2024 support
