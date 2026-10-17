using System;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Platform;

// The few SDL3 entry points needed to put a Vulkan surface on the SDL3 window
// which LinuxCompat creates (with SDL_WINDOW_VULKAN) in place of the WinForms one.
// LinuxCompat has already loaded the library, so we get the same instance back.
//
// Function pointers instead of [LibraryImport]: Pulsar compiles plugins with plain Roslyn,
// which does not run source generators, so LibraryImport stubs would never be generated.
internal static unsafe class Sdl3
{
    // Distributions often ship only the versioned SONAME, without the dev symlink
    private static readonly string[] LibraryNames = ["libSDL3.so.0", "libSDL3.so"];

    private static delegate* unmanaged<uint*, byte**> vulkanGetInstanceExtensions;
    private static delegate* unmanaged<IntPtr, VkInstance, void*, VkSurfaceKHR*, byte> vulkanCreateSurface;
    private static delegate* unmanaged<IntPtr, int*, int*, byte> getWindowSizeInPixels;
    private static delegate* unmanaged<byte*> getError;

    public static bool IsLoaded { get; private set; }

    public static bool TryLoad(out string reason)
    {
        if (IsLoaded)
        {
            reason = null;
            return true;
        }

        IntPtr library = IntPtr.Zero;
        foreach (var name in LibraryNames)
        {
            if (NativeLibrary.TryLoad(name, out library))
                break;
        }

        if (library == IntPtr.Zero)
        {
            reason = "SDL3 library not found (" + string.Join(", ", LibraryNames) + ")";
            return false;
        }

        try
        {
            vulkanGetInstanceExtensions = (delegate* unmanaged<uint*, byte**>)NativeLibrary.GetExport(library, "SDL_Vulkan_GetInstanceExtensions");
            vulkanCreateSurface = (delegate* unmanaged<IntPtr, VkInstance, void*, VkSurfaceKHR*, byte>)NativeLibrary.GetExport(library, "SDL_Vulkan_CreateSurface");
            getWindowSizeInPixels = (delegate* unmanaged<IntPtr, int*, int*, byte>)NativeLibrary.GetExport(library, "SDL_GetWindowSizeInPixels");
            getError = (delegate* unmanaged<byte*>)NativeLibrary.GetExport(library, "SDL_GetError");
        }
        catch (EntryPointNotFoundException e)
        {
            reason = "SDL3 library is missing an entry point: " + e.Message;
            return false;
        }

        IsLoaded = true;
        reason = null;
        return true;
    }

    public static byte** Vulkan_GetInstanceExtensions(out uint count)
    {
        uint n;
        var names = vulkanGetInstanceExtensions(&n);
        count = n;
        return names;
    }

    public static bool Vulkan_CreateSurface(IntPtr window, VkInstance instance, out VkSurfaceKHR surface)
    {
        VkSurfaceKHR s;
        var ok = vulkanCreateSurface(window, instance, null, &s) != 0;
        surface = s;
        return ok;
    }

    public static bool GetWindowSizeInPixels(IntPtr window, out int width, out int height)
    {
        int w, h;
        var ok = getWindowSizeInPixels(window, &w, &h) != 0;
        width = w;
        height = h;
        return ok;
    }

    public static string GetError() => Marshal.PtrToStringUTF8((IntPtr)getError()) ?? "";
}
