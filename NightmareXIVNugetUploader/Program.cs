using LibGit2Sharp;
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
            if(!NoDownload)
            {
                if(Directory.Exists("repo_ecommons"))
                {
                    SetAttributesNormal(new("repo_ecommons"));
                    Directory.Delete("repo_ecommons", true);
                }
                if(Directory.Exists("bin_dalamud"))
                {
                    SetAttributesNormal(new("bin_dalamud"));
                    Directory.Delete("bin_dalamud", true);
                }
                Console.WriteLine("Downloading ECommons...");
                LibGit2Sharp.Repository.Clone("https://github.com/NightmareXIV/ECommons.git", "repo_ecommons", new CloneOptions()
                {
                    BranchName = "master",
                });
                Console.WriteLine("Downloading Dalamud...");
                using var dalamud = Client.GetStreamAsync("https://github.com/goatcorp/dalamud-distrib/raw/refs/heads/main/latest.zip").Result;
                Console.WriteLine("Extracting Dalamud...");
                ZipFile.ExtractToDirectory(dalamud, "bin_dalamud");
            }
            var slnPath = Path.Combine("repo_ecommons", "ECommons.sln");
            var csprojPath = Path.Combine("repo_ecommons", "ECommons", "ECommons.csproj");
            var csproj = File.ReadAllText(csprojPath);
            csproj = csproj.Replace("$(DalamudLibPath)", Path.Combine("..", "..", "bin_dalamud") + Path.DirectorySeparatorChar);
            File.WriteAllText(csprojPath, csproj);

            Console.WriteLine("Compiling");
            Process.Start(new ProcessStartInfo()
            {
                FileName = "dotnet",
                Arguments = $"publish {slnPath}",
                UseShellExecute = true,
            })!.WaitForExit();

            var path = Directory.GetFiles(Path.Combine("repo_ecommons", "ECommons", "bin", "Release")).First(x => x.EndsWith(".nupkg") && x.Contains("ECommons."));

            if(PackageVersionExistsFromNupkgAsync(path).Result)
            {
                Console.WriteLine("Version already exists, will not upload");
                return;
            }

            var sourceUrl = "https://api.nuget.org/v3/index.json";
            try
            {
                PushPackage(path, sourceUrl, Key);
                Console.WriteLine("Package uploaded successfully.");
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Failed to upload package: {ex.Message}");
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
        return await PackageVersionExistsAsync(packageId, version, sourceUrl);
    }
}
