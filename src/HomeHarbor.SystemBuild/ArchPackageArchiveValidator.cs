namespace HomeHarbor.Tooling;

internal sealed record ArchPackageMetadata(string Name, string Version);

internal static class ArchPackageArchiveValidator
{
    private static readonly string[] RequiredHomeHarborPackages =
    [
        "homeharbor-control",
        "homeharbor-recovery",
        "homeharbor-installer"
    ];

    internal static async Task ValidateHomeHarborPackagesAsync(
        string packageDirectory,
        string version,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        await ValidatePackagesAsync(
            packageDirectory,
            version,
            RequiredHomeHarborPackages,
            runner,
            cancellationToken);
    }

    internal static async Task ValidatePackagesAsync(
        string packageDirectory,
        string version,
        IReadOnlyCollection<string> requiredPackages,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requiredPackages);
        if (requiredPackages.Count == 0 || requiredPackages.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("required local package names must not be empty");
        }

        var metadata = new List<ArchPackageMetadata>();
        foreach (var archive in Directory.GetFiles(packageDirectory, "*.pkg.tar.*", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var result = await runner.RunAsync(
                "bsdtar",
                ["-xOf", archive, ".PKGINFO"],
                cancellationToken: cancellationToken);
            _ = result.EnsureSuccess("could not inspect package archive " + archive);
            metadata.Add(ParsePackageInfo(result.Stdout, archive));
        }

        ValidateMetadata(metadata, version, requiredPackages);
    }

    internal static ArchPackageMetadata ParsePackageInfo(string packageInfo, string label)
    {
        var values = packageInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal);
        if (!values.TryGetValue("pkgname", out var name) || string.IsNullOrWhiteSpace(name) ||
            !values.TryGetValue("pkgver", out var packageVersion) || string.IsNullOrWhiteSpace(packageVersion))
        {
            throw new InvalidOperationException("package archive has incomplete .PKGINFO: " + label);
        }

        return new ArchPackageMetadata(name, packageVersion);
    }

    internal static void ValidateHomeHarborMetadata(
        IEnumerable<ArchPackageMetadata> metadata,
        string version)
        => ValidateMetadata(metadata, version, RequiredHomeHarborPackages);

    internal static void ValidateMetadata(
        IEnumerable<ArchPackageMetadata> metadata,
        string version,
        IReadOnlyCollection<string> requiredPackages)
    {
        var expectedVersion = version.Replace('-', '_') + "-1";
        var packages = metadata
            .Where(package => requiredPackages.Contains(package.Name, StringComparer.Ordinal))
            .GroupBy(package => package.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var required in requiredPackages)
        {
            if (!packages.TryGetValue(required, out var matches) || matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"local package set must contain exactly one {required} archive");
            }

            if (!string.Equals(matches[0].Version, expectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{required} was built as {matches[0].Version}, expected {expectedVersion}");
            }
        }
    }
}
