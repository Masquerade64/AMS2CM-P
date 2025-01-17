﻿using System.Diagnostics;
using Core.Mods;
using Newtonsoft.Json;
using SevenZipExtractor;

namespace Core;

public record InstallPaths(
    string ModArchivesPath,
    string GamePath,
    string TempPath,
    string InstalledFilesPath
);

public class ModManager
{
    private const string Ams2SteamId = "1066890";
    private const string Ams2ProcessName = "AMS2AVX";
    private static readonly string Ams2InstallationDir = Path.Combine("steamapps", "common", "Automobilista 2");
    private static readonly string FileRemovedByBootfiles = Path.Combine("Pakfiles", "PHYSICSPERSISTENT.bff");

    private const string ModsSubdir = "Mods";
    private const string EnabledModsSubdir = "Enabled";

    private static readonly JsonSerializerSettings JsonSerializerSettings = new() { Formatting = Formatting.Indented };

    private readonly InstallPaths _installPaths;

    public static ModManager Init()
    {
        var ams2InstallationDirectory = File.ReadAllText("gamepath.txt");
        var modsDir = Path.Combine(ams2InstallationDirectory, ModsSubdir);
        var installPaths = new InstallPaths(
            ModArchivesPath: Path.Combine(modsDir, EnabledModsSubdir),
            GamePath: ams2InstallationDirectory,
            TempPath: Path.Combine(modsDir, "Temp", Guid.NewGuid().ToString()),
            InstalledFilesPath: Path.Combine(modsDir, "installed.json")
        );

        return new ModManager(installPaths);
    }

    private ModManager(InstallPaths installPaths)
    {
        _installPaths = installPaths;
    }

    public void InstallEnabledMods()
    {
        CheckGameNotRunning();
        RestoreOriginalState();
        InstallAllModFiles();
        Cleanup();
    }

    private void Cleanup()
    {
        if (Directory.Exists(_installPaths.TempPath))
        {
            Directory.Delete(_installPaths.TempPath, recursive: true);
        }
    }

    private void RestoreOriginalState()
    {
        var previouslyInstalledFiles = ReadPreviouslyInstalledFiles();
        if (previouslyInstalledFiles.Any())
        {
            Console.WriteLine($"Uninstalling mods:");
            foreach (var (modName, filePaths) in previouslyInstalledFiles)
            {
                Console.WriteLine($"- {modName}");
                JsgmeFileInstaller.RestoreOriginalState(_installPaths.GamePath, filePaths);
            }
        }
        else
        {
            CheckNoBootfilesInstalled();
            Console.WriteLine("No previously installed mods found. Skipping uninstall phase.");
        }
    }

    private static void CheckGameNotRunning()
    {
        if (Process.GetProcesses().Any(_ => _.ProcessName == Ams2ProcessName))
        {
            throw new Exception("The game is running.");
        }
    }

    private void CheckNoBootfilesInstalled()
    {
        if (!File.Exists(Path.Combine(_installPaths.GamePath, FileRemovedByBootfiles)))
        {
            throw new Exception("Bootfiles installed by another tool (e.g. JSGME) have been detected. Please uninstall all mods.");
        }
    }

    private void InstallAllModFiles()
    {
        if (!Directory.Exists(_installPaths.ModArchivesPath))
        {
            Console.WriteLine($"No mod archives found in {_installPaths.ModArchivesPath}");
            return;
        }
        var modConfigs = new List<IMod.ConfigEntries>();
        var modArchives = Directory.EnumerateFiles(_installPaths.ModArchivesPath).ToList();
        var installedFilesByMod = new Dictionary<string, IReadOnlyCollection<string>>();
        if (!modArchives.Any())
        {
            Console.WriteLine($"No mod archives found in {_installPaths.ModArchivesPath}");
        }
        else
        {
            Console.WriteLine("Installing mods:");
            foreach (var filePath in modArchives)
            {
                var packageName = Path.GetFileNameWithoutExtension(filePath);

                Console.WriteLine($"- {packageName}");

                var extractionDir = Path.Combine(_installPaths.TempPath, packageName);
                using var archiveFile = new ArchiveFile(filePath);
                archiveFile.Extract(extractionDir);
            
                var mod = new ManualInstallMod(packageName, extractionDir);
                try
                {
                    mod.Install(_installPaths.GamePath);
                    modConfigs.Add(mod.Config);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  Error: {e.Message}");
                }
                // Add even partially installed files
                installedFilesByMod.Add(mod.PackageName, mod.InstalledFiles);
            }
        }

        WriteInstalledFiles(installedFilesByMod);

        if (!modConfigs.Any())
        {
            return;
        }
        Console.WriteLine("Post-processing:");
        Console.WriteLine("- Appending crd file entries");
        PostProcessor.AppendCrdFileEntries(_installPaths.GamePath, modConfigs.SelectMany(_ => _.CrdFileEntries));
        Console.WriteLine("- Appending trd file entries");
        PostProcessor.AppendTrdFileEntries(_installPaths.GamePath, modConfigs.SelectMany(_ => _.TrdFileEntries));
        Console.WriteLine("- Appending driveline records");
        PostProcessor.AppendDrivelineRecords(_installPaths.GamePath, modConfigs.SelectMany(_ => _.DrivelineRecords));
    }


    private Dictionary<string, IReadOnlyCollection<string>> ReadPreviouslyInstalledFiles() {
        if (!File.Exists(_installPaths.InstalledFilesPath))
        {
            return new Dictionary<string, IReadOnlyCollection<string>>();
        }

        return JsonConvert
            .DeserializeObject<Dictionary<string, IReadOnlyCollection<string>>>(File.ReadAllText(_installPaths.InstalledFilesPath));
    }

    private void WriteInstalledFiles(Dictionary<string, IReadOnlyCollection<string>> filesByMod)
    {
        File.WriteAllText(_installPaths.InstalledFilesPath, JsonConvert.SerializeObject(filesByMod, JsonSerializerSettings));
    }
}