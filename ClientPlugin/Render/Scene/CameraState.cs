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

    // rgb: linear fallback ambient, w: factor (used until the IBL cubemap is ready)
    public Vector4 AmbientColor;

    // Inverse of the Y-flipped view-projection, transposed rows (skybox view rays)
    public Vector4 InvViewProjection0;
    public Vector4 InvViewProjection1;
    public Vector4 InvViewProjection2;
    public Vector4 InvViewProjection3;

    // SkyboxOrientation, transposed rows
    public Vector4 BackgroundOrientation0;
    public Vector4 BackgroundOrientation1;
    public Vector4 BackgroundOrientation2;
    public Vector4 BackgroundOrientation3;

    // x: EnvSkyboxBrightness, y: IBL mip count, z: AmbientDiffuseFactor, w: AmbientSpecularFactor
    public Vector4 SkyboxParams;

    public uint SkyboxCube;
    public uint IblCube;
    public uint BrdfLut;
    public uint Pad;
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

    // Stock (unflipped) projection for frustum extraction, camera-relative
    public Matrix CullingViewProjection { get; private set; }

    public Vector3D Position { get; private set; }
    public float Near { get; private set; }
    public float Far { get; private set; }
    public float FovH { get; private set; }
    public float FovV { get; private set; }

    // MyCommon.MoveToNextFrame: scales LOD switching distances by FOV and resolution
    public float LodCoefficient { get; private set; } = 1f;

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

        // Frustum culling uses the stock D3D-space matrix (the Y flip below does not change the volume,
        // but keeping the stock matrix avoids mirrored plane winding)
        CullingViewProjection = view * projection;

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
        LodCoefficient = message.FOV > 0f && resolution.Y > 0
            ? (float)(Math.Tan(message.FOV / 2.0) / Math.Tan(35.0 * Math.PI / 180.0) * (1080.0 / resolution.Y))
            : 1f;
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

    public FrameConstants BuildFrameConstants(MyEnvironmentData environment,
        uint skyboxCube, uint iblCube, uint brdfLut, uint iblMipCount)
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
        var invViewProjection = Matrix.Transpose(Matrix.Invert(ViewProjectionAt0));
        var orientation = Matrix.Transpose(Matrix.CreateFromQuaternion(environment.SkyboxOrientation));

        return new FrameConstants
        {
            ViewProjection0 = new Vector4(viewProjection.M11, viewProjection.M12, viewProjection.M13, viewProjection.M14),
            ViewProjection1 = new Vector4(viewProjection.M21, viewProjection.M22, viewProjection.M23, viewProjection.M24),
            ViewProjection2 = new Vector4(viewProjection.M31, viewProjection.M32, viewProjection.M33, viewProjection.M34),
            ViewProjection3 = new Vector4(viewProjection.M41, viewProjection.M42, viewProjection.M43, viewProjection.M44),
            SunDirection = new Vector4(sunDirection, light.SunDiffuseFactor),
            SunColor = new Vector4(sun, light.SunSpecularFactor),
            AmbientColor = new Vector4(1f, 1f, 1f, ambient * light.AmbientDiffuseFactor),

            InvViewProjection0 = new Vector4(invViewProjection.M11, invViewProjection.M12, invViewProjection.M13, invViewProjection.M14),
            InvViewProjection1 = new Vector4(invViewProjection.M21, invViewProjection.M22, invViewProjection.M23, invViewProjection.M24),
            InvViewProjection2 = new Vector4(invViewProjection.M31, invViewProjection.M32, invViewProjection.M33, invViewProjection.M34),
            InvViewProjection3 = new Vector4(invViewProjection.M41, invViewProjection.M42, invViewProjection.M43, invViewProjection.M44),

            BackgroundOrientation0 = new Vector4(orientation.M11, orientation.M12, orientation.M13, orientation.M14),
            BackgroundOrientation1 = new Vector4(orientation.M21, orientation.M22, orientation.M23, orientation.M24),
            BackgroundOrientation2 = new Vector4(orientation.M31, orientation.M32, orientation.M33, orientation.M34),
            BackgroundOrientation3 = new Vector4(orientation.M41, orientation.M42, orientation.M43, orientation.M44),

            SkyboxParams = new Vector4(light.EnvSkyboxBrightness, iblMipCount,
                light.AmbientDiffuseFactor, light.AmbientSpecularFactor),
            SkyboxCube = skyboxCube,
            IblCube = iblCube,
            BrdfLut = brdfLut,
        };
    }
}
