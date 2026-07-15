using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeHarbor.Tooling;

public sealed record ArchProductPackageSetProvenance(
    int SchemaVersion,
    string ProductId,
    string Version,
    string PackagingSourceSha256,
    IReadOnlyDictionary<string, string> Packages)
{
    public const string FileName = ".arch-ab-package-set.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task WriteAsync(
        string root,
        SystemImageBuildPlan plan,
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        var expectedSource = ComputePackagingSourceSha256(root);
        var packages = await HashPackagesAsync(packageDirectory, cancellationToken);
        if (!string.Equals(expectedSource, ComputePackagingSourceSha256(root), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Arch package sources changed during the package build");
        }

        var provenance = new ArchProductPackageSetProvenance(
            2,
            plan.Product.Id,
            plan.Version,
            expectedSource,
            packages);
        await FileWrites.AtomicWriteTextAsync(
            Path.Combine(Path.GetFullPath(packageDirectory), FileName),
            JsonSerializer.Serialize(provenance, JsonOptions) + "\n",
            0644,
            cancellationToken);
    }

    public static async Task VerifyAsync(
        string root,
        SystemImageBuildPlan plan,
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(packageDirectory);
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("local product package set has no build provenance: " + path);
        }

        var provenance = JsonSerializer.Deserialize<ArchProductPackageSetProvenance>(
            await File.ReadAllTextAsync(path, cancellationToken),
            JsonOptions) ?? throw new InvalidOperationException("local product package provenance is empty: " + path);
        if (provenance.SchemaVersion != 2 ||
            !string.Equals(provenance.ProductId, plan.Product.Id, StringComparison.Ordinal) ||
            !string.Equals(provenance.Version, plan.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"local package set targets {provenance.ProductId} {provenance.Version}, not {plan.Product.Id} {plan.Version}");
        }

        var source = ComputePackagingSourceSha256(root);
        if (!string.Equals(provenance.PackagingSourceSha256, source, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("local package set does not match current Arch packaging sources");
        }

        var packages = await HashPackagesAsync(directory, cancellationToken);
        if (provenance.Packages is null ||
            provenance.Packages.Count != packages.Count ||
            provenance.Packages.Any(pair =>
                !packages.TryGetValue(pair.Key, out var digest) ||
                !string.Equals(pair.Value, digest, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("local package archives changed after the product package build");
        }
    }

    internal static string ComputePackagingSourceSha256(string root)
    {
        var sourceRoot = Path.Combine(Path.GetFullPath(root), "packaging", "arch");
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException("Arch package source directory not found: " + sourceRoot);
        }

        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => IsMaintainedPackagingInput(sourceRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("Arch package source directory has no maintained inputs: " + sourceRoot);
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            digest.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static bool IsMaintainedPackagingInput(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return !segments.Contains("src", StringComparer.Ordinal) &&
            !segments.Contains("pkg", StringComparer.Ordinal) &&
            !segments.Contains(".git", StringComparer.Ordinal) &&
            !path.Contains(".pkg.tar.", StringComparison.Ordinal) &&
            !path.EndsWith(".tar.gz", StringComparison.Ordinal) &&
            !path.EndsWith(".log", StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashPackagesAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        var packages = Directory.GetFiles(packageDirectory, "*.pkg.tar.*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException("local product package set is empty: " + packageDirectory);
        }

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            await using var stream = File.OpenRead(package);
            hashes.Add(
                Path.GetFileName(package),
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)));
        }

        return hashes;
    }
}
