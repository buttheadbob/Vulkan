using System;
using VRageMath;

namespace ClientPlugin.Render.Scene;

// Culling volume from a row-vector D3D view-projection with the game's infinite reverse-Z projection.
// The far plane is at infinity, so only left/right/bottom/top and the near plane bound the volume.
internal sealed class Frustum
{
    private readonly Plane[] planes = new Plane[5];

    public Frustum(Matrix viewProjection)
    {
        // Transposed rows are the columns of the original matrix, so clip.i = dot(position, row_i)
        var vp = Matrix.Transpose(viewProjection);
        var c0 = Row(vp, 0);
        var c1 = Row(vp, 1);
        var c2 = Row(vp, 2);
        var c3 = Row(vp, 3);

        planes[0] = Normalize(c3 + c0); // left:   x + w >= 0
        planes[1] = Normalize(c3 - c0); // right:  w - x >= 0
        planes[2] = Normalize(c3 + c1); // bottom: y + w >= 0
        planes[3] = Normalize(c3 - c1); // top:    w - y >= 0
        planes[4] = Normalize(c3 - c2); // near:   z <= w (reverse-Z, near plane at depth 1)
    }

    public bool Intersects(BoundingBox box)
    {
        var center = (box.Min + box.Max) * 0.5f;
        var radius = (box.Max - box.Min).Length() * 0.5f;

        foreach (var plane in planes)
        {
            if (plane.DotCoordinate(center) < -radius)
                return false;
        }

        return true;
    }

    public void WritePlanes(Span<Vector4> destination)
    {
        for (var i = 0; i < planes.Length; i++)
        {
            var plane = planes[i];
            destination[i] = new Vector4(plane.Normal, plane.D);
        }
    }

    private static Vector4 Row(Matrix matrix, int index) => index switch
    {
        0 => new Vector4(matrix.M11, matrix.M12, matrix.M13, matrix.M14),
        1 => new Vector4(matrix.M21, matrix.M22, matrix.M23, matrix.M24),
        2 => new Vector4(matrix.M31, matrix.M32, matrix.M33, matrix.M34),
        _ => new Vector4(matrix.M41, matrix.M42, matrix.M43, matrix.M44),
    };

    private static Plane Normalize(Vector4 plane)
    {
        var length = new Vector3(plane.X, plane.Y, plane.Z).Length();
        return length > 1e-6f
            ? new Plane(plane.X / length, plane.Y / length, plane.Z / length, plane.W / length)
            : new Plane(plane.X, plane.Y, plane.Z, plane.W);
    }
}
