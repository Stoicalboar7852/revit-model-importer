using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitModelImporter.Revit.Import;

/// <summary>Stores the importer's material signature on each material it creates (Extensible Storage), so a
/// re-import finds and reuses identical materials instead of creating "Wood (2)", "Wood (3)", …</summary>
internal static class MaterialSignatureStorage
{
    private static readonly Guid SchemaGuid = new("a552ba8a-2dfe-40af-b19e-d215dc521483");
    private const string FieldName = "Signature";

    public static Schema GetOrCreateSchema()
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema is not null)
        {
            return schema;
        }

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName("RevitModelImporterMaterial");
        builder.SetDocumentation("Signature of a material created by Revit Model Importer, used to reuse identical materials.");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(FieldName, typeof(string));
        return builder.Finish();
    }

    /// <summary>Signature → material id for every material in the document that carries one.</summary>
    public static Dictionary<string, ElementId> ReadAll(Document doc)
    {
        var result = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        var schema = Schema.Lookup(SchemaGuid);
        if (schema is null)
        {
            return result;
        }

        foreach (var material in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
        {
            var entity = material.GetEntity(schema);
            if (entity is null || !entity.IsValid())
            {
                continue;
            }

            var signature = entity.Get<string>(FieldName);
            if (!string.IsNullOrEmpty(signature))
            {
                result.TryAdd(signature, material.Id);
            }
        }

        return result;
    }

    public static void Write(Element material, string signature)
    {
        var schema = GetOrCreateSchema();
        var entity = new Entity(schema);
        entity.Set(FieldName, signature);
        material.SetEntity(entity);
    }
}
