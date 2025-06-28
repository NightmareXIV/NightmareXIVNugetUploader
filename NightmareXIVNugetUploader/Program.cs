using LibGit2Sharp;
using System.Diagnostics;
using System.IO.Compression;

namespace NightmareXIVNugetUploader;

internal class Program
{
    static HttpClient Client = new();
    static bool NoDownload = false;
    static void Main(string[] args)
    {
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
                Repository.Clone("https://github.com/NightmareXIV/ECommons.git", "repo_ecommons", new CloneOptions()
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
        }
        catch(Exception e)
        {
            Console.WriteLine(e.ToString());
        }
        Console.WriteLine("Completed");
        //Console.ReadLine();
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
}
