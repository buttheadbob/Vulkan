using System;
using System.Threading;
using ClientPlugin.Render;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Testing;

// Development/test helper: VULKAN_STOCK_SCREENSHOT=<path> saves a reference frame from the
// renderer that is actually running (stock DirectX 11 or Vulkan, since MyRenderProxy routes
// it to the active renderer). The delay is counted from the automated "Continue Game" click.
internal static class StockScreenshot
{
    private static readonly string Path = Environment.GetEnvironmentVariable("VULKAN_STOCK_SCREENSHOT");

    private static readonly double DelaySeconds = ParseDelay();

    private static double ParseDelay()
    {
        var value = Environment.GetEnvironmentVariable("VULKAN_STOCK_SCREENSHOT_DELAY");
        return double.TryParse(value, out var seconds) && seconds > 0 ? seconds : 30;
    }

    public static void Schedule()
    {
        if (string.IsNullOrEmpty(Path))
            return;

        RenderLog.Info($"Stock screenshot scheduled in {DelaySeconds:0.#} s: {Path}");
        var timer = new Timer(_ =>
        {
            try
            {
                MyRenderProxy.TakeScreenshot(Vector2.One, Path, false, false, false);
                RenderLog.Info($"Stock screenshot requested: {Path}");
            }
            catch (Exception exception)
            {
                RenderLog.Error($"Stock screenshot failed: {exception}");
            }
        }, null, TimeSpan.FromSeconds(DelaySeconds), Timeout.InfiniteTimeSpan);
        GC.KeepAlive(timer);
    }
}
