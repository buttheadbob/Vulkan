using System;
using System.Runtime.InteropServices;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace ClientPlugin.Render.Scene;

// Per-frame constants read by scene shaders through a buffer device address.
// Matrices use the game's row-vector convention, one explicit float4 row each.
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Vector4 ViewProjection0;
    public Vector4 ViewProjection1;
    public Vector4 ViewProjection2;
    public Vector4 ViewProjection3;

    // xyz: direction from the sun (SunLightDirection), w: sun diffuse factor
    public Vector4 SunDirection;

    // rgb: linear sun color (SunColorRaw), w: sun specular factor
    public Vector4 SunColor;

    // rgb: linear ambient color, w: ambient factor
    public Vector4 AmbientColor;
}

// Camera from MyRenderMessageSetCameraViewMatrix, plus the stock projection and camera-relative setup.
// Rendering is camera-relative: the view matrix keeps only rotation and world matrices move by -CameraPosition,
// so float precision stays near the camera even far from the origin.
internal sealed class CameraState
{
    public bool HasValue { get; private set; }
    public MatrixD ViewD { get; private set; }
    public Matrix ViewAt0 { get; private set; }
    public Matrix ViewProjectionAt0 { get; private set; }
    public Vector3D Position { get; private set; }
    public float Near { get; private set; }
    public float Far { get; private set; }
    public float FovH { get; private set; }
    public float FovV { get; private set; }

    public void Set(MyRenderMessageSetCameraViewMatrix message, Vector2I resolution)
    {
        var aspect = resolution.Y != 0 ? resolution.X / (float)resolution.Y : 1f;

        // MyRender11.SetupCameraMatricesInternal: rotation-only view for world-space rendering
        var view = (Matrix)message.ViewMatrix;
        view.M14 = view.M24 = view.M34 = 0;
        view.M41 = view.M42 = view.M43 = 0;
        view.M44 = 1;

        var projection = message.FOV > 0f
            ? CreatePerspectiveFovRhInfiniteComplementary(message.FOV, aspect, message.NearPlane)
            : message.ProjectionMatrix;
        projection.M31 = message.ProjectionOffsetX;
        projection.M32 = message.ProjectionOffsetY;

        // Vulkan clip space has +Y down; the stock matrices are D3D (Y up)
        projection.M22 = -projection.M22;
        projection.M32 = -projection.M32;

        ViewD = message.ViewMatrix;
        ViewAt0 = view;
        ViewProjectionAt0 = view * projection;
        Position = message.CameraPosition;
        Near = message.NearPlane;
        Far = message.FarPlane;
        FovH = message.FOV;
        FovV = (float)(2.0 * Math.Atan(Math.Tan(message.FOV / 2.0) * (resolution.Y / (double)resolution.X)));
        HasValue = true;
    }

    // VRage.Math/VRageMath/Matrix.cs:1984
    private static Matrix CreatePerspectiveFovRhInfiniteComplementary(float fieldOfView, float aspectRatio, float nearPlaneDistance)
    {
        var num = 1f / (float)Math.Tan(fieldOfView * 0.5);
        var m = num / aspectRatio;
        return new Matrix
        {
            M11 = m,
            M22 = num,
            M33 = 0f,
            M34 = -1f,
            M43 = nearPlaneDistance,
        };
    }

    public FrameConstants BuildFrameConstants(MyEnvironmentData environment)
    {
        var light = environment.EnvironmentLight;
        var sun = light.SunColorRaw;
        var sunDirection = light.SunLightDirection;
        var length = sunDirection.Length();
        if (length > 1e-6f)
            sunDirection /= length;

        // Stock adds MyEnvironmentProbe.LastAmbient to AmbientForwardPass (MyCommon.cs:427), which needs the
        // env probe we do not render yet. Until then use the game's own probe ambient floor, in neutral gray,
        // so unlit faces are not crushed to black.
        var ambient = Math.Max(light.AmbientForwardPass, environment.EnvironmentProbe.AmbientMinClamp);

        // The shader computes dot(position, row) (row-vector math), so hand it the transpose: each float4
        // is then a column of the XNA matrix, which is what that dot product needs.
        var viewProjection = Matrix.Transpose(ViewProjectionAt0);
        return new FrameConstants
        {
            ViewProjection0 = new Vector4(viewProjection.M11, viewProjection.M12, viewProjection.M13, viewProjection.M14),
            ViewProjection1 = new Vector4(viewProjection.M21, viewProjection.M22, viewProjection.M23, viewProjection.M24),
            ViewProjection2 = new Vector4(viewProjection.M31, viewProjection.M32, viewProjection.M33, viewProjection.M34),
            ViewProjection3 = new Vector4(viewProjection.M41, viewProjection.M42, viewProjection.M43, viewProjection.M44),
            SunDirection = new Vector4(sunDirection, light.SunDiffuseFactor),
            SunColor = new Vector4(sun, light.SunSpecularFactor),
            AmbientColor = new Vector4(1f, 1f, 1f, ambient * light.AmbientDiffuseFactor),
        };
    }
}
