using RevitModelImporter.Core.Geometry;

namespace RevitModelImporter.Core.Processing;

/// <summary>One importable object: all meshes of one scene-node instance, flattened into world space,
/// with a material per triangle and (optionally) UVs per triangle corner.</summary>
public sealed class ObjectMesh
{
    public string Name { get; set; } = "";

    /// <summary>Slash-separated path of node names from the root, used as ApplicationDataId in Revit.</summary>
    public string NodePath { get; set; } = "";

    /// <summary>xyz triples.</summary>
    public double[] Positions { get; set; } = [];

    /// <summary>3 vertex indices per triangle.</summary>
    public int[] Indices { get; set; } = [];

    /// <summary>Material index per triangle (index into the scene's material list).</summary>
    public int[] TriangleMaterials { get; set; } = [];

    /// <summary>Per-corner UVs: 6 floats per triangle (u0, v0, u1, v1, u2, v2), or null when the mesh has no UVs.</summary>
    public float[]? CornerUVs { get; set; }

    public int TriangleCount => Indices.Length / 3;

    public int VertexCount => Positions.Length / 3;

    public Vec3 GetVertex(int i) => new(Positions[3 * i], Positions[3 * i + 1], Positions[3 * i + 2]);

    public BoundingBox ComputeBounds() => BoundingBox.FromPositions(Positions);

    public void Transform(Mat4 m)
    {
        for (var i = 0; i < Positions.Length; i += 3)
        {
            var p = m.TransformPoint(new Vec3(Positions[i], Positions[i + 1], Positions[i + 2]));
            Positions[i] = p.X;
            Positions[i + 1] = p.Y;
            Positions[i + 2] = p.Z;
        }

        if (m.Determinant3x3 < 0)
        {
            FlipWinding();
        }
    }

    public void FlipWinding()
    {
        for (var t = 0; t < TriangleCount; t++)
        {
            (Indices[3 * t + 1], Indices[3 * t + 2]) = (Indices[3 * t + 2], Indices[3 * t + 1]);
            if (CornerUVs is not null)
            {
                var o = 6 * t;
                (CornerUVs[o + 2], CornerUVs[o + 4]) = (CornerUVs[o + 4], CornerUVs[o + 2]);
                (CornerUVs[o + 3], CornerUVs[o + 5]) = (CornerUVs[o + 5], CornerUVs[o + 3]);
            }
        }
    }

    /// <summary>Concatenates several object meshes into one (the "single merged element" grouping).</summary>
    public static ObjectMesh Merge(string name, IReadOnlyList<ObjectMesh> parts)
    {
        var positions = new List<double>();
        var indices = new List<int>();
        var mats = new List<int>();
        var anyUv = parts.Any(p => p.CornerUVs is not null);
        var uvs = anyUv ? new List<float>() : null;
        foreach (var p in parts)
        {
            var baseIndex = positions.Count / 3;
            positions.AddRange(p.Positions);
            foreach (var i in p.Indices)
            {
                indices.Add(i + baseIndex);
            }

            mats.AddRange(p.TriangleMaterials);
            if (uvs is not null)
            {
                if (p.CornerUVs is not null)
                {
                    uvs.AddRange(p.CornerUVs);
                }
                else
                {
                    uvs.AddRange(new float[p.TriangleCount * 6]);
                }
            }
        }

        return new ObjectMesh
        {
            Name = name,
            NodePath = name,
            Positions = positions.ToArray(),
            Indices = indices.ToArray(),
            TriangleMaterials = mats.ToArray(),
            CornerUVs = uvs?.ToArray(),
        };
    }
}
