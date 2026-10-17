using System;
using System.Runtime.InteropServices;
using VRageMath;
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

    // SDL_WINDOWPOS_CENTERED_DISPLAY(id): centers the window on a display and, more importantly,
    // makes SDL remember it as the target display for the next fullscreen transition.
    private const uint WindowPosCenteredDisplay = 0x2FFF0000u;

    [StructLayout(LayoutKind.Sequential)]
    public struct SdlRect
    {
        public int X;
        public int Y;
        public int W;
        public int H;
    }

    private static delegate* unmanaged<uint*, byte**> vulkanGetInstanceExtensions;
    private static delegate* unmanaged<IntPtr, VkInstance, void*, VkSurfaceKHR*, byte> vulkanCreateSurface;
    private static delegate* unmanaged<IntPtr, int*, int*, byte> getWindowSizeInPixels;
    private static delegate* unmanaged<byte*> getError;
    private static delegate* unmanaged<int*, uint*> getDisplays;
    private static delegate* unmanaged<uint, SdlRect*, byte> getDisplayBounds;
    private static delegate* unmanaged<uint, byte*> getDisplayName;
    private static delegate* unmanaged<IntPtr, int, int, byte> setWindowPosition;
    private static delegate* unmanaged<void*, void> free;

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
            getDisplays = (delegate* unmanaged<int*, uint*>)NativeLibrary.GetExport(library, "SDL_GetDisplays");
            getDisplayBounds = (delegate* unmanaged<uint, SdlRect*, byte>)NativeLibrary.GetExport(library, "SDL_GetDisplayBounds");
            getDisplayName = (delegate* unmanaged<uint, byte*>)NativeLibrary.GetExport(library, "SDL_GetDisplayName");
            setWindowPosition = (delegate* unmanaged<IntPtr, int, int, byte>)NativeLibrary.GetExport(library, "SDL_SetWindowPosition");
            free = (delegate* unmanaged<void*, void>)NativeLibrary.GetExport(library, "SDL_free");
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

    // Selects the SDL display that matches the adapter/output the game chose (DXGI-style DesktopBounds),
    // then asks SDL to center the window on it. SDL remembers the display as the fullscreen target even
    // on Wayland, where SetWindowPosition itself is a no-op. Must run on the SDL video thread.
    // Returns false with a reason when no matching display was found or the call failed.
    public static bool TrySetWindowDisplay(IntPtr window, Rectangle desktopBounds, string outputName, out string description)
    {
        description = null;
        if (!IsLoaded)
        {
            description = "SDL3 is not loaded";
            return false;
        }

        var displays = getDisplays(null);
        if (displays == null)
        {
            description = "SDL_GetDisplays failed: " + GetError();
            return false;
        }

        try
        {
            var bestDisplay = 0u;
            var bestScore = int.MaxValue;
            var bestRect = default(SdlRect);
            string bestName = null;
            var count = 0;

            for (var i = 0; displays[i] != 0; i++)
            {
                var id = displays[i];
                SdlRect rect;
                if (getDisplayBounds(id, &rect) == 0)
                    continue;

                var name = Marshal.PtrToStringUTF8((IntPtr)getDisplayName(id)) ?? "";
                var score = ScoreDisplay(rect, name, desktopBounds, outputName);
                count++;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestDisplay = id;
                bestRect = rect;
                bestName = name;
            }

            if (bestDisplay == 0)
            {
                description = count == 0 ? "SDL reports no displays" : "no SDL display matches the selected monitor";
                return false;
            }

            // Centering on a display also sets SDL's pending target display for the next fullscreen
            // request. Wayland refuses to move toplevel windows, but SDL records the pending display
            // before calling the backend, and that is what the following fullscreen request uses.
            var position = (int)(WindowPosCenteredDisplay | (bestDisplay & 0xFFFF));
            var moved = setWindowPosition(window, position, position) != 0;

            description = $"SDL display {bestDisplay} '{bestName}' at {bestRect.X},{bestRect.Y} {bestRect.W}x{bestRect.H}"
                          + (moved ? "" : $" (positioning declined: {GetError()}; pending display set)");
            return true;
        }
        finally
        {
            free(displays);
        }
    }

    private static int ScoreDisplay(SdlRect rect, string name, Rectangle bounds, string outputName)
    {
        if (rect.X == bounds.X && rect.Y == bounds.Y && rect.W == bounds.Width && rect.H == bounds.Height)
            return 0;

        if (!string.IsNullOrEmpty(outputName) && !string.IsNullOrEmpty(name))
        {
            if (string.Equals(name, outputName, StringComparison.OrdinalIgnoreCase))
                return 1;
            if (name.IndexOf(outputName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                outputName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
        }

        // Same resolution and a nearby center: usually the same physical output under a different layout
        if (rect.W == bounds.Width && rect.H == bounds.Height)
        {
            var dx = rect.X + rect.W / 2 - (bounds.X + bounds.Width / 2);
            var dy = rect.Y + rect.H / 2 - (bounds.Y + bounds.Height / 2);
            return 100 + Math.Abs(dx) + Math.Abs(dy);
        }

        return int.MaxValue;
    }
}
