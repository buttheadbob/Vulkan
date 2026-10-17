using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientPlugin.Render.Textures;

namespace VulkanSmokeTest;

// Parses every DDS file below a directory with the plugin's DdsLoader and summarizes formats and failures
internal static class DdsSurvey
{
    public static int Run(string directory)
    {
        var formats = new Dictionary<string, int>();
        var failures = new List<string>();
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*.dds", SearchOption.AllDirectories))
        {
            count++;
            try
            {
                var texture = DdsLoader.Load(File.ReadAllBytes(file), file);
                var key = texture.Format + (texture.IsCube ? " (cube)" : texture.ArrayLayers > 1 ? " (array)" : "");
                formats[key] = formats.GetValueOrDefault(key) + 1;

                var expected = texture.Regions[^1].bufferOffset;
                if (expected > (ulong)texture.Data.Length)
                    failures.Add($"{file}: regions exceed data");
            }
            catch (Exception e)
            {
                failures.Add($"{Path.GetRelativePath(directory, file)}: {e.Message}");
            }
        }

        Console.WriteLine($"{count} DDS files");
        foreach (var (format, n) in formats.OrderByDescending(f => f.Value))
            Console.WriteLine($"  {n,6}  {format}");

        Console.WriteLine($"{failures.Count} failures");
        foreach (var failure in failures.Take(30))
            Console.WriteLine("  " + failure);

        return failures.Count == 0 ? 0 : 1;
    }
}
