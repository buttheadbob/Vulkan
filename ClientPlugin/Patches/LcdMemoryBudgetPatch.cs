using System.Reflection;
using ClientPlugin.Render;
using HarmonyLib;
using Sandbox.Game.SessionComponents;

namespace ClientPlugin.Patches;

// The game computes the LCD render budget from VRage's platform renderer. The Linux platform
// implementation reports no budget, so MySessionComponentPanels never puts any panel in its
// visible budget and every working LCD stays on the "Online" default texture. Give the session
// component a sane generated-texture budget instead.
[HarmonyPatch(typeof(MySessionComponentPanels), "Init")]
internal static class LcdMemoryBudgetPatch
{
    private const ulong Budget = 1UL << 30; // 1 GiB

    private static void Postfix(MySessionComponentPanels __instance)
    {
        var field = typeof(MySessionComponentPanels).GetField("m_memoryBudget",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            RenderLog.Warning("LCD budget: m_memoryBudget field not found");
            return;
        }

        field.SetValue(__instance, Budget);
    }
}
