# Revit Model Importer – design notes

This document records the research spike, the decisions that came out of it, and the places where the
implementation deviates from the original brief (`docs/build-prompt.md`). Every Revit API statement below was
checked against the XML API documentation that ships inside the reference-assembly packages
`Nice3point.Revit.Api.RevitAPI` 2025.0.2, 2026.0.4 and 2027.0.0/2027.0.20, which contain Autodesk's own API
reference text (the `RevitAPI.xml` next to `RevitAPI.dll`). The online references (revitapidocs.com, rvtdocs.com,
help.autodesk.com, blog.autodesk.io) could not be reached from the build container (network policy), so they were
only consulted through search-engine snippets; those statements are marked *(secondary)*.

## 1. Research spike

### 1.1 Can UVs be passed to `DirectShape` / `TessellatedShapeBuilder` geometry?  **No (confirmed).**

* `TessellatedFace` has exactly two constructors in 2025, 2026 and 2027:
  `TessellatedFace(IList<XYZ> outerLoop, ElementId materialId)` and
  `TessellatedFace(IList<IList<XYZ>> loops, ElementId materialId)`. Its other members are `GetBoundaryLoops()`,
  `MaterialId` and `IsValidObject`. There is no UV, texture-coordinate or normal parameter anywhere on
  `TessellatedFace`, `TessellatedShapeBuilder`, `TessellatedShapeBuilderResult`, `DirectShape` or `DirectShapeType`.
* A search of all ~31–33k documented members of RevitAPI 2025/2026/2027 for `Texture`, `UV`, `TexCoord` found
  only unrelated members (`UV` points on faces, `DWFExportOptions.ExportTexture`, `ExternalResourceTypes.MaterialTexture`).
* Revit therefore projects the material's texture onto each face itself (planar per-face mapping driven by the
  texture's real-world size). A model's UV unwrap cannot be reproduced. *(secondary: forum threads from Rhino.Inside
  and Speckle developers report the same — "there isn't any way to translate Rhino UV mapping to Revit", Speckle
  "does not convert UV/texture coordinates".)*
* **Lead that needs verification in Revit, not implemented:** RevitAPI has had `Document.Import(string, OBJImportOptions, View)`
  and `Document.Link(...)` since 2022.1. One secondary source (an EvolveLAB Helix forum post) claims UV mapping
  persists through an OBJ route. Whether Revit's native OBJ import keeps UV-mapped textures could not be checked
  here. It would produce an `ImportInstance` (no category choice, no per-object elements), so it does not fit the
  DirectShape pipeline. It is listed as a next step in the README.

**Consequence:** the three material modes from the brief are implemented (see §4.4), with a per-model default.

### 1.2 `TessellatedShapeBuilder` facts that shape the design

From the 2027 XML docs (identical wording in 2025 and 2026):

* Supported target/fallback combinations are only `Solid/Abort`, `AnyGeometry/Mesh` and `Mesh/Salvage`.
  We use `AnyGeometry/Mesh` (default) and `Mesh/Salvage` ("mesh only", faster).
* **`TessellatedFace` material:** "Material of the face to be used when the result is a Solid or a Sheet. **If the result
  is a Mesh, a material will be assigned to the entire Mesh.**" And `GetGeometricalObjects`: if several face sets are
  built with `AnyGeometry/Mesh`, the result can be "a two-element array with both geometry as the 1st element and
  mesh as the 2nd". So all face sets that fall back to a mesh are merged into **one** `Mesh` with **one** material.
  ⇒ The add-in runs a separate `Build()` per (object, material, chunk) and concatenates the resulting geometry
  objects into the element's shape. This is the only way to keep per-material colours when Revit falls back to a
  mesh, which it does for nearly every real-world model.
* `Build()` "throws ... if an attempt is made to create unacceptable geometry with too many facets". No number is
  documented. ⇒ Large material groups are split into chunks (default 20,000 triangles per build, configurable in
  `settings.json` as `MaxTrianglesPerBuild`). The import summary reports build time so the effect can be measured
  in Revit; it could not be measured here.
* `GetBuildResult()` returns a `TessellatedShapeBuilderResult` with `Outcome`, `HasInvalidData`,
  `GetNumberOfFaceSets()` and `GetIssuesForFaceSet(i)` → `TessellatedBuildIssue` (`NumberEncountered`,
  `MakesDataUnusable()`, `GetIssueDescription()`). There is **no per-face rejected flag**. Rejected triangles are
  therefore counted as: all input triangles when the outcome is `Nothing`; otherwise the sum of
  `NumberEncountered` over issues where `MakesDataUnusable()` is true, and for mesh outcomes additionally
  `inputTriangles − Mesh.NumTriangles` when that is larger. The summary labels this number as an estimate.
* `TessellatedFace` loops "without duplication of the ends"; the contents of the vertex list "will be changed while
  constructing the face" ⇒ a fresh `List<XYZ>` per face.
* `DirectShape.CreateElement(Document, ElementId categoryId)`, `SetShape(IList<GeometryObject>)`, `SetName(string)`,
  `ApplicationId` and `ApplicationDataId` (strings, settable) all exist in 2025–2027. `ApplicationId` = add-in name +
  AddInId GUID, `ApplicationDataId` = source file path + node path.
* `DirectShape.IsValidCategoryId(ElementId, Document)` exists in all three years; the category list is filtered with it.
* `DirectShapeLibrary` / `DirectShapeType` / `DirectShape.CreateElementInstance` exist. Instancing was **not**
  implemented: the brief asks for it only "if that measurably helps", and that can only be measured inside Revit.
  The intermediate file keeps each object's mesh source so it can be added later (see README → next steps).

### 1.3 Tolerances

* `Application.VertexTolerance`: "Two points within this distance are considered coincident."
* `Application.ShortCurveTolerance`: "The enforced minimum length for any curve created by Revit."
* Both are read at runtime by the add-in and passed to the worker (`--vertex-tolerance`, `--min-edge`), which welds
  vertices within the vertex tolerance and drops triangles that have an edge shorter than the short-curve tolerance,
  after unit conversion. Dropped counts are reported; if more than 5 % of triangles would be dropped, the dialog and
  the summary warn that the source units are probably wrong. The analysis step returns a histogram of
  shortest-edge lengths so the dialog can estimate the loss for whatever unit the user picks **before** importing.

### 1.4 Materials and appearance assets

Verified members (2025–2027):

* `Material.Create(Document, string)` → `ElementId`. Names may not contain `{ } [ ] | ; < > ? ` ~`
  (the docs list the backtick too) — `NamingUtils.IsValidName(string)` checks this at runtime; we sanitise first and
  then also call `IsValidName`. `Material.IsNameUnique(Document, string)` is used to make names unique.
* `Material.Color`, `Material.Transparency` (0–100), `Material.Shininess`, `Material.Smoothness`,
  `Material.UseRenderAppearanceForShading`, `Material.AppearanceAssetId`.
* `AppearanceAssetElement.Duplicate(string)`, `AppearanceAssetElement.Create(Document, string, Asset)`,
  `GetRenderingAsset()` (may be empty for library assets: "use Application.GetAssets(AssetType.Appearance) ... and
  retrieve the asset by its name"), `GetAppearanceAssetElementByName`.
* `Application.GetAssets(AssetType)` (since 2018.1).
* `AppearanceAssetEditScope(Document)`, `Start(ElementId)` → editable `Asset`, `Commit(bool updateViews)`, `Cancel()`.
  A transaction must be open when committing; one scope edits one asset at a time.
* `AssetProperty.AddConnectedAsset(string schemaId)` ("For "UnifiedBitmap", it contains an empty property
  unifiedbitmap_Bitmap"), `RemoveConnectedAsset()`, `GetSingleConnectedAsset()`, `IsValidSchemaIdentifier(string)`.
* Schema property names come from the static classes `Autodesk.Revit.DB.Visual.Generic`
  (`GenericDiffuse`, `GenericTransparency`, `GenericBumpMap`, `GenericBumpAmount`, `GenericIsMetal`,
  `GenericGlossiness`, `GenericCutoutOpacity`) and `UnifiedBitmap` (`UnifiedbitmapBitmap`,
  `TextureRealWorldScaleX/Y` (`AssetPropertyDistance`, min 0.01), `TextureURepeat/VRepeat`, `TextureScaleLock`).
* Asset creation strategy (as the brief asks): (1) find a "Generic" appearance asset in
  `Application.GetAssets(AssetType.Appearance)` and `AppearanceAssetElement.Create` from it; (2) otherwise duplicate
  any `AppearanceAssetElement` already in the document whose rendering asset uses the Generic schema; (3) otherwise
  create the material without an appearance asset (shaded colour + transparency only) and report a warning that
  textures could not be attached. The connected bitmap is attached with
  `AddConnectedAsset("UnifiedBitmapSchema")`; the schema id is checked with `IsValidSchemaIdentifier` first and
  `"UnifiedBitmap"` is tried as a fallback.
* `AssetPropertyDistance` values are converted from feet into the property's own unit
  (`GetUnitTypeId()` + `UnitUtils.Convert`).

PBR → Revit mapping actually implemented:

| Source (glTF PBR / classic)              | Revit                                                          | Notes |
|------------------------------------------|----------------------------------------------------------------|-------|
| baseColorFactor / Kd (diffuse colour)    | `Material.Color`, `generic_diffuse` colour                    | linear → sRGB for PBR formats |
| baseColorTexture / map_Kd                | `generic_diffuse` → UnifiedBitmap `unifiedbitmap_Bitmap`      | Textured mode only; real-world size from UV density |
| alpha (baseColor.a) / d / Tr / opacity   | `Material.Transparency`, `generic_transparency`               | 1 − opacity |
| normalTexture / map_bump / bump / height | `generic_bump_map` → UnifiedBitmap, `generic_bump_amount`     | Revit treats it as a bump (height) map; normal maps render approximately |
| metallicFactor ≥ 0.5                     | `generic_is_metal` = true                                     | |
| roughnessFactor                          | `generic_glossiness` = 1 − roughness, `Material.Smoothness`   | |
| emissive, occlusion, metallic/roughness textures | not mapped                                             | Revit's Generic schema has no slot that behaves equivalently |

### 1.5 Assimp binding

Compared on 2026-10-20 using the NuGet catalog and the package contents themselves:

| Binding | Latest | Last release | Assimp bundled | Licence | Native RIDs in package | API |
|---|---|---|---|---|---|---|
| **AssimpNetter** (Saalvage) | 6.0.5 | 2026-06-23 | 6.0.x (reported by `aiGetVersion*` at runtime: 6.0) | MIT (wrapper) + BSD-3 (Assimp) | win-x64, win-x86, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 | high-level managed scene (`Scene`, `Mesh`, `Material`, `EmbeddedTexture`, `Metadata`) |
| Silk.NET.Assimp | 2.23.0 | 2026-01-23 | 6.0.2 via Ultz.Native.Assimp | MIT + BSD-3 | win-x64/x86/arm64, linux-x64/arm/arm64, osx | raw unsafe pointers only; Silk.NET 2.x is in maintenance while 3.0 is developed |
| SharpAssimp (JeremyAnsel) | 6.0.12 | 2026-05-03 | 6.0.x | MIT + BSD-3 | win-x64, win-x86, linux-x64, osx-arm64 | AssimpNet-style high-level API |

**Decision: AssimpNetter 6.0.5.** It is the actively maintained continuation of AssimpNet, bundles a current Assimp
6.0 build for both required RIDs (win-x64 and linux-x64, verified by loading `libassimp.so` on Ubuntu 24.04 in this
container), and its managed scene graph saves a lot of unsafe marshalling code compared with Silk.NET.

Import extensions reported by this build (`AssimpContext.GetSupportedImportFormats()`), used to build the file
filter at runtime: `.3d .3ds .3mf .ac .ac3d .acc .amf .ase .ask .assbin .b3d .blend .bsp .bvh .cob .csm .dae .dxf
.enff .fbx .glb .gltf .hmp .ifc .ifczip .iqm .irr .irrmesh .lwo .lws .lxo .md2 .md3 .md5anim .md5camera .md5mesh .mdc
.mdl .mesh .mesh.xml .mot .ms3d .ndo .nff .obj .off .ogex .pk3 .ply .pmx .prj .q3o .q3s .raw .scn .sib .smd .step .stl
.stp .ter .uc .vrm .vta .x .x3d .x3db .xgl .xml .zae .zgl`.

Excluded from the filter: formats Revit imports natively (`.skp .3dm .ifc .ifczip .dwg .dxf .sat .step .stp`), and
extensions that never carry static geometry or are ambiguous (`.bvh .csm .md5anim .md5camera .mot .xml`).

Export formats in this build (used by the test-fixture generator): collada, x, stp, obj, objnomtl, stl, stlb, ply,
plyb, 3ds, gltf2, glb2, assbin, assxml, x3d, fbx, fbxa, 3mf, pbrt, assjson.

### 1.6 Reference assemblies and target frameworks

| Revit | Runtime | Add-in TFM | Package version (RevitAPI + RevitAPIUI) |
|---|---|---|---|
| 2025 | .NET 8 | `net8.0-windows` | `Nice3point.Revit.Api.*` 2025.0.2 (package TFM `net8.0`) |
| 2026 | .NET 8 | `net8.0-windows` | 2026.0.4 |
| 2027 | .NET 10 | `net10.0-windows` | 2027.0.20 (package TFM `net10.0-windows7.0`) |

The packages contain only `ref/` assemblies plus the XML docs; they are referenced with `ExcludeAssets="runtime"`
and `PrivateAssets="all"` so nothing from Autodesk is ever copied or committed. The earliest release of each year
(x.0) is used so the add-in cannot accidentally call an API that only exists in a later point update.

Because 2025 and 2026 share a TFM, the add-in project selects the Revit year with an MSBuild property:
`-p:RevitVersion=2025|2026|2027`. With no property, `net8.0-windows` builds for 2026 and `net10.0-windows` for 2027.
CI builds all three explicitly. Compilation symbols `REVIT2025`, `REVIT2026`, `REVIT2027` and
`REVIT2026_OR_GREATER` are defined.

API differences that matter for this code: `ElementId.IntegerValue` and `ElementId(int)` are gone in 2027
(present but obsolete in 2025/2026), so only `ElementId(long)` / `ElementId.Value` are used.

### 1.7 Add-in dependency isolation (Revit 2026/2027)

Revit 2026 added the manifest setting `<ManifestSettings><UseRevitContext>False</UseRevitContext>
<ContextName>…</ContextName></ManifestSettings>` to load an add-in into its own `AssemblyLoadContext`; 2027 adds
`PublicAssemblies`, `Dependencies` and `UseAllContextsForDependencyResolution` *(secondary: Autodesk developer blog
and rvtdocs "What's new" pages)*.

**Decision: not used.** The add-in assembly loaded into Revit references only our own `RevitModelImporter.Core`
(no NuGet dependencies) plus the BCL, WPF and the Revit API. Every third-party and native library (AssimpNetter,
Assimp, StbImageSharp, Pfim) lives in the worker process. There is nothing left that could conflict with Revit or
other add-ins, so isolation would only add risk (a developer reported on the Autodesk forum that isolation in
2026.1 did not separate two add-ins as documented, and WPF resource loading from a non-default load context is a
known source of problems). The manifests stay identical in shape across 2025–2027.

## 2. Architecture

```
Revit.exe ──────────────────────────────────────────────┐        worker process (per run)
  RevitModelImporter.Revit.dll (net8/net10, WPF)        │        RevitModelImporter.Worker.exe (net8, RollForward=LatestMajor)
    ├─ ribbon, command, dialogs (views)                 │ spawn    ├─ AssimpNetter + native assimp
    ├─ WorkerClient (Core) ─────────────────────────────┼────────► ├─ StbImageSharp / Pfim (texture decode)
    │      stdout: JSON lines (progress/result/error)   │◄──────── ├─ Core: flatten, units/axes, simplify, bake, cleanup
    ├─ IntermediateReader (Core) ◄── model.rmi ─────────┼──────────┤  writes model.rmi + textures folder
    └─ DirectShape + Material creation (Revit API)      │          └─ exit code
  RevitModelImporter.Core.dll (net8, no deps)           │
```

* **Core** (`net8.0`, no NuGet dependencies, no native code, warnings as errors): scene model, units/axes,
  flattening, cleanup, simplifier, baking/palette, texture resolution, PNG writer, statistics and the recommendation
  logic, intermediate file format, worker protocol + `WorkerClient`, settings, and the **view models**.
* **Worker** (`net8.0` console, warnings as errors): `formats`, `analyze`, `convert`.
* **Revit** (`net8.0-windows;net10.0-windows`, WPF): ribbon, command, views, dialog parenting, worker discovery,
  Revit materials and geometry. References only Core.
* **Tests** (`net8.0`, xUnit, Linux and Windows): Core unit tests, view-model rule tests, worker end-to-end tests
  against generated fixtures.

### Deviation: view models live in Core
The brief asks for the recommendation rules to live in the dialog's view model **and** for all tests to run on Linux.
A `net8.0` test project cannot reference a `net8.0-windows` WPF project, so the view models (plain
`INotifyPropertyChanged` + `ICommand`, both cross-platform BCL types) live in `RevitModelImporter.Core.Presentation`
and the XAML views in the Revit project bind to them. Revit-specific data (categories, project base point) is passed
in as plain values. Core still has no Revit and no native code.

### Worker runtime: framework-dependent + `RollForward=LatestMajor`
Chosen over a self-contained publish because:
* Revit 2025/2026 require the .NET 8 Desktop Runtime and Revit 2027 the .NET 10 one; the Revit installer installs
  them machine-wide, and both include `Microsoft.NETCore.App`. One `net8.0` worker build runs on either thanks to
  `LatestMajor`, so the same worker ships for all three years.
* The package stays ~10 MB instead of ~80 MB, and the runtime is serviced by Windows Update instead of being frozen
  inside the add-in.
* To be independent of how the runtime was installed, the add-in starts the worker with `DOTNET_ROOT` set to the
  root of the runtime **Revit itself is running on** (derived from `RuntimeEnvironment.GetRuntimeDirectory()`), so
  if Revit runs, the worker can run.

### Intermediate file (`.rmi`), version 1
```
offset 0   "RMI1"                       4-byte magic
       4   uint32 container version (1)
       8   uint64 header length N
      16   N bytes UTF-8 JSON header (IntermediateHeader)
           zero padding to an 8-byte boundary
           binary blob: buffers referenced from the header by (offset, length) relative to the blob start
```
Positions are `float64` xyz in **Revit internal units (decimal feet), Z up**, already flattened to world space,
unit-converted, simplified, welded and cleaned. Indices are `int32` triangles, and each triangle has an `int32`
material index into the header's material list. The header carries materials (colour, opacity, texture file paths in
the destination texture folder, real-world texture size in feet), objects, statistics and warnings. The reader
rejects other magic values and newer major versions with a clear message.

### Process boundary and failure handling
* Exit codes (`WorkerExitCode`): 0 OK, 2 usage, 3 file not found, 4 unsupported format, 5 parse failure / corrupt file,
  6 output write failure, 7 out of memory, 10 unexpected error. Any other code (e.g. 0xC0000005 from a native crash)
  is reported as "the worker crashed" — Revit and the open document are unaffected.
* stdout carries one JSON object per line: `progress`, `warning`, `result`, `error`. stderr is captured for diagnostics.
* Cancel kills the whole worker process tree immediately.

## 3. Threading and transactions in Revit
* The command runs on Revit's API thread. The worker is started from that thread and awaited while a modal WPF
  progress dialog (owned by Revit's main window via `WindowInteropHelper.Owner = UIApplication.MainWindowHandle`)
  pumps messages; Cancel kills the worker.
* Element creation happens back in `IExternalCommand.Execute` (valid API context) inside one `TransactionGroup`,
  with one `Transaction` for materials and one per batch of elements, then `Assimilate()` → one undo step.
* While elements are created, a progress window runs on its own STA thread (its own dispatcher), because the Revit
  thread is busy. Its Cancel button sets a flag that is checked between elements; on cancel the group is
  `RollBack()`-ed so nothing is left behind.
* **Deviation from "parent all dialogs to Revit's main window":** this one progress window is *not* owned by Revit's
  window. A cross-thread owner/owned relationship makes Windows attach the two threads' input queues, so the
  progress window (and its Cancel button) would freeze exactly while Revit's thread is busy creating elements.
  It is centred on Revit's window and kept topmost instead. Every other window (file dialog, analysis/conversion
  progress, Import dialog, summary, settings, error dialogs) is owned by `UIApplication.MainWindowHandle`.
* Every transaction gets an `IFailuresPreprocessor` that deletes warnings (collecting their text for the summary) and
  resolves errors where Revit offers a resolution; otherwise it rolls the transaction back.

## 4. Processing decisions

### 4.1 Face count
"Faces" are triangles after Assimp's `Triangulate` step: a quad counts as 2, an n-gon as n−2. The dialog shows this
number and the source polygon count when they differ.

### 4.2 Up axis and units defaults
* Up axis: glTF, OBJ, FBX, DAE (Assimp normalises Collada `up_axis` to Y-up), X, 3D, AC… → Y-up;
  STL, PLY, 3MF, 3DS, OFF → Z-up. FBX's own `UpAxis` metadata is honoured when present.
* Units auto-detected: glTF/GLB (always metres), FBX (`UnitScaleFactor` metadata, centimetres per unit),
  3MF (the `unit` attribute of the model part, read by the worker from the zip; millimetres by default per the 3MF
  spec), DAE (Assimp applies `<unit meter>` to the root node, so the scene arrives in metres). Other formats → the
  user picks (default: last choice for that format, else millimetres for STL/3MF/PLY and metres otherwise).

### 4.3 Simplification
* `IMeshSimplifier` + `QuadricSimplifier`: Garland–Heckbert quadric error metric edge collapse following the
  structure of Sven Forstmann's MIT-licensed *Fast-Quadric-Mesh-Simplification* (iterative threshold schedule,
  per-triangle error caching, deleted-triangle flags), rewritten in C# with these changes:
  * **Borders and material seams are preserved**: an edge used by one triangle (open border), by more than two
    (non-manifold) or by triangles with different materials (seam) marks its vertices as constrained. Constrained
    vertices never move. An interior vertex may collapse onto a constrained one. Two constrained vertices may only
    merge along a constrained edge, onto one of their original positions, and only while the border stays straight:
    each constrained edge adds a plane through the edge, perpendicular to its face, with weight 1000, and such
    collapses are refused above an error equivalent to 1e-4 of the model diagonal, however far the threshold
    schedule has climbed. Every border vertex of the result is an original border vertex and every border edge lies
    on the original outline (`OpenGrid_BorderIsPreserved`, `MaterialSeam_IsPreserved`).
  * **Flip and degeneracy guard**: a collapse is rejected if any surviving triangle's normal turns by more than ~78°
    or its area collapses; a final pass removes zero-area triangles.
  * **UVs are carried through every collapse** (per-corner/wedge UVs, re-interpolated barycentrically at the new
    position), so **baked colours are computed after simplification** from the simplified triangles. Baking first
    would turn every colour boundary into a material seam and block simplification of textured scans.
* A global target (ratio or triangle count) is distributed across meshes in proportion to their triangle counts
  with a per-mesh minimum (default 12 triangles, never more than the mesh has), so small parts survive.
* Measured timings (`OneMillionTriangles_ReducedToTenPercent_InAFewSeconds`, 998,000 → 99,800 triangles, closed UV
  sphere, single thread): Release build in this 4-vCPU container 2.0–2.1 s warm, 3.4 s including JIT; Debug build
  5.2–5.5 s; about 1 s on the GitHub `ubuntu-latest` runner. End to end through the worker, the 299,040-triangle PLY
  sphere simplified to the 45,000-face recommendation in about 1–3 s including loading. Objects are simplified in
  parallel (one thread per object), so multi-object models scale with cores.

### 4.4 Material modes
| Mode | What it does | Good for | Default when |
|---|---|---|---|
| Textured | Revit material + diffuse bitmap; texture real-world size = median world-units-per-UV-unit measured on the mesh | tiling textures (brick, wood, tiles) | textured model whose UVs mostly tile (>10 % of UV coordinates outside 0–1) |
| Baked colors | per-triangle colour sampled at the 3 corner UVs + centroid × base colour, k-means palette (default 32) in linear RGB, one flat material per palette entry | photogrammetry, game assets with atlases | textured model with UVs inside 0–1 |
| Flat colors | base colour, or the average texture colour × base colour | untextured or CAD-like models | no textures |

### 4.5 Textures on disk
* Destination: `<project folder>\<project name>_textures\` when the document is saved, otherwise the user-configurable
  fallback folder (`%LocalAppData%\RevitModelImporter\textures` by default). The summary warns that this folder must
  travel with the project.
* File names are `<sanitised original name>_<first 12 hex of SHA-256 of the content>.<ext>`, so re-imports reuse the
  same file and the material de-duplication can compare paths.
* Formats Revit renders (PNG, JPG/JPEG, BMP, TIF/TIFF) are copied; everything else that can be decoded (TGA, DDS,
  PSD, GIF, HDR, PIC, PNM) is converted to PNG; embedded textures (GLB, FBX) are extracted. WebP and KTX2 have no
  permissively licensed managed decoder available; such textures are reported as unsupported and the material falls
  back to its base colour.
* External texture resolution order: path as written (absolute, or relative to the model file) → same file name in
  the model folder → sibling `textures/`, `Textures/`, `texture/`, `tex/`, `maps/`, `images/` folders → case-insensitive
  match of each path segment → user-supplied folders ("Locate textures folder…").

### 4.6 Material de-duplication on re-import
Each material created by the importer gets an Extensible Storage entity (schema
`RevitModelImporter.MaterialSignature`) holding a signature hash of mode, colour, opacity, texture file name and
texture size. Before creating a material the add-in looks up an existing material with the same signature and
reuses it.

## 5. Assimp 6.0 issues found while testing

* **FindDegenerates area check drops valid small triangles.** Its area epsilon is absolute, so a 25 mm sphere
  lost ~10 % of its faces and a dense unit sphere lost the faces near its poles. The worker disables the area check
  (`AI_CONFIG_PP_FD_CHECKAREA = 0`) and leaves zero-area removal to `MeshCleanup`, which works after unit
  conversion with Revit's tolerances.
* **Binary PLY exported by Assimp with UVs is malformed** (the header declares RGBA colours, the data holds UVs)
  and **reading it back segfaults Assimp** on Linux. Kept as `crash_assimp_malformed.ply` to prove that a native
  crash only ends the worker process (the add-in reports it; Revit is unaffected). Well-formed binary PLY files,
  with or without colours, load fine.
* **ASCII FBX export writes invalid base64** (`====` padding) for embedded data whose length is a multiple of 3, and
  the reader then drops the texture. Only Assimp-written ASCII FBX files are affected; the test fixture pads its PNG.
  This was found because CI's .NET (different zlib) produced a PNG of such a length.
* **3DS export converts Y-up to Z-up** and the importer does not convert back, so a round trip moves the model;
  3DS defaults to Z-up in the dialog.

## 6. Things that could not be verified here
See the README's "Verification status" section; in short, nothing that needs Revit (ribbon, dialogs on screen,
DirectShape creation, appearance assets, undo, rejected-face counts, timings in Revit) has been run in Revit.
