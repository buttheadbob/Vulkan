using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ShaderCompiler;

// Downloads the pinned official Slang release into a local cache (ignored by git) and returns the path of slangc.
// To update: change the version and URLs, then the hashes (sha256sum of the downloaded archives).
internal static class Slang
{
    private const string Version = "v2026.20";

    private const string LinuxUrl = "https://github.com/shader-slang/slang/releases/download/v2026.20/slang-2026.20-linux-x86_64-glibc-2.28.tar.gz";
    private const string LinuxSha256 = "84ebd777968caceea26b21bfd014341078be6047be6c5a963a69f984f896cebd";

    private const string WindowsUrl = "https://github.com/shader-slang/slang/releases/download/v2026.20/slang-2026.20-windows-x86_64.zip";
    private const string WindowsSha256 = "e6dd32bbddf1720ebb4e679d55e9635ce792865a5bd84f3d2cd9af28647c0174";

    public static string EnsureInstalled(string cacheDir)
    {
        var installDir = Path.Combine(cacheDir, Version);
        var windows = OperatingSystem.IsWindows();
        var executable = Path.Combine(installDir, "bin", windows ? "slangc.exe" : "slangc");

        if (File.Exists(executable))
            return executable;

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The pinned Slang release is only available for x64 here");

        var (url, sha256) = windows ? (WindowsUrl, WindowsSha256) : (LinuxUrl, LinuxSha256);
        Console.WriteLine($"Downloading Slang {Version} from {url}");

        byte[] archive;
        using (var http = new HttpClient())
            archive = http.GetByteArrayAsync(url).GetAwaiter().GetResult();

        var actual = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        if (actual != sha256)
            throw new InvalidDataException($"Slang archive hash mismatch: expected {sha256}, got {actual}");

        var staging = installDir + ".tmp";
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        using (var stream = new MemoryStream(archive))
        {
            if (windows)
            {
                ZipFile.ExtractToDirectory(stream, staging);
            }
            else
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: true);
            }
        }

        if (Directory.Exists(installDir))
            Directory.Delete(installDir, true);
        Directory.Move(staging, installDir);

        if (!windows)
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);

        return executable;
    }
}
