using System;
using System.Diagnostics;
using System.Reflection;
using ClientPlugin.Render;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game.GUI;

namespace ClientPlugin.Testing;

// Development/test helper: VULKAN_AUTOLOAD=<seconds> makes the main menu press "Continue Game"
// on its own after the delay, so frame-capture tests need no user interaction. Inactive unless set.
[HarmonyPatch(typeof(MyGuiScreenMainMenu), nameof(MyGuiScreenMainMenu.HandleInput))]
internal static class AutoLoadMainMenuPatch
{
    private static readonly double DelaySeconds = ParseDelay();
    private static readonly FieldInfo ContinueField =
        typeof(MyGuiScreenMainMenu).GetField("m_continueButton", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo ContinueMethod =
        typeof(MyGuiScreenMainMenu).GetMethod("OnContinueGameClicked", BindingFlags.NonPublic | BindingFlags.Instance);

    private static long startTimestamp;
    private static bool fired;

    private static double ParseDelay()
    {
        var value = Environment.GetEnvironmentVariable("VULKAN_AUTOLOAD");
        return double.TryParse(value, out var seconds) && seconds > 0 ? seconds : 0;
    }

    private static void Postfix(MyGuiScreenMainMenu __instance)
    {
        if (DelaySeconds <= 0 || fired)
            return;

        if (startTimestamp == 0)
        {
            startTimestamp = Stopwatch.GetTimestamp();
            RenderLog.Info($"AutoLoad: main menu will continue the last game in {DelaySeconds:0.#} s");
            return;
        }

        var elapsed = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
        if (elapsed < DelaySeconds)
            return;

        if (ContinueField?.GetValue(__instance) is not MyGuiControlButton button || !button.Visible || !button.Enabled)
            return;

        if (ContinueMethod == null)
        {
            RenderLog.Warning("AutoLoad: OnContinueGameClicked not found");
            fired = true;
            return;
        }

        fired = true;
        RenderLog.Info($"AutoLoad: continuing the last game after {elapsed:0.#} s");
        ContinueMethod.Invoke(__instance, [button]);
    }
}
