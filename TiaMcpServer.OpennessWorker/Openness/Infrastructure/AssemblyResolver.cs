using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TiaMcpServer.OpennessWorker.Openness.Infrastructure;

public static class AssemblyResolver
{
    private const string TiaPortalV21DirEnvironmentVariable = "TiaPortalV21Dir";
    private const string TiaPortalLocationEnvironmentVariable = "TiaPortalLocation";
    private const string TiaPortalRootEnvironmentVariable = "TIA_PORTAL_ROOT";
    private const string TiaPortalV21RegistrySubKey =
        @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP21\Global";
    private const string LegacyTiaPortalV21RegistrySubKey =
        @"SOFTWARE\Siemens\Automation\InstalledApps\Totally Integrated Automation Portal V21";
    private const string PublicApiSuffix = @"PublicAPI\V21\net48";

    private static readonly string[] RequiredAssemblies =
    {
        "Siemens.Engineering.Base.dll",
        "Siemens.Engineering.Step7.dll"
    };

    public static void Register()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    internal static string ExpandTiaPortalLocation(string tiaPortalRoot)
    {
        var root = tiaPortalRoot.Trim().Trim('"');
        return root.EndsWith(PublicApiSuffix, StringComparison.OrdinalIgnoreCase)
            ? root
            : Path.Combine(root, PublicApiSuffix);
    }

    internal static IEnumerable<string> CreateCandidatePaths(
        string? tiaPortalV21Dir,
        string? tiaPortalLocation,
        string? tiaPortalRoot,
        IEnumerable<string?> registryInstallPaths,
        string defaultPath)
    {
        if (!string.IsNullOrWhiteSpace(tiaPortalV21Dir))
        {
            var path = tiaPortalV21Dir!.Trim().Trim('"');
            yield return path.EndsWith(PublicApiSuffix, StringComparison.OrdinalIgnoreCase)
                ? path
                : ExpandTiaPortalLocation(path);
        }

        if (!string.IsNullOrWhiteSpace(tiaPortalLocation))
        {
            yield return ExpandTiaPortalLocation(tiaPortalLocation!);
        }

        if (!string.IsNullOrWhiteSpace(tiaPortalRoot))
        {
            var root = tiaPortalRoot!.Trim().Trim('"');
            yield return root.EndsWith(PublicApiSuffix, StringComparison.OrdinalIgnoreCase)
                ? root
                : ExpandTiaPortalLocation(root);
        }

        foreach (var registryInstallPath in registryInstallPaths)
        {
            if (!string.IsNullOrWhiteSpace(registryInstallPath))
            {
                yield return ExpandTiaPortalLocation(registryInstallPath!);
            }
        }

        yield return defaultPath;
    }

    internal static string? SelectFirstCompleteCandidate(
        IEnumerable<string> candidates,
        Func<string, bool> isComplete,
        Action<string>? inspected = null)
    {
        foreach (var candidate in candidates)
        {
            inspected?.Invoke(candidate);
            if (isComplete(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name);

        if (requestedName.Name is null ||
            !requestedName.Name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal))
        {
            return null;
        }

        var opennessInstallPath = GetOpennessInstallPath();
        var assemblyPath = Path.Combine(opennessInstallPath, $"{requestedName.Name}.dll");

        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        var loadedAssembly = Assembly.LoadFrom(assemblyPath);

        if (!string.Equals(requestedName.FullName, loadedAssembly.GetName().FullName, StringComparison.Ordinal))
        {
            throw new FileNotFoundException("TIA Portal Openness version does not match the requested assembly.", assemblyPath);
        }

        return loadedAssembly;
    }

    private static string GetOpennessInstallPath()
    {
        var checkedLocations = new List<string>();
        var environmentPath = Environment.GetEnvironmentVariable(TiaPortalV21DirEnvironmentVariable);
        var tiaPortalLocation = Environment.GetEnvironmentVariable(TiaPortalLocationEnvironmentVariable);
        var registryInstallPaths = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetRegistryInstallPaths(checkedLocations)
            : Enumerable.Empty<string?>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var defaultPath = string.IsNullOrWhiteSpace(programFiles)
            ? string.Empty
            : Path.Combine(programFiles, "Siemens", "Automation", "Portal V21", PublicApiSuffix);
        var candidates = CreateCandidatePaths(
            environmentPath,
            tiaPortalLocation,
            Environment.GetEnvironmentVariable(TiaPortalRootEnvironmentVariable),
            registryInstallPaths,
            defaultPath);
        var selected = SelectFirstCompleteCandidate(
            candidates,
            ContainsRequiredAssemblies,
            path => checkedLocations.Add(path));

        if (selected is not null)
        {
            return selected;
        }

        throw new FileNotFoundException(
            "TIA Portal V21 Openness assemblies were not found. Checked locations: " +
            string.Join("; ", checkedLocations) +
            $". Set {TiaPortalV21DirEnvironmentVariable} to the folder containing Siemens.Engineering.*.dll files.");
    }

    private static IEnumerable<string?> GetRegistryInstallPaths(List<string> checkedLocations)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        foreach (var (subKey, valueName) in new[]
                 {
                     (TiaPortalV21RegistrySubKey, "Path"),
                     (LegacyTiaPortalV21RegistrySubKey, "INSTALLPATH")
                 })
        {
            foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var registryPath = $@"HKLM\{subKey} ({registryView})";
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
                using var key = baseKey.OpenSubKey(subKey);
                if (key is null)
                {
                    checkedLocations.Add($"{registryPath}: key not found");
                    continue;
                }

                var installPath = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(installPath))
                {
                    checkedLocations.Add($"{registryPath}: {valueName} not set");
                    continue;
                }

                yield return installPath;
            }
        }
    }

    private static bool ContainsRequiredAssemblies(string directoryPath)
    {
        return Directory.Exists(directoryPath) &&
            RequiredAssemblies.All(assemblyName => File.Exists(Path.Combine(directoryPath, assemblyName)));
    }
}
