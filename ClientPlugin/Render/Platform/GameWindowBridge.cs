using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using VRage;
using VRage.Platform.Windows.Forms;
using VRageMath;
using VRageRender;
using ClientPlugin.Render.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Platform;

// The only platform-specific part of the renderer: finding the game window,
// putting a Vulkan surface on it and forwarding window mode changes.
//
// Windows: the stock WinForms window (MyGameWindow), surface from its HWND.
// Linux:   the SDL3 window created by the LinuxCompat plugin, which stores the
//          SDL_Window* in MyWindowsWindows.WindowHandle in place of the HWND.
internal abstract class GameWindowBridge
{
    public abstract string Name { get; }

    public IntPtr Handle => ((MyWindowsWindows)MyVRage.Platform.Windows).WindowHandle;

    // Instance extensions required to create a surface on this platform
    public abstract IEnumerable<string> GetSurfaceExtensions();

    public abstract VkSurfaceKHR CreateSurface(VkInstance instance, VkInstanceApi api);

    // Size of the drawable area in pixels, if the window system knows it
    public abstract Vector2I? GetDrawableSize();

    // The stock renderer lets MyWindowsRender drive the window on device creation and
    // settings changes. That code path is bypassed with our renderer, so we do it here.
    public abstract void OnModeChanged(MyRenderDeviceSettings settings, MyAdapterInfo adapter);

    // Returns null with a reason if the current platform/window cannot be supported
    public static GameWindowBridge Detect(out string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            reason = null;
            return new Win32WindowBridge();
        }

        if (OperatingSystem.IsLinux())
        {
            var provider = SdlWindowBridge.FindLinuxCompatWindowProvider();
            if (provider == null)
            {
                reason = "LinuxCompat plugin not found, cannot locate the SDL3 game window";
                return null;
            }

            if (!Sdl3.TryLoad(out reason))
                return null;

            reason = null;
            return new SdlWindowBridge(provider);
        }

        reason = $"Unsupported operating system: {RuntimeInformation.OSDescription}";
        return null;
    }
}

internal sealed class Win32WindowBridge : GameWindowBridge
{
    public override string Name => "Win32";

    public override IEnumerable<string> GetSurfaceExtensions()
    {
        yield return VkNames.ToString(VK_KHR_SURFACE_EXTENSION_NAME);
        yield return VkNames.ToString(VK_KHR_WIN32_SURFACE_EXTENSION_NAME);
    }

    public override unsafe VkSurfaceKHR CreateSurface(VkInstance instance, VkInstanceApi api)
    {
        var createInfo = new VkWin32SurfaceCreateInfoKHR
        {
            hinstance = Kernel32.GetModuleHandle(),
            hwnd = Handle,
        };
        api.vkCreateWin32SurfaceKHR(&createInfo, out VkSurfaceKHR surface).CheckResult("vkCreateWin32SurfaceKHR");
        return surface;
    }

    public override Vector2I? GetDrawableSize() => null;

    public override void OnModeChanged(MyRenderDeviceSettings settings, MyAdapterInfo adapter)
    {
        // MyGameWindow.OnModeChanged(MyWindowModeEnum, int, int, Rectangle), called from the render
        // thread like the stock MyWindowsRender does. Reflection, because MyGameWindow derives from
        // a WinForms Form and referencing it would require System.Windows.Forms at compile time.
        object window = typeof(MyWindowsWindows).GetProperty(nameof(MyWindowsWindows.GameWindow))?.GetValue(MyVRage.Platform.Windows);
        window?.GetType()
            .GetMethod("OnModeChanged", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(window, [settings.WindowMode, settings.BackBufferWidth, settings.BackBufferHeight, adapter.DesktopBounds]);
    }
}

internal sealed class SdlWindowBridge : GameWindowBridge
{
    // LinuxCompat: ClientPlugin.Patches.PlatformGuards.SdlInput2Provider.Instance (SdlGameWindow)
    private const string ProviderTypeName = "ClientPlugin.Patches.PlatformGuards.SdlInput2Provider";

    private readonly PropertyInfo instanceProperty;

    public SdlWindowBridge(PropertyInfo instanceProperty)
    {
        this.instanceProperty = instanceProperty;
    }

    public override string Name => "SDL3 (LinuxCompat)";

    // Pulsar renames plugin assemblies when it loads them, so look the type up by name in all of them
    public static PropertyInfo FindLinuxCompatWindowProvider()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a != Assembly.GetExecutingAssembly())
            .Select(a => a.GetType(ProviderTypeName, false))
            .FirstOrDefault(t => t != null)
            ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    }

    public override unsafe IEnumerable<string> GetSurfaceExtensions()
    {
        var names = Sdl3.Vulkan_GetInstanceExtensions(out uint count);
        if (names == null)
            throw new InvalidOperationException("SDL_Vulkan_GetInstanceExtensions failed: " + Sdl3.GetError());

        var result = new List<string>((int)count);
        for (var i = 0; i < count; i++)
            result.Add(Marshal.PtrToStringUTF8((IntPtr)names[i]));
        return result;
    }

    public override unsafe VkSurfaceKHR CreateSurface(VkInstance instance, VkInstanceApi api)
    {
        if (!Sdl3.Vulkan_CreateSurface(Handle, instance, out VkSurfaceKHR surface))
            throw new InvalidOperationException("SDL_Vulkan_CreateSurface failed: " + Sdl3.GetError());
        return surface;
    }

    public override Vector2I? GetDrawableSize()
    {
        if (Sdl3.GetWindowSizeInPixels(Handle, out int width, out int height) && width > 0 && height > 0)
            return new Vector2I(width, height);
        return null;
    }

    public override void OnModeChanged(MyRenderDeviceSettings settings, MyAdapterInfo adapter)
    {
        var window = instanceProperty.GetValue(null);
        if (window == null)
            return;

        var refreshRate = 0f;
        if (settings.RefreshRate != 0)
            refreshRate = adapter.GetDisplayMode(settings.BackBufferWidth, settings.BackBufferHeight, settings.RefreshRate)?.RefreshRateF ?? 0f;

        // SdlGameWindow.OnModeChanged(MyWindowModeEnum, int, int, Rectangle, float)
        window.GetType()
            .GetMethod("OnModeChanged", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(window, [settings.WindowMode, settings.BackBufferWidth, settings.BackBufferHeight, adapter.DesktopBounds, refreshRate]);
    }
}
