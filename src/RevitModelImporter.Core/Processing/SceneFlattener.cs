using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Scene;

namespace RevitModelImporter.Core.Processing;

/// <summary>Walks the node hierarchy, accumulates transforms and produces one <see cref="ObjectMesh"/> per
/// node instance that references meshes. Meshes referenced by several nodes (instancing) are copied for
/// each instance. All meshes of a node are combined so material seams become shared edges.</summary>
public static class SceneFlattener
{
    public static List<ObjectMesh> Flatten(SceneModel scene, Mat4 sourceToTarget)
    {
        var result = new List<ObjectMesh>();
        Visit(scene, scene.Root, sourceToTarget, "", result);
        return result;
    }

    private static void Visit(SceneModel scene, SceneNode node, Mat4 parent, string parentPath, List<ObjectMesh> result)
    {
        var world = parent * node.LocalTransform;
        var name = string.IsNullOrWhiteSpace(node.Name) ? "Node" : node.Name.Trim();
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;

        if (node.MeshIndices.Count > 0)
        {
            var meshes = node.MeshIndices
                .Where(i => i >= 0 && i < scene.Meshes.Count)
                .Select(i => scene.Meshes[i])
                .Where(m => m.TriangleCount > 0)
                .ToList();
            if (meshes.Count > 0)
            {
                var obj = Combine(meshes, world);
                obj.Name = ObjectName(node, meshes);
                obj.NodePath = path;
                result.Add(obj);
            }
        }

        foreach (var child in node.Children)
        {
            Visit(scene, child, world, path, result);
        }
    }

    private static string ObjectName(SceneNode node, List<SceneMesh> meshes)
    {
        if (!string.IsNullOrWhiteSpace(node.Name) && !node.Name.StartsWith("Assimp", StringComparison.Ordinal))
        {
            return node.Name.Trim();
        }

        var meshName = meshes.Select(m => m.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        return meshName?.Trim() ?? (string.IsNullOrWhiteSpace(node.Name) ? "Object" : node.Name.Trim());
    }

    /// <summary>Transforms and concatenates meshes, welding vertices with bit-identical world positions so
    /// that the per-material sub-meshes produced by loaders share their boundary vertices again.</summary>
    internal static ObjectMesh Combine(IReadOnlyList<SceneMesh> meshes, Mat4 world)
    {
        var flip = world.Determinant3x3 < 0;
        var totalTris = meshes.Sum(m => m.TriangleCount);
        var anyUv = meshes.Any(m => m.TexCoords is not null);

        var positions = new List<double>(meshes.Sum(m => m.VertexCount) * 3);
        var indices = new int[totalTris * 3];
        var mats = new int[totalTris];
        var uvs = anyUv ? new float[totalTris * 6] : null;
        var lookup = new Dictionary<Vec3, int>(meshes.Sum(m => m.VertexCount));

        var t = 0;
        foreach (var mesh in meshes)
        {
            var remap = new int[mesh.VertexCount];
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var p = world.TransformPoint(new Vec3(mesh.Positions[3 * v], mesh.Positions[3 * v + 1], mesh.Positions[3 * v + 2]));
                // Normalise -0.0 so it hashes like 0.0.
                p = new Vec3(p.X + 0.0, p.Y + 0.0, p.Z + 0.0);
                if (!lookup.TryGetValue(p, out var idx))
                {
                    idx = positions.Count / 3;
                    positions.Add(p.X);
                    positions.Add(p.Y);
                    positions.Add(p.Z);
                    lookup.Add(p, idx);
                }

                remap[v] = idx;
            }

            for (var k = 0; k < mesh.TriangleCount; k++, t++)
            {
                var a = mesh.Indices[3 * k];
                var b = mesh.Indices[3 * k + 1];
                var c = mesh.Indices[3 * k + 2];
                if (flip)
                {
                    (b, c) = (c, b);
                }

                indices[3 * t] = remap[a];
                indices[3 * t + 1] = remap[b];
                indices[3 * t + 2] = remap[c];
                mats[t] = mesh.MaterialIndex;
                if (uvs is not null && mesh.TexCoords is { } tc)
                {
                    uvs[6 * t] = tc[2 * a];
                    uvs[6 * t + 1] = tc[2 * a + 1];
                    uvs[6 * t + 2] = tc[2 * b];
                    uvs[6 * t + 3] = tc[2 * b + 1];
                    uvs[6 * t + 4] = tc[2 * c];
                    uvs[6 * t + 5] = tc[2 * c + 1];
                }
            }
        }

        return new ObjectMesh
        {
            Positions = positions.ToArray(),
            Indices = indices,
            TriangleMaterials = mats,
            CornerUVs = uvs,
        };
    }
}
