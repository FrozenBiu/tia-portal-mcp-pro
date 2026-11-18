using Microsoft.Win32;
using TiaMcpServer.Diagnostics;

namespace TiaMcpServer.Worker;


public sealed record TiaPortalCandidate(
    string Path,
    string InstallationPath,
    string Source,
    bool InstallationPresent,
    bool DirectoryPresent,
    bool AssembliesPresent,
    IReadOnlyList<string> MissingAssemblies);

public sealed record TiaPortalInstallationResult(
    bool Found,
    string? SelectedPath,
    string? SelectedInstallationPath,
    string? Source,
    bool OpennessFound,
    string? SelectedOpennessPath,
    string? OpennessSource,
    IReadOnlyList<TiaPortalCandidate> Candidates);

/// <summary>
/// Locates the TIA Portal V21 Openness API folder from the host (.NET 10) process.
/// Mirrors the precedence of <c>TiaMcpServer.OpennessWorker.Openness.Infrastructure.AssemblyResolver</c>
/// (which lives in the net48 worker and cannot be referenced here):
/// 1. <c>TiaPortalV21Dir</c> environment variable (used as-is, points at the net48 API folder).
/// 2. <c>TiaPortalLocation</c> environment variable (Portal root; <c>PublicAPI\V21\net48</c> appended).
/// 3. <c>TIA_PORTAL_ROOT</c> or the Siemens V21 registry <c>Path</c> (64- then 32-bit view).
/// 4. The legacy Siemens registry <c>INSTALLPATH</c>, then Program Files.
/// The first existing directory is selected as the installation. Assembly completeness is
/// retained separately so the installation and Openness-assembly diagnostics stay distinct.
/// </summary>
public static class TiaPortalInstallationLocator
{
    public static readonly string[] RequiredAssemblies =
    {
        "Siemens.Engineering.Base.dll",
        "Siemens.Engineering.Step7.dll"
    };

    private const string TiaPortalV21DirEnvironmentVariable = "TiaPortalV21Dir";
    private const string TiaPortalLocationEnvironmentVariable = "TiaPortalLocation";
    private const string TiaPortalRootEnvironmentVariable = "TIA_PORTAL_ROOT";
    private const string TiaPortalV21RegistrySubKey =
        @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP21\Global";
    private const string LegacyTiaPortalV21RegistrySubKey =
        @"SOFTWARE\Siemens\Automation\InstalledApps\Totally Integrated Automation Portal V21";
    private const string PublicApiSuffix = @"PublicAPI\V21\net48";

    public static TiaPortalInstallationResult Locate(
        IEnvironmentVariableService env,
        IRegistryService registry,
        IFileSystemService fileSystem)
    {
        var candidates = new List<TiaPortalCandidate>();

        var envV21Dir = env.Get(TiaPortalV21DirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envV21Dir))
        {
            var path = envV21Dir!.Trim().Trim('"');
            AddCandidate(candidates, fileSystem, path, path, $"env:{TiaPortalV21DirEnvironmentVariable}");
        }

        var envLocation = env.Get(TiaPortalLocationEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envLocation))
        {
            var root = envLocation!.Trim().Trim('"');
            var path = Path.Combine(root, PublicApiSuffix);
            AddCandidate(candidates, fileSystem, root, path, $"env:{TiaPortalLocationEnvironmentVariable}");
        }

        var envRoot = env.Get(TiaPortalRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            var root = envRoot!.Trim().Trim('"');
            AddCandidate(candidates, fileSystem, root, Path.Combine(root, PublicApiSuffix), $"env:{TiaPortalRootEnvironmentVariable}");
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var (key, valueName) in new[]
                         {
                             (TiaPortalV21RegistrySubKey, "Path"),
                             (LegacyTiaPortalV21RegistrySubKey, "INSTALLPATH")
                         })
                {
                    var installPath = registry.GetStringValue(RegistryHive.LocalMachine, view, key, valueName);
                    if (!string.IsNullOrWhiteSpace(installPath))
                    {
                        var root = installPath!.Trim().Trim('"');
                        AddCandidate(candidates, fileSystem, root, Path.Combine(root, PublicApiSuffix), $"registry:{view}:{valueName}");
                    }
                }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var root = Path.Combine(programFiles, "Siemens", "Automation", "Portal V21");
            AddCandidate(candidates, fileSystem, root, Path.Combine(root, PublicApiSuffix), "default:ProgramFiles");
        }

        var selected = candidates.FirstOrDefault(c => c.InstallationPresent);
        var selectedOpenness = candidates.FirstOrDefault(c => c.AssembliesPresent);
        return new TiaPortalInstallationResult(
            selected is not null,
            selected?.Path,
            selected?.InstallationPath,
            selected?.Source,
            selectedOpenness is not null,
            selectedOpenness?.Path,
            selectedOpenness?.Source,
            candidates);
    }

    private static void AddCandidate(
        List<TiaPortalCandidate> candidates,
        IFileSystemService fileSystem,
        string installationPath,
        string apiPath,
        string source)
    {
        var missing = new List<string>();
        var installationPresent = fileSystem.DirectoryExists(installationPath);
        var directoryPresent = fileSystem.DirectoryExists(apiPath);
        var assembliesPresent = directoryPresent;
        if (directoryPresent)
        {
            foreach (var assembly in RequiredAssemblies)
            {
                if (!fileSystem.FileExists(Path.Combine(apiPath, assembly)))
                {
                    missing.Add(assembly);
                }
            }
            assembliesPresent = missing.Count == 0;
        }
        else
        {
            missing.AddRange(RequiredAssemblies);
        }

        candidates.Add(new TiaPortalCandidate(
            apiPath,
            installationPath,
            source,
            installationPresent,
            directoryPresent,
            assembliesPresent,
            missing));
    }
}
