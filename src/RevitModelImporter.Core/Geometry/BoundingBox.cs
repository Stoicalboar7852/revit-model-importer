namespace RevitModelImporter.Core.Geometry;

/// <summary>Axis-aligned bounding box. <see cref="IsEmpty"/> until the first point is added.</summary>
public sealed class BoundingBox
{
    public Vec3 Min { get; private set; } = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);

    public Vec3 Max { get; private set; } = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => Min.X > Max.X;

    public Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;

    public Vec3 Center => IsEmpty ? Vec3.Zero : (Min + Max) * 0.5;

    public double Diagonal => Size.Length;

    public void Add(Vec3 p)
    {
        Min = Vec3.Min(Min, p);
        Max = Vec3.Max(Max, p);
    }

    public void Add(BoundingBox other)
    {
        if (other.IsEmpty)
        {
            return;
        }

        Add(other.Min);
        Add(other.Max);
    }

    public static BoundingBox FromPositions(ReadOnlySpan<double> xyz)
    {
        var box = new BoundingBox();
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        for (var i = 0; i + 2 < xyz.Length; i += 3)
        {
            var x = xyz[i];
            var y = xyz[i + 1];
            var z = xyz[i + 2];
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }

        if (minX <= maxX)
        {
            box.Add(new Vec3(minX, minY, minZ));
            box.Add(new Vec3(maxX, maxY, maxZ));
        }

        return box;
    }
}
