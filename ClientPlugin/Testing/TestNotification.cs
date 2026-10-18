using System;
using Sandbox.ModAPI;

namespace ClientPlugin.Testing;

internal static class TestNotification
{
    private static bool shown;

    public static void ShowIfRequested()
    {
        if (shown)
            return;

        var text = Environment.GetEnvironmentVariable("VULKAN_NOTIFICATION");
        if (string.IsNullOrEmpty(text))
            return;

        shown = true;
        var duration = int.TryParse(Environment.GetEnvironmentVariable("VULKAN_NOTIFICATION_MS"), out var ms) && ms > 0
            ? ms
            : 60000;
        MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            MyAPIGateway.Utilities.ShowNotification(text, duration, "White"), "Vulkan test notification", 0, 0);
    }
}
