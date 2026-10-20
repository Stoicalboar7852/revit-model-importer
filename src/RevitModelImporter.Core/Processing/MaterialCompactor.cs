namespace RevitModelImporter.Core.Processing;

public static class MaterialCompactor
{
    /// <summary>Removes materials no triangle uses and renumbers triangle materials.</summary>
    /// <returns>For each kept material, its old index (in new order).</returns>
    public static List<int> Compact(IReadOnlyList<ObjectMesh> meshes, int materialCount)
    {
        var used = new bool[materialCount];
        foreach (var m in meshes)
        {
            foreach (var t in m.TriangleMaterials)
            {
                if (t >= 0 && t < materialCount)
                {
                    used[t] = true;
                }
            }
        }

        var remap = new int[materialCount];
        var kept = new List<int>();
        for (var i = 0; i < materialCount; i++)
        {
            remap[i] = used[i] ? kept.Count : -1;
            if (used[i])
            {
                kept.Add(i);
            }
        }

        foreach (var m in meshes)
        {
            for (var t = 0; t < m.TriangleMaterials.Length; t++)
            {
                var old = m.TriangleMaterials[t];
                m.TriangleMaterials[t] = old >= 0 && old < materialCount ? remap[old] : 0;
            }
        }

        return kept;
    }
}
