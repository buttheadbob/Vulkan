using System;
using VRage.Utils;

namespace ClientPlugin.Render;

// Writes into the game log (SpaceEngineers_*.log) with a common prefix,
// so renderer messages are easy to find in bug reports.
internal static class RenderLog
{
    private const string Prefix = "[Vulkan] ";

    public static void Info(string message) => MyLog.Default?.WriteLineAndConsole(Prefix + message);

    public static void Warning(string message) => MyLog.Default?.Warning(Prefix + message);

    public static void Error(string message) => MyLog.Default?.Error(Prefix + message);

    public static void Error(string message, Exception exception) => Error($"{message}\n{exception}");
}
