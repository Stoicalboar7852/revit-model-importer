namespace RevitModelImporter.Core.Geometry;

/// <summary>Row-major 4x4 affine transform in double precision. Points are column vectors:
/// p' = M * p, so the translation lives in M14, M24, M34 (same convention as Assimp).</summary>
public readonly struct Mat4 : IEquatable<Mat4>
{
    public readonly double M11, M12, M13, M14;
    public readonly double M21, M22, M23, M24;
    public readonly double M31, M32, M33, M34;
    public readonly double M41, M42, M43, M44;

    public Mat4(
        double m11, double m12, double m13, double m14,
        double m21, double m22, double m23, double m24,
        double m31, double m32, double m33, double m34,
        double m41, double m42, double m43, double m44)
    {
        M11 = m11; M12 = m12; M13 = m13; M14 = m14;
        M21 = m21; M22 = m22; M23 = m23; M24 = m24;
        M31 = m31; M32 = m32; M33 = m33; M34 = m34;
        M41 = m41; M42 = m42; M43 = m43; M44 = m44;
    }

    public static Mat4 Identity { get; } = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    public static Mat4 Translation(double x, double y, double z) => new(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);

    public static Mat4 Scale(double s) => new(s, 0, 0, 0, 0, s, 0, 0, 0, 0, s, 0, 0, 0, 0, 1);

    public static Mat4 Scale(double x, double y, double z) => new(x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1);

    /// <summary>Rotation about the X axis by <paramref name="radians"/> (right-handed).</summary>
    public static Mat4 RotationX(double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return new(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1);
    }

    /// <summary>Rotation about the Z axis by <paramref name="radians"/> (right-handed).</summary>
    public static Mat4 RotationZ(double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return new(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    }

    public static Mat4 operator *(Mat4 a, Mat4 b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
        a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
        a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
        a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
        a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
        a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
        a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
        a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
        a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
        a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
        a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
        a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
        a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
        a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);

    public Vec3 TransformPoint(Vec3 p) => new(
        M11 * p.X + M12 * p.Y + M13 * p.Z + M14,
        M21 * p.X + M22 * p.Y + M23 * p.Z + M24,
        M31 * p.X + M32 * p.Y + M33 * p.Z + M34);

    public Vec3 TransformVector(Vec3 v) => new(
        M11 * v.X + M12 * v.Y + M13 * v.Z,
        M21 * v.X + M22 * v.Y + M23 * v.Z,
        M31 * v.X + M32 * v.Y + M33 * v.Z);

    /// <summary>Determinant of the upper 3x3 block. Negative means the transform mirrors
    /// geometry, which flips triangle winding.</summary>
    public double Determinant3x3 =>
        M11 * (M22 * M33 - M23 * M32) - M12 * (M21 * M33 - M23 * M31) + M13 * (M21 * M32 - M22 * M31);

    public bool IsIdentity => Equals(Identity);

    public double[] ToArray() => [M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44];

    public static Mat4 FromArray(IReadOnlyList<double> m)
    {
        if (m.Count != 16)
        {
            throw new ArgumentException("A 4x4 matrix needs 16 values.", nameof(m));
        }

        return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    }

    public bool Equals(Mat4 other) => ToArray().AsSpan().SequenceEqual(other.ToArray());

    public override bool Equals(object? obj) => obj is Mat4 m && Equals(m);

    public override int GetHashCode() => HashCode.Combine(M11, M22, M33, M14, M24, M34);

    public static bool operator ==(Mat4 left, Mat4 right) => left.Equals(right);

    public static bool operator !=(Mat4 left, Mat4 right) => !left.Equals(right);
}
