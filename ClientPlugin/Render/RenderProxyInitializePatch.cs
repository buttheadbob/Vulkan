using System;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Render;

// Swaps the game's D3D11 renderer for ours. MyProgram.InitializeRender creates MyDX11Render and
// hands it to MyRenderProxy.Initialize, before the window or the render thread exist. MyDX11Render's
// constructor only stores settings, so replacing the instance here leaves no D3D11 state behind.
// If Vulkan cannot run, the stock renderer is left in place.
[HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.Initialize))]
[HarmonyPatchCategory(Preloader.EarlyCategory)]
internal static class RenderProxyInitializePatch
{
    private static void Prefix(ref IMyRender render)
    {
        if (render is not MyDX11Render)
            return; // Null renderer (dedicated server, -nullrender) or another plugin's renderer

        try
        {
            if (Config.Current.Renderer != RendererKind.Vulkan)
            {
                RenderLog.Info("DirectX 11 selected in the plugin settings, using the stock renderer");
                RendererStatus.SetFallback(null);
                return;
            }

            var vulkan = VulkanRender.TryCreate(MyRenderProxy.Settings, out var reason);
            if (vulkan == null)
            {
                RenderLog.Warning($"Vulkan renderer unavailable, falling back to DirectX 11: {reason}");
                RendererStatus.SetFallback(reason);
                return;
            }

            render = vulkan;
            RendererStatus.SetVulkanInstalled();
            RenderLog.Info("Vulkan renderer installed");
        }
        catch (Exception e)
        {
            RenderLog.Error("Vulkan renderer failed to initialize, falling back to DirectX 11", e);
            RendererStatus.SetFallback(e.Message);
        }
    }
}
