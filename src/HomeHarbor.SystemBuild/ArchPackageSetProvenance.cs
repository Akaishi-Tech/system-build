using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeHarbor.Tooling;

public sealed record ArchPackageSetProvenance(
    int SchemaVersion,
    string HomeHarborVersion,
    string SelinuxSourceSha256,
    IReadOnlyDictionary<string, string> Packages)
{
    public const string FileName = ".homeharbor-package-set.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task WriteAsync(
        string root,
        string version,
        string packageDirectory,
        CancellationToken cancellationToken = default)
        => await WriteAsync(
            root,
            version,
            packageDirectory,
            ComputeSelinuxSourceSha256(root),
            cancellationToken);

    public static async Task WriteAsync(
        string root,
        string version,
        string packageDirectory,
        string expectedSelinuxSourceSha256,
        CancellationToken cancellationToken = default)
    {
        RequireUnchangedSelinuxSource(root, expectedSelinuxSourceSha256);
        var packages = await HashPackagesAsync(packageDirectory, cancellationToken);
        RequireUnchangedSelinuxSource(root, expectedSelinuxSourceSha256);
        var provenance = new ArchPackageSetProvenance(
            1,
            RequireVersion(version),
            expectedSelinuxSourceSha256,
            packages);
        await FileWrites.AtomicWriteTextAsync(
            Path.Combine(Path.GetFullPath(packageDirectory), FileName),
            JsonSerializer.Serialize(provenance, JsonOptions) + "\n",
            0644,
            cancellationToken);
    }

    public static async Task VerifyAsync(
        string root,
        string version,
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                "the locally built package directory is unavailable; rerun system-build: " + directory);
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "the local package set has no build provenance; rerun system-build: " + path);
        }

        var provenance = JsonSerializer.Deserialize<ArchPackageSetProvenance>(
            await File.ReadAllTextAsync(path, cancellationToken),
            JsonOptions) ?? throw new InvalidOperationException("local package provenance is empty: " + path);
        if (provenance.SchemaVersion != 1)
        {
            throw new InvalidOperationException("unsupported local package provenance schema: " + provenance.SchemaVersion);
        }

        var expectedVersion = RequireVersion(version);
        if (!string.Equals(provenance.HomeHarborVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"local package set was built for {provenance.HomeHarborVersion}, not {expectedVersion}; rerun system-build");
        }

        var currentSource = ComputeSelinuxSourceSha256(root);
        if (!string.Equals(provenance.SelinuxSourceSha256, currentSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "local package set does not match the maintained SELinux PKGBUILD sources; rerun system-build");
        }

        var currentPackages = await HashPackagesAsync(packageDirectory, cancellationToken);
        if (provenance.Packages is null ||
            provenance.Packages.Count != currentPackages.Count ||
            provenance.Packages.Any(pair =>
                !currentPackages.TryGetValue(pair.Key, out var digest) ||
                !string.Equals(pair.Value, digest, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "local package archives have changed since system-build; rerun system-build");
        }
    }

    internal static string ComputeSelinuxSourceSha256(string root)
    {
        var sourceRoot = Path.Combine(Path.GetFullPath(root), "packaging", "arch", "selinux");
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException("SELinux package source directory not found: " + sourceRoot);
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = EnumerateMaintainedFiles(sourceRoot)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("SELinux package source directory is empty: " + sourceRoot);
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = FileTreeCopier.ReadSymbolicLink(
                    file,
                    (attributes & FileAttributes.Directory) != 0)
                    ?? throw new IOException("could not read maintained SELinux source link: " + file);
                digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0L\n"));
                digest.AppendData(Encoding.UTF8.GetBytes(target));
                continue;
            }

            var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(file);
            digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0F" + mode.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\n"));
            digest.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void RequireUnchangedSelinuxSource(string root, string expectedSha256)
    {
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("expected SELinux source SHA-256 must be 64 hexadecimal characters");
        }

        var current = ComputeSelinuxSourceSha256(root);
        if (!string.Equals(expectedSha256, current, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "maintained SELinux package sources changed during the package build; discard the mixed package set and rebuild");
        }
    }

    internal static bool IsMaintainedSource(string sourceRoot, string path)
    {
        var relative = Path.GetRelativePath(sourceRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return !segments.Contains("src", StringComparer.Ordinal) &&
               !segments.Contains("pkg", StringComparer.Ordinal) &&
               !segments.Contains(".git", StringComparer.Ordinal) &&
               !IsInsideBareGitRepository(sourceRoot, path) &&
               !Path.GetFileName(path).Equals(".SRCINFO", StringComparison.Ordinal) &&
               !path.Contains(".pkg.tar.", StringComparison.Ordinal) &&
               !path.EndsWith(".log", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateMaintainedFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!IsMaintainedSource(directory, entry))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                (attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumerateMaintainedFiles(entry))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static bool IsBareGitRepository(string path)
        => Directory.Exists(path) &&
           File.Exists(Path.Combine(path, "HEAD")) &&
           File.Exists(Path.Combine(path, "config")) &&
           Directory.Exists(Path.Combine(path, "objects")) &&
           Directory.Exists(Path.Combine(path, "refs"));

    private static bool IsInsideBareGitRepository(string sourceRoot, string path)
    {
        var root = Path.GetFullPath(sourceRoot);
        var current = Directory.Exists(path)
            ? Path.GetFullPath(path)
            : Path.GetDirectoryName(Path.GetFullPath(path));
        while (current is not null && SecurityGuards.IsInsideDirectory(current, root))
        {
            if (IsBareGitRepository(current))
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashPackagesAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(packageDirectory);
        var packages = Directory.GetFiles(directory, "*.pkg.tar.*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException("local package set is empty: " + directory);
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            await using var stream = File.OpenRead(package);
            result.Add(Path.GetFileName(package), Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)));
        }

        return result;
    }

    private static string RequireVersion(string version)
        => string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException("HomeHarbor package-set version is required")
            : version.Trim();
}
