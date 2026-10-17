using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShaderCompiler;

// Compiles the Slang shader modules listed in Shaders/shaders.json to SPIR-V and writes them into
// ClientPlugin/Render/Shaders/ShaderBytecode.g.cs, so they are compiled into the plugin DLL.
// Each module becomes one SPIR-V module containing all of its [shader(...)] entry points.
//
// Runs the official slangc compiler (pinned release, see Slang.cs), downloaded on first use.
//
// Usage: dotnet run --project Tools/ShaderCompiler [-- --check]
//   --check  Do not write; exit with 1 if the generated file is out of date (for CI / before committing).
internal static class Program
{
    private const string ManifestPath = "Shaders/shaders.json";
    private const string OutputPath = "ClientPlugin/Render/Shaders/ShaderBytecode.g.cs";

    private sealed class Manifest
    {
        public List<ModuleEntry> Modules { get; set; } = [];
    }

    private sealed class ModuleEntry
    {
        public string Name { get; set; }
        public string File { get; set; }
        public List<string> Defines { get; set; } = [];
    }

    private sealed record EntryPoint(string Name, string Stage);

    private sealed record CompiledModule(ModuleEntry Module, byte[] Spirv, List<EntryPoint> EntryPoints);

    private static int Main(string[] args)
    {
        var check = args.Contains("--check");
        var root = FindRepositoryRoot();
        var shadersDir = Path.Combine(root, "Shaders");

        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(Path.Combine(root, ManifestPath)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });

        var duplicates = manifest.Modules.GroupBy(s => s.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count != 0)
        {
            Console.Error.WriteLine("Duplicate module names: " + string.Join(", ", duplicates));
            return 1;
        }

        var slangc = Slang.EnsureInstalled(Path.Combine(root, "Tools", "ShaderCompiler", ".slang"));

        var compiled = new List<CompiledModule>();
        var failed = false;
        foreach (var module in manifest.Modules.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var result = Compile(slangc, shadersDir, module, out var messages);
            if (result == null)
            {
                Console.Error.WriteLine($"FAILED {module.Name} ({module.File}):\n{messages}");
                failed = true;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(messages))
                Console.WriteLine($"Messages for {module.Name}:\n{messages}");

            var entries = string.Join(", ", result.EntryPoints.Select(e => $"{e.Name} ({e.Stage})"));
            Console.WriteLine($"{module.Name,-24} {result.Spirv.Length,7} bytes  {entries}");
            compiled.Add(result);
        }

        if (failed)
            return 1;

        var source = Generate(compiled);
        var outputFile = Path.Combine(root, OutputPath);
        var current = File.Exists(outputFile) ? File.ReadAllText(outputFile) : null;

        if (check)
        {
            if (current == source)
            {
                Console.WriteLine("Generated shader code is up to date");
                return 0;
            }

            Console.Error.WriteLine($"{OutputPath} is out of date, run the shader compiler");
            return 1;
        }

        if (current == source)
        {
            Console.WriteLine($"{OutputPath} unchanged");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
        File.WriteAllText(outputFile, source);
        Console.WriteLine($"Wrote {OutputPath}");
        return 0;
    }

    private static CompiledModule Compile(string slangc, string shadersDir, ModuleEntry module, out string messages)
    {
        var output = Path.GetTempFileName();
        var reflection = Path.GetTempFileName();
        var startInfo = new ProcessStartInfo(slangc)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
                 {
                     Path.Combine(shadersDir, module.File),
                     "-target", "spirv",
                     "-profile", "spirv_1_6",              // Vulkan 1.3
                     "-O3",
                     "-I", shadersDir,
                     "-matrix-layout-column-major",        // As uploaded from VRageMath
                     "-fvk-use-scalar-layout",             // C#-like struct layout in buffers (scalar block layout is core in Vulkan 1.2)
                     "-warnings-as-errors", "all",
                     "-warnings-disable", "41012",         // "profile implicitly upgraded": informational, only used capabilities are emitted
                     "-reflection-json", reflection,
                     "-o", output,
                 })
            startInfo.ArgumentList.Add(argument);

        foreach (var define in module.Defines)
            startInfo.ArgumentList.Add("-D" + define);

        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            messages = (stderr + stdout.Result).Trim();

            if (process.ExitCode != 0)
                return null;

            return new CompiledModule(module, File.ReadAllBytes(output), ReadEntryPoints(reflection));
        }
        finally
        {
            File.Delete(output);
            File.Delete(reflection);
        }
    }

    private static List<EntryPoint> ReadEntryPoints(string reflectionFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(reflectionFile));
        return document.RootElement.GetProperty("entryPoints").EnumerateArray()
            .Select(e => new EntryPoint(e.GetProperty("name").GetString(), e.GetProperty("stage").GetString()))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Generate(List<CompiledModule> modules)
    {
        var sb = new StringBuilder();
        sb.Append("""
            // <auto-generated>
            // Generated by Tools/ShaderCompiler from Shaders/shaders.json. Do not edit.
            // Regenerate with: dotnet run --project Tools/ShaderCompiler
            // </auto-generated>

            using System;

            namespace ClientPlugin.Render.Shaders;

            // SPIR-V modules of the shaders, as 32-bit words (vkCreateShaderModule requires 4-byte alignment),
            // and the names of their entry points. ReadOnlySpan<uint> properties over constant arrays are
            // embedded as static data in the DLL.
            internal static class ShaderBytecode
            {

            """);

        foreach (var (module, spirv, entryPoints) in modules)
        {
            if (spirv.Length % 4 != 0)
                throw new InvalidDataException($"SPIR-V of {module.Name} is not a whole number of words");

            var words = MemoryMarshal.Cast<byte, uint>(spirv);
            var hash = Convert.ToHexString(SHA256.HashData(spirv))[..16].ToLowerInvariant();
            var defines = module.Defines.Count == 0 ? "" : " [" + string.Join(" ", module.Defines) + "]";

            sb.Append($"    // {module.File}{defines}, {spirv.Length} bytes, sha256 {hash}\n");
            sb.Append($"    public static class {module.Name}\n    {{\n");

            foreach (var entry in entryPoints)
                sb.Append($"        public const string {ToPascalCase(entry.Name)} = \"{entry.Name}\"; // {entry.Stage}\n");

            sb.Append("\n        public static ReadOnlySpan<uint> Code => new uint[]\n        {\n");
            for (var i = 0; i < words.Length; i += 8)
            {
                sb.Append("            ");
                for (var j = i; j < Math.Min(i + 8, words.Length); j++)
                    sb.Append($"0x{words[j]:x8}, ");
                sb.Length--;
                sb.Append('\n');
            }

            sb.Append("        };\n    }\n\n");
        }

        sb.Length--;
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string ToPascalCase(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Vulkan.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (Vulkan.sln) not found");
    }
}
