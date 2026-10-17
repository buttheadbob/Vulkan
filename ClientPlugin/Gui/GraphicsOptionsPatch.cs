using System.Text;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Gui;

// Adds a "Vulkan Renderer" button to the game's Graphics options screen, opening the plugin's settings.
// It goes into the free spot of the right column, below "Enable damage effects".
// Controls are found by name, so no access to the screen's private fields is needed.
[HarmonyPatch(typeof(MyGuiScreenOptionsGraphics), nameof(MyGuiScreenOptionsGraphics.RecreateControls))]
internal static class GraphicsOptionsPatch
{
    // Row height of the stock screen (vector4 in MyGuiScreenOptionsGraphics.RecreateControls)
    private const float RowHeight = 0.045f;

    private static void Postfix(MyGuiScreenOptionsGraphics __instance, bool constructor)
    {
        if (!constructor)
            return;

        var presets = __instance.Controls.GetControlByName("GraphicsPreset");
        var damageEffects = __instance.Controls.GetControlByName("EnableDamageEffects");
        if (presets == null || damageEffects == null)
            return; // Layout changed by a game update or another plugin; the settings stay reachable from Pulsar

        var button = new MyGuiControlButton(
            position: new Vector2(presets.Position.X, damageEffects.Position.Y + RowHeight),
            visualStyle: MyGuiControlButtonStyleEnum.Default,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP,
            toolTip: "Vulkan renderer settings and GPU information",
            text: new StringBuilder("Vulkan Renderer"),
            onButtonClick: _ => Plugin.Instance?.OpenConfigDialog())
        {
            Name = "VulkanRenderer",
        };

        __instance.Controls.Add(button);
    }
}
