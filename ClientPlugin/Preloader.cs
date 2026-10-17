#define HAS_PRELOADER_PATCHES

#if HAS_PRELOADER_PATCHES

using System.Reflection;
using HarmonyLib;

// DO NOT USE A NAMESPACE HERE!
// CRITICAL: Using a namespace here will prevent Pulsar from finding the Preloader class.
// INTENTIONALLY COMMENTED OUT: namespace ClientPlugin;

public class Preloader
{
    // Harmony category of the patches that must be in place before the game creates its renderer.
    // IPlugin.Init runs too late for those: by then the D3D11 device and the window already exist.
    public const string EarlyCategory = "VulkanEarly";

    // No TargetDLLs: this plugin does not rewrite any game assembly with Mono.Cecil,
    // it only uses the hooks below to apply Harmony patches early.

    // Runs right before Space Engineers starts, after the game assembly resolver is set up
    public static void Finish()
    {
        new Harmony(ClientPlugin.Plugin.Name + ".Early").PatchCategory(Assembly.GetExecutingAssembly(), EarlyCategory);
    }
}

#endif
