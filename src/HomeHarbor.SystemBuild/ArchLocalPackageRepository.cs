namespace HomeHarbor.Tooling;

public sealed record ArchLocalPackageRepository(string PackageDirectory, string DatabasePath, string PacmanConfigPath);

public static class ArchLocalPackageRepositoryBuilder
{
    public const string RepositoryName = "homeharbor-local";

    public static async Task<ArchLocalPackageRepository> CreateAsync(
        string packageDirectory,
        string configDirectory,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        var packages = Directory.GetFiles(packageDirectory, "*.pkg.tar.*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException("cannot create an empty HomeHarbor package repository");
        }

        foreach (var existing in Directory.GetFiles(packageDirectory, RepositoryName + ".*").Order(StringComparer.Ordinal))
        {
            File.Delete(existing);
        }

        var databasePath = Path.Combine(packageDirectory, RepositoryName + ".db.tar.gz");
        var result = await runner.RunAsync(
            "repo-add",
            [databasePath, .. packages],
            new CommandRunOptions(WorkingDirectory: packageDirectory, StreamOutput: true, StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess("could not create the HomeHarbor local package repository");
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException("repo-add did not create " + databasePath);
        }

        _ = Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "pacman.conf");
        await WritePacmanConfigAsync(configPath, packageDirectory, cancellationToken);
        return new ArchLocalPackageRepository(packageDirectory, databasePath, configPath);
    }

    public static Task WriteBootstrapPacmanConfigAsync(string path, CancellationToken cancellationToken = default)
        => WritePacmanConfigAsync(path, null, cancellationToken);

    internal static async Task WritePacmanConfigAsync(
        string path,
        string? packageDirectory,
        CancellationToken cancellationToken = default)
    {
        var localRepository = string.IsNullOrWhiteSpace(packageDirectory)
            ? string.Empty
            : $"""

                [{RepositoryName}]
                SigLevel = Optional TrustAll
                Server = {new Uri(Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar).AbsoluteUri}
                """;
        var config = $"""
            [options]
            Architecture = auto
            CheckSpace
            Color
            ParallelDownloads = 5
            SigLevel = Required DatabaseOptional
            LocalFileSigLevel = Optional
            {localRepository}

            [core]
            Include = /etc/pacman.d/mirrorlist

            [extra]
            Include = /etc/pacman.d/mirrorlist

            [multilib]
            Include = /etc/pacman.d/mirrorlist
            """;
        if (config.Contains("archlinuxhardened", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HomeHarbor pacman configuration must not use archlinuxhardened binaries");
        }

        await FileWrites.AtomicWriteTextAsync(path, config + "\n", 0644, cancellationToken);
    }
}
