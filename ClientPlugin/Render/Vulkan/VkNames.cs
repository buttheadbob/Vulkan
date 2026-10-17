using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClientPlugin.Render.Vulkan;

internal static unsafe class VkNames
{
    // Vortice exposes extension and layer names as UTF-8 spans
    public static string ToString(ReadOnlySpan<byte> name) => Encoding.UTF8.GetString(name).TrimEnd('\0');

    // Fixed-size, null-terminated name fields (VkExtensionProperties.extensionName, etc.)
    public static string FromFixed(byte* name) => Marshal.PtrToStringUTF8((IntPtr)name) ?? "";

    public static HashSet<string> ToSet(IEnumerable<string> names) => new(names, StringComparer.Ordinal);
}
