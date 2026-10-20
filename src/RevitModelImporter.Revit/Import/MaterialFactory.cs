using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Processing;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using RevitException = Autodesk.Revit.Exceptions.ApplicationException;

namespace RevitModelImporter.Revit.Import;

/// <summary>Creates Revit materials (with Generic appearance assets and textures) from intermediate materials,
/// reusing identical materials from earlier imports. Must run inside an open transaction.</summary>
internal sealed class MaterialFactory
{
    private readonly Document _doc;
    private readonly RevitApplication _app;
    private readonly List<string> _warnings;
    private readonly Dictionary<string, ElementId> _bySignature;
    private Asset? _libraryGeneric;
    private bool _libraryChecked;
    private ElementId? _documentGeneric;
    private bool _documentChecked;
    private bool _warnedNoAppearance;

    public MaterialFactory(Document doc, RevitApplication app, List<string> warnings)
    {
        _doc = doc;
        _app = app;
        _warnings = warnings;
        _bySignature = MaterialSignatureStorage.ReadAll(doc);
    }

    public int Created { get; private set; }

    public int Reused { get; private set; }

    public ElementId GetOrCreate(IntermediateMaterial m)
    {
        if (_bySignature.TryGetValue(m.Key, out var existing) && _doc.GetElement(existing) is Material)
        {
            Reused++;
            return existing;
        }

        var name = UniqueMaterialName(m.Name);
        var id = Material.Create(_doc, name);
        var material = (Material)_doc.GetElement(id);
        material.Color = new Color(m.Color[0], m.Color[1], m.Color[2]);
        material.Transparency = Math.Clamp((int)Math.Round((1 - m.Opacity) * 100), 0, 100);
        material.Smoothness = Math.Clamp((int)Math.Round(m.Glossiness * 100), 0, 100);
        material.Shininess = Math.Clamp((int)Math.Round(m.Glossiness * 128), 0, 128);
        material.UseRenderAppearanceForShading = false;

        var appearance = CreateAppearance(name, m);
        if (appearance is not null)
        {
            material.AppearanceAssetId = appearance;
        }

        MaterialSignatureStorage.Write(material, m.Key);
        _bySignature[m.Key] = id;
        Created++;
        return id;
    }

    private ElementId? CreateAppearance(string materialName, IntermediateMaterial m)
    {
        var assetName = RevitNaming.MakeUnique(materialName, n => AppearanceAssetElement.GetAppearanceAssetElementByName(_doc, n) is not null);
        AppearanceAssetElement? element = null;

        // 1. A Generic asset from Revit's appearance library.
        if (LibraryGenericAsset() is { } libraryAsset)
        {
            try
            {
                element = AppearanceAssetElement.Create(_doc, assetName, libraryAsset);
            }
            catch (RevitException)
            {
                element = null;
            }
        }

        // 2. A Generic appearance already in the document.
        if (element is null && DocumentGenericAppearance() is { } templateId && _doc.GetElement(templateId) is AppearanceAssetElement template)
        {
            try
            {
                element = template.Duplicate(assetName);
            }
            catch (RevitException)
            {
                element = null;
            }
        }

        // 3. Neither exists (e.g. the material library is not installed): colour-only material.
        if (element is null)
        {
            if (!_warnedNoAppearance)
            {
                _warnings.Add("No Generic appearance asset was found in Revit's material library or in the project, so materials only have shaded colors; textures could not be attached.");
                _warnedNoAppearance = true;
            }

            return null;
        }

        try
        {
            Edit(element, m);
        }
        catch (RevitException e)
        {
            _warnings.Add($"Material '{materialName}': the appearance could not be fully set up ({e.Message}).");
        }

        return element.Id;
    }

    private void Edit(AppearanceAssetElement element, IntermediateMaterial m)
    {
        using var scope = new AppearanceAssetEditScope(_doc);
        var editable = scope.Start(element.Id);
        var color = new Color(m.Color[0], m.Color[1], m.Color[2]);

        if (editable.FindByName(Generic.GenericDiffuse) is AssetPropertyDoubleArray4d diffuse)
        {
            Try(() => diffuse.SetValueAsColor(color));
            if (diffuse.NumberOfConnectedProperties > 0)
            {
                diffuse.RemoveConnectedAsset();
            }

            if (m.DiffuseTexture is { } texture)
            {
                AttachBitmap(diffuse, texture, m.TextureSizeX, m.TextureSizeY, m.Name);
            }
        }

        if (editable.FindByName(Generic.GenericTransparency) is AssetPropertyDouble transparency)
        {
            Try(() => transparency.Value = Math.Clamp(1.0 - m.Opacity, 0, 1));
        }

        if (editable.FindByName(Generic.GenericGlossiness) is AssetPropertyDouble glossiness)
        {
            Try(() => glossiness.Value = Math.Clamp(m.Glossiness, 0, 1));
        }

        if (editable.FindByName(Generic.GenericIsMetal) is AssetPropertyBoolean metal)
        {
            Try(() => metal.Value = m.IsMetal);
        }

        if (editable.FindByName(Generic.GenericBumpMap) is AssetPropertyDoubleArray4d bump)
        {
            if (bump.NumberOfConnectedProperties > 0)
            {
                bump.RemoveConnectedAsset();
            }

            if (m.BumpTexture is { } bumpTexture)
            {
                AttachBitmap(bump, bumpTexture, m.TextureSizeX, m.TextureSizeY, m.Name);
                if (editable.FindByName(Generic.GenericBumpAmount) is AssetPropertyDouble amount)
                {
                    Try(() => amount.Value = Math.Clamp(m.BumpAmount, -1000, 1000));
                }
            }
        }

        scope.Commit(false);
    }

    /// <summary>Connects a UnifiedBitmap to <paramref name="property"/> and points it at <paramref name="file"/>.</summary>
    private void AttachBitmap(AssetProperty property, string file, double sizeXFeet, double sizeYFeet, string materialName)
    {
        if (!File.Exists(file))
        {
            _warnings.Add($"Material '{materialName}': texture file '{file}' does not exist, so it was not attached.");
            return;
        }

        var schema = property.IsValidSchemaIdentifier("UnifiedBitmapSchema") ? "UnifiedBitmapSchema" : "UnifiedBitmap";
        property.AddConnectedAsset(schema);
        var bitmap = property.GetSingleConnectedAsset();
        if (bitmap is null)
        {
            return;
        }

        if (bitmap.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) is AssetPropertyString path)
        {
            if (path.IsValidValue(file))
            {
                path.Value = file;
            }
            else
            {
                _warnings.Add($"Material '{materialName}': Revit did not accept the texture path '{file}'.");
            }
        }

        if (bitmap.FindByName(UnifiedBitmap.TextureScaleLock) is AssetPropertyBoolean scaleLock)
        {
            Try(() => scaleLock.Value = false);
        }

        if (sizeXFeet > 0)
        {
            SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleX, sizeXFeet);
        }

        if (sizeYFeet > 0)
        {
            SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleY, sizeYFeet);
        }

        foreach (var repeat in new[] { UnifiedBitmap.TextureURepeat, UnifiedBitmap.TextureVRepeat })
        {
            if (bitmap.FindByName(repeat) is AssetPropertyBoolean r)
            {
                Try(() => r.Value = true);
            }
        }
    }

    private static void SetDistance(Asset asset, string name, double feet)
    {
        if (asset.FindByName(name) is AssetPropertyDistance distance)
        {
            var value = UnitUtils.Convert(feet, UnitTypeId.Feet, distance.GetUnitTypeId());
            Try(() => distance.Value = Math.Max(0.01, value));
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (RevitException)
        {
            // Out-of-range values are skipped; the property keeps the template's value.
        }
    }

    private Asset? LibraryGenericAsset()
    {
        if (_libraryChecked)
        {
            return _libraryGeneric;
        }

        _libraryChecked = true;
        try
        {
            var candidates = _app.GetAssets(AssetType.Appearance)
                .Where(a => a.FindByName(Generic.GenericDiffuse) is not null)
                .ToList();
            _libraryGeneric = candidates.FirstOrDefault(a => IsPlainGeneric(a) && (a.Name.Contains("Generic", StringComparison.OrdinalIgnoreCase) || a.Title.Contains("Generic", StringComparison.OrdinalIgnoreCase)))
                ?? candidates.FirstOrDefault(IsPlainGeneric)
                ?? candidates.FirstOrDefault();
        }
        catch (RevitException)
        {
            _libraryGeneric = null;
        }

        return _libraryGeneric;
    }

    private ElementId? DocumentGenericAppearance()
    {
        if (_documentChecked)
        {
            return _documentGeneric;
        }

        _documentChecked = true;
        AppearanceAssetElement? withTexture = null;
        foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>())
        {
            Asset? asset;
            try
            {
                asset = element.GetRenderingAsset();
            }
            catch (RevitException)
            {
                continue;
            }

            if (asset is null || asset.Size == 0 || asset.FindByName(Generic.GenericDiffuse) is null)
            {
                continue;
            }

            if (IsPlainGeneric(asset))
            {
                _documentGeneric = element.Id;
                return _documentGeneric;
            }

            withTexture ??= element;
        }

        _documentGeneric = withTexture?.Id;
        return _documentGeneric;
    }

    /// <summary>True when the diffuse slot has no bitmap connected (a clean template).</summary>
    private static bool IsPlainGeneric(Asset asset) =>
        asset.FindByName(Generic.GenericDiffuse) is AssetProperty diffuse && diffuse.NumberOfConnectedProperties == 0;

    private string UniqueMaterialName(string name)
    {
        var clean = RevitNaming.Sanitize(name, "Imported material");
        if (!NamingUtils.IsValidName(clean))
        {
            clean = new string(clean.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')').ToArray());
            if (clean.Length == 0)
            {
                clean = "Imported material";
            }
        }

        return RevitNaming.MakeUnique(clean, n => !Material.IsNameUnique(_doc, n));
    }
}
