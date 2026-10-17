using System;
using System.Runtime.InteropServices;

namespace ClientPlugin.Render.Platform;

// Function pointer instead of [LibraryImport], see Sdl3 for why
internal static unsafe class Kernel32
{
    // HMODULE of the process executable, for VkWin32SurfaceCreateInfoKHR.hinstance
    public static IntPtr GetModuleHandle()
    {
        var kernel32 = NativeLibrary.Load("kernel32.dll");
        var getModuleHandleW = (delegate* unmanaged<char*, IntPtr>)NativeLibrary.GetExport(kernel32, "GetModuleHandleW");
        return getModuleHandleW(null);
    }
}
