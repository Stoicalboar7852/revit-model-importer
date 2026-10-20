namespace RevitModelImporter.Core.Simplification;

/// <summary>Splits a model-wide triangle target across meshes in proportion to their triangle counts,
/// with a per-mesh minimum so small parts (bolts, handles) do not disappear.</summary>
public static class TargetAllocator
{
    public const int DefaultMinimumPerMesh = 12;

    /// <returns>Target triangle count per mesh (never more than the mesh has).</returns>
    public static int[] Allocate(IReadOnlyList<int> triangleCounts, long totalTarget, int minimumPerMesh = DefaultMinimumPerMesh)
    {
        var n = triangleCounts.Count;
        var result = new int[n];
        long total = 0;
        foreach (var c in triangleCounts)
        {
            total += c;
        }

        if (n == 0 || total == 0)
        {
            return result;
        }

        if (totalTarget >= total)
        {
            for (var i = 0; i < n; i++)
            {
                result[i] = triangleCounts[i];
            }

            return result;
        }

        // Meshes at or below their floor keep everything; the rest share what is left proportionally.
        // Repeat because giving a mesh its floor shrinks the budget for the others.
        var fixedMesh = new bool[n];
        while (true)
        {
            long fixedSum = 0;
            long flexibleTotal = 0;
            for (var i = 0; i < n; i++)
            {
                if (fixedMesh[i])
                {
                    fixedSum += result[i];
                }
                else
                {
                    flexibleTotal += triangleCounts[i];
                }
            }

            var budget = Math.Max(0, totalTarget - fixedSum);
            var ratio = flexibleTotal > 0 ? (double)budget / flexibleTotal : 0;
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (fixedMesh[i])
                {
                    continue;
                }

                var floor = Math.Min(triangleCounts[i], minimumPerMesh);
                var share = (int)Math.Round(triangleCounts[i] * ratio);
                if (share <= floor)
                {
                    result[i] = floor;
                    fixedMesh[i] = true;
                    changed = true;
                }
                else
                {
                    result[i] = Math.Min(share, triangleCounts[i]);
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Estimated triangle count after simplification, as shown live in the dialog.</summary>
    public static long Estimate(IReadOnlyList<int> triangleCounts, long totalTarget, int minimumPerMesh = DefaultMinimumPerMesh)
    {
        long sum = 0;
        foreach (var c in Allocate(triangleCounts, totalTarget, minimumPerMesh))
        {
            sum += c;
        }

        return sum;
    }
}
