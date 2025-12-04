using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace NightmareXIVNugetUploader;

internal class Program
{
    static HttpClient Client = new();
    static bool NoDownload = false;
    static string Key = null!;
    static void Main(string[] args)
    {
        try 
        {
            //
        }
        catch(Exception){}
        if(string.IsNullOrEmpty(Key))
        {
            Key = Environment.GetEnvironmentVariable("NUGETKEY")!;
            Console.WriteLine($"Key length: {Key.Length}");
        }
        try
        {
            string csprojPath = null;
            string slnPath = null;
            if(!NoDownload)
            {
                Console.WriteLine("Downloading Dalamud...");

                // Get the GitHub workspace root
                string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")!;

                string repoPath = Path.Combine(workspace, "repo");

                slnPath = Path.Combine(repoPath, "ECommons.sln");
                csprojPath = Path.Combine(repoPath, "ECommons", "ECommons.csproj");


                // Read suffix from PackageKind (null if missing)
                string? packageKind = ExtractPackageKindFromCsproj(csprojPath);

                string? baseVersion = GetBaseVersionFromCsproj(csprojPath);

                Console.Write($"Package kind: {packageKind}, baseVersion: {baseVersion}");

                string dalamudUrlBase = "https://github.com/goatcorp/dalamud-distrib/raw/refs/heads/main/";
                string dalamudUrl = packageKind == null
                    ? $"{dalamudUrlBase}latest.zip"
                    : $"{dalamudUrlBase}{packageKind}/latest.zip";

                using var dalamud = Client.GetStreamAsync(dalamudUrl).Result;
                Console.WriteLine($"Extracting Dalamud from ({dalamudUrl})...");
                ZipFile.ExtractToDirectory(dalamud, "bin_dalamud");
            }
            {
                var csproj = File.ReadAllText(csprojPath);
                csproj = csproj.Replace("$(DalamudLibPath)", Path.Combine("..", "..", "bin_dalamud") + Path.DirectorySeparatorChar);
                File.WriteAllText(csprojPath, csproj);

                Console.WriteLine("Compiling");
                var home = Environment.GetEnvironmentVariable("HOME");
                Process.Start(new ProcessStartInfo()
                {
                    FileName = "dotnet",
                    Arguments = $"publish {slnPath}",
                    UseShellExecute = true,
                })!.WaitForExit();

                var path = Directory.GetFiles(".", "*.nupkg", SearchOption.AllDirectories).First(x => x.EndsWith(".nupkg") && x.Contains("ECommons."));

                if(PackageVersionExistsFromNupkgAsync(path).Result)
                {
                    Console.WriteLine("Version already exists, will not upload");
                    return;
                }

                var sourceUrl = "https://api.nuget.org/v3/index.json";
                var (packageId, version) = GetPackageIdAndVersion(path);
                if(version.EndsWith("-stg", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Uploading stg package as release");
                    string releasePath = StripSuffixFromNupkg(path, version);
                    PushPackage(releasePath, sourceUrl, Key);
                }
                else
                {
                    PushPackage(path, sourceUrl, Key);
                }
                Console.WriteLine("Package uploaded successfully.");
            }
        }
        catch(Exception e)
        {
            Console.WriteLine(e.ToString());
            Environment.Exit(1);
        }
        Console.WriteLine("Completed");
        //Console.ReadLine();
    }

    static string StripSuffixFromNupkg(string nupkgPath, string fullVersion)
    {
        string baseVersion = fullVersion.Split('-')[0];
        string newPath = Path.Combine(Path.GetDirectoryName(nupkgPath)!, Path.GetFileName(nupkgPath)!.Replace(fullVersion, baseVersion));

        using var original = new PackageArchiveReader(nupkgPath);
        using var newStream = File.Create(newPath);
        using var zip = new ZipArchive(newStream, ZipArchiveMode.Create);

        foreach(var file in original.GetFiles())
        {
            var entryStream = original.GetStream(file);
            var entryData = new MemoryStream();
            entryStream.CopyTo(entryData);
            entryData.Position = 0;

            if(file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(entryData, leaveOpen: true);
                string content = reader.ReadToEnd();
                content = content.Replace($"<version>{fullVersion}</version>", $"<version>{baseVersion}</version>");
                entryData = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            }

            var entry = zip.CreateEntry(file);
            using var newEntryStream = entry.Open();
            entryData.CopyTo(newEntryStream);
        }

        return newPath;
    }

    public static void PushPackage(string packagePath, string sourceUrl, string apiKey)
    {
        var logger = NullLogger.Instance;
        var providers = NuGet.Protocol.Core.Types.Repository.Provider.GetCoreV3();
        var sourceRepository = new SourceRepository(new PackageSource(sourceUrl), providers);

        var packageUpdateResource = sourceRepository.GetResourceAsync<PackageUpdateResource>().Result;
        var symbolPackageUpdateResource = sourceRepository.GetResourceAsync<SymbolPackageUpdateResourceV3>().Result;

        packageUpdateResource.Push(
            [packagePath],
            symbolSource: null,
            timeoutInSecond: 300,
            disableBuffering: false,
            getApiKey: _ => apiKey,
            getSymbolApiKey: _ => null,
            noServiceEndpoint: false,
            skipDuplicate: false,
            symbolPackageUpdateResource,
            logger).Wait();
    }

    static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach(var subDir in dir.GetDirectories())
            SetAttributesNormal(subDir);
        foreach(var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
    public static (string PackageId, string Version) GetPackageIdAndVersion(string nupkgPath)
    {
        if(!File.Exists(nupkgPath))
            throw new FileNotFoundException($"File not found: {nupkgPath}");

        using var packageReader = new PackageArchiveReader(nupkgPath);
        PackageIdentity identity = packageReader.GetIdentity();

        return (identity.Id, identity.Version.ToString());
    }

    /// <summary>
    /// Checks whether a specific version of a package exists on the NuGet feed.
    /// </summary>
    public static async Task<bool> PackageVersionExistsAsync(string packageId, string version, string sourceUrl = "https://api.nuget.org/v3/index.json")
    {
        try
        {
            string packageIdLower = packageId.ToLowerInvariant();

            // Get NuGet service index
            string indexUrl = sourceUrl;
            if(!indexUrl.EndsWith("/index.json"))
                indexUrl = sourceUrl.TrimEnd('/') + "/index.json";

            string json = await Client.GetStringAsync(indexUrl);
            using JsonDocument doc = JsonDocument.Parse(json);

            string packageBaseAddress = null;
            foreach(var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
            {
                if(resource.GetProperty("@type").GetString() == "PackageBaseAddress/3.0.0")
                {
                    packageBaseAddress = resource.GetProperty("@id").GetString();
                    break;
                }
            }

            if(packageBaseAddress == null)
                throw new Exception("PackageBaseAddress not found in NuGet index.");

            // Check for existing versions
            string versionsUrl = $"{packageBaseAddress}{packageIdLower}/index.json";
            string versionsJson = await Client.GetStringAsync(versionsUrl);
            using JsonDocument versionsDoc = JsonDocument.Parse(versionsJson);

            foreach(var v in versionsDoc.RootElement.GetProperty("versions").EnumerateArray())
            {
                if(string.Equals(v.GetString(), version, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch(HttpRequestException ex) when(ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Package not found at all
            return false;
        }
        catch(Exception ex)
        {
            Console.WriteLine($"Error checking package version: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Combines extraction and version existence check from a .nupkg file.
    /// </summary>
    public static async Task<bool> PackageVersionExistsFromNupkgAsync(string nupkgPath, string sourceUrl = "https://api.nuget.org/v3/index.json")
    {
        var (packageId, version) = GetPackageIdAndVersion(nupkgPath);

        string baseVersion = version.Split('-')[0];

        // Get all versions of this package from the feed
        var allVersions = await GetAllPackageVersionsAsync(packageId, sourceUrl);

        // Check if any version starts with the same base version
        foreach(var v in allVersions)
        {
            if(v.StartsWith(baseVersion + "-", StringComparison.OrdinalIgnoreCase) || v.Equals(baseVersion, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"A version with base '{baseVersion}' already exists on NuGet: {v}");
                return true;
            }
        }

        return false;
    }

    public static async Task<List<string>> GetAllPackageVersionsAsync(string packageId, string sourceUrl = "https://api.nuget.org/v3/index.json")
    {
        string packageIdLower = packageId.ToLowerInvariant();

        if(!sourceUrl.EndsWith("/index.json"))
            sourceUrl = sourceUrl.TrimEnd('/') + "/index.json";

        string json = await Client.GetStringAsync(sourceUrl);
        using JsonDocument doc = JsonDocument.Parse(json);

        string? packageBaseAddress = null;
        foreach(var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            if(resource.GetProperty("@type").GetString() == "PackageBaseAddress/3.0.0")
            {
                packageBaseAddress = resource.GetProperty("@id").GetString();
                break;
            }
        }

        if(packageBaseAddress == null)
            throw new Exception("PackageBaseAddress not found in NuGet index.");

        string versionsUrl = $"{packageBaseAddress}{packageIdLower}/index.json";
        string versionsJson = await Client.GetStringAsync(versionsUrl);
        using JsonDocument versionsDoc = JsonDocument.Parse(versionsJson);

        return versionsDoc.RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .ToList();
    }

    static string? GetPackageSuffixFromNupkg(string releaseFolderPath)
    {
        var nupkgPath = Directory.GetFiles(releaseFolderPath).FirstOrDefault(x => x.EndsWith(".nupkg") && x.Contains("ECommons."));
        if(nupkgPath == null)
        {
            throw new FileNotFoundException("Could not find ECommons .nupkg in Release folder.");
        }

        var (_, version) = GetPackageIdAndVersion(nupkgPath);
        var dashIndex = version.IndexOf('-');
        if(dashIndex >= 0 && dashIndex < version.Length - 1)
        {
            return version.Substring(dashIndex + 1);
        }

        return null;
    }

    static string? GetBaseVersionFromCsproj(string csprojPath)
    {
        var csprojText = File.ReadAllText(csprojPath);
        var startTag = "<BaseVersion>";
        var endTag = "</BaseVersion>";
        var startIndex = csprojText.IndexOf(startTag);
        if(startIndex == -1)
            return null;

        var endIndex = csprojText.IndexOf(endTag, startIndex);
        if(endIndex == -1)
            return null;

        int valueStart = startIndex + startTag.Length;
        return csprojText[valueStart..endIndex].Trim();
    }

    static string? ExtractPackageKindFromCsproj(string csprojPath)
    {
        var csprojText = File.ReadAllText(csprojPath);
        var startTag = "<PackageKind>";
        var endTag = "</PackageKind>";
        var startIndex = csprojText.IndexOf(startTag);
        if(startIndex == -1)
            return null;

        var endIndex = csprojText.IndexOf(endTag, startIndex);
        if(endIndex == -1)
            return null;

        int valueStart = startIndex + startTag.Length;
        return csprojText[valueStart..endIndex].Trim();
    }

    public static Dictionary<string, string> ParseConfigFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach(var line in File.ReadAllLines(path))
        {
            if(string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int idx = line.IndexOf('=');
            if(idx <= 0)
            {
                continue;
            }

            string key = line.Substring(0, idx).Trim();
            string value = line.Substring(idx + 1).Trim();

            dict[key] = value;
        }

        return dict;
    }
}
