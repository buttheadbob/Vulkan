using System;

// Stand-ins for the plugin types the linked Vulkan sources use

namespace ClientPlugin
{
    internal static class Plugin
    {
        public const string Name = "Vulkan";
    }
}

namespace ClientPlugin.Render
{
    internal static class RenderLog
    {
        public static void Info(string message) => Console.WriteLine("[Info] " + message);
        public static void Warning(string message) => Console.WriteLine("[Warning] " + message);
        public static void Error(string message) => Console.WriteLine("[Error] " + message);
        public static void Error(string message, Exception exception) => Error($"{message}\n{exception}");
    }
}
