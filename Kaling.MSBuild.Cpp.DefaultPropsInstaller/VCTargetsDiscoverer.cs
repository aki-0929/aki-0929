using System.Diagnostics;
using System.Text.Json;
using System.Xml;

namespace Kaling.MSBuild.Cpp.DefaultPropsInstaller;

public sealed record DiscoveredVCTargets(string PropsPath, string Label);

/// <summary>
/// Finds Visual C++ MSBuild targets (VCTargets) via vswhere and their Microsoft.Cpp.Default.props.
/// </summary>
public static class VCTargetsDiscoverer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<DiscoveredVCTargets> Discover()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<DiscoveredVCTargets>();

        foreach (var instance in ReadVswhereInstances())
        {
            if (string.IsNullOrWhiteSpace(instance.InstallationPath))
                continue;

            var vcTargets = VCTargetsFolder(instance.InstallationVersion);
            if (vcTargets is null)
                continue;

            var props = Path.Combine(
                instance.InstallationPath,
                "MSBuild",
                "Microsoft",
                "VC",
                vcTargets,
                PropsInstaller.TargetFileName);

            if (!File.Exists(props))
                continue;

            var full = Path.GetFullPath(props);
            if (!seen.Add(full))
                continue;

            try
            {
                if (!PropsInstaller.Matches(PropsInstaller.Load(full)))
                    continue;
            }
            catch (Exception ex) when (ex is XmlException or IOException or InvalidDataException)
            {
                continue;
            }

            items.Add(new DiscoveredVCTargets(full, FormatLabel(instance, vcTargets)));
        }

        var duplicates = items
            .GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return items
            .Select(item => duplicates.Contains(item.Label)
                ? item with { Label = $"{item.Label} — {item.PropsPath}" }
                : item)
            .ToList();
    }

    private static IEnumerable<VswhereInstance> ReadVswhereInstances()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");

        if (!File.Exists(vswhere))
            return [];

        var psi = new ProcessStartInfo
        {
            FileName = vswhere,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-all");
        psi.ArgumentList.Add("-prerelease");
        psi.ArgumentList.Add("-products");
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add("-format");
        psi.ArgumentList.Add("json");

        using var process = Process.Start(psi);
        if (process is null)
            return [];

        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5_000))
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            return [];
        }

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            return [];

        try
        {
            return JsonSerializer.Deserialize<VswhereInstance[]>(output, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? VCTargetsFolder(string? installationVersion)
    {
        if (string.IsNullOrWhiteSpace(installationVersion))
            return null;

        var versionText = installationVersion.Split('-', '+')[0];
        if (!Version.TryParse(versionText, out var version))
            return null;

        return $"v{version.Major}0";
    }

    private static string FormatLabel(VswhereInstance instance, string vcTargets)
    {
        if (!string.IsNullOrWhiteSpace(instance.DisplayName))
            return $"{instance.DisplayName} ({vcTargets})";

        return $"{instance.InstallationPath} ({vcTargets})";
    }

    private sealed class VswhereInstance
    {
        public string? InstallationPath { get; set; }
        public string? InstallationVersion { get; set; }
        public string? DisplayName { get; set; }
    }
}