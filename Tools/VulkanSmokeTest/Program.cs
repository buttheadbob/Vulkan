using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Platform;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageMath;

namespace VulkanSmokeTest;

// Exercises the plugin's Vulkan device layer the way the game will: an SDL3 window created with
// SDL_WINDOW_VULKAN (as LinuxCompat does), then instance, surface, device, swapchain and frame loop.
// Usage: VulkanSmokeTest [--validation] [--seconds N]
//        VulkanSmokeTest --dds <directory>   (parse every DDS file below the directory with the plugin's loader)
internal static unsafe class Program
{
    private const uint SDL_INIT_VIDEO = 0x20;
    private const ulong SDL_WINDOW_VULKAN = 0x10000000;
    private const ulong SDL_WINDOW_RESIZABLE = 0x20;
    private const uint SDL_EVENT_QUIT = 0x100;

    private static int Main(string[] args)
    {
        var ddsIndex = Array.IndexOf(args, "--dds");
        if (ddsIndex >= 0)
            return DdsSurvey.Run(args[ddsIndex + 1]);

        var validation = Array.IndexOf(args, "--validation") >= 0;
        var shadowTest = Array.IndexOf(args, "--shadow") >= 0;
        var secondsIndex = Array.IndexOf(args, "--seconds");
        var seconds = secondsIndex >= 0 ? double.Parse(args[secondsIndex + 1]) : 3.0;

        if (!Sdl3.TryLoad(out var reason))
            return Fail(reason);

        var sdl = NativeLibrary.Load("libSDL3.so.0");
        var init = (delegate* unmanaged<uint, byte>)NativeLibrary.GetExport(sdl, "SDL_Init");
        var createWindow = (delegate* unmanaged<byte*, int, int, ulong, IntPtr>)NativeLibrary.GetExport(sdl, "SDL_CreateWindow");
        var destroyWindow = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(sdl, "SDL_DestroyWindow");
        var pollEvent = (delegate* unmanaged<byte*, byte>)NativeLibrary.GetExport(sdl, "SDL_PollEvent");
        var quit = (delegate* unmanaged<void>)NativeLibrary.GetExport(sdl, "SDL_Quit");

        if (init(SDL_INIT_VIDEO) == 0)
            return Fail("SDL_Init: " + Sdl3.GetError());

        var title = "Vulkan smoke test\0"u8;
        IntPtr window;
        fixed (byte* t = title)
            window = createWindow(t, 1280, 720, SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
        if (window == IntPtr.Zero)
            return Fail("SDL_CreateWindow: " + Sdl3.GetError());

        try
        {
            if (!VulkanInstance.TryInitializeLoader(out reason))
                return Fail(reason);

            // Probe the same way VulkanRender.TryCreate does
            using (var probe = VulkanInstance.Create([], validation: false))
            {
                foreach (var (_, caps) in probe.EnumerateDevices())
                    Console.WriteLine($"Probe: {caps} meetsRequirements={caps.MeetsRequirements(out var missing)} {missing}");
            }

            var extensions = new System.Collections.Generic.List<string>();
            var names = Sdl3.Vulkan_GetInstanceExtensions(out uint count);
            for (var i = 0; i < count; i++)
                extensions.Add(Marshal.PtrToStringUTF8((IntPtr)names[i]));

            using var instance = VulkanInstance.Create(extensions, validation);
            if (!Sdl3.Vulkan_CreateSurface(window, instance.Handle, out var surface))
                return Fail("SDL_Vulkan_CreateSurface: " + Sdl3.GetError());

            using var device = VulkanDevice.Create(instance, surface, null);

            if (shadowTest)
                return RunShadowPipelineTest(instance, device);

            var swapchain = new VulkanSwapchain(instance, device, surface);
            swapchain.Recreate(new Vector2I(1280, 720), vsync: true);
            var frameLoop = new FrameLoop(device, swapchain);

            var stopwatch = Stopwatch.StartNew();
            var frames = 0;
            var eventBuffer = stackalloc byte[128];
            var running = true;
            while (running && stopwatch.Elapsed.TotalSeconds < seconds)
            {
                while (pollEvent(eventBuffer) != 0)
                {
                    if (*(uint*)eventBuffer == SDL_EVENT_QUIT)
                        running = false;
                }

                if (frameLoop.SwapchainOutdated)
                {
                    Sdl3.GetWindowSizeInPixels(window, out var w, out var h);
                    swapchain.Recreate(new Vector2I(w, h), vsync: true);
                    frameLoop.OnSwapchainRecreated();
                }

                if (!frameLoop.TryBeginFrame())
                    continue;

                var t = (float)stopwatch.Elapsed.TotalSeconds;
                frameLoop.ClearBackbuffer(new VkClearColorValue(0.5f + 0.5f * MathF.Sin(t * 2f), 0.2f, 0.5f + 0.5f * MathF.Cos(t * 2f), 1f));
                frameLoop.EndFrame();
                frameLoop.Present();
                frames++;
            }

            Console.WriteLine($"OK: presented {frames} frames in {stopwatch.Elapsed.TotalSeconds:0.00} s ({frames / stopwatch.Elapsed.TotalSeconds:0} fps)");

            frameLoop.Dispose();
            swapchain.Dispose();
            instance.Api.vkDestroySurfaceKHR(surface, null);
            return 0;
        }
        catch (Exception e)
        {
            return Fail(e.ToString());
        }
        finally
        {
            destroyWindow(window);
            quit();
        }
    }

    // Creates the exact depth-only pipeline ModelPass uses for the sun shadow map. This is the
    // operation that previously crashed RADV; keep it out of the game until it passes here with
    // --validation.
    private static int RunShadowPipelineTest(VulkanInstance instance, VulkanDevice device)
    {
        using var context = new GpuContext(instance, device);
        var builder = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Model.Code.ToArray(),
            VertexEntry = ShaderBytecode.Model.ShadowMain,
            FragmentEntry = ShaderBytecode.Model.ShadowFragment,
            ColorFormats = [],
            DepthFormat = VkFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
            CullMode = VkCullModeFlags.Back,
            FrontFace = VkFrontFace.Clockwise,
        };

        var pipeline = builder.Build(context, "shadow-smoke");
        device.Api.vkDestroyPipeline(pipeline, null);
        Console.WriteLine("OK: shadow depth-only pipeline created");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.WriteLine("FAILED: " + message);
        return 1;
    }
}
