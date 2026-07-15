using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeHarbor.Tooling;

public sealed record SelinuxDependencyPackageEntry(
    string Name,
    string Version,
    string Architecture,
    string Sha256);

public sealed record SelinuxDependencyPackageSetProvenance(
    int SchemaVersion,
    string TargetArchitecture,
    string DependencyInputSha256,
    IReadOnlyDictionary<string, SelinuxDependencyPackageEntry> Packages)
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = ".homeharbor-selinux-dependencies.json";
    public const string TargetArchitectureName = "x86_64";

    private static readonly string[] BuildContractInputs =
    [
        "Directory.Build.props",
        "global.json",
        "tools/system-build/Directory.Build.props",
        "tools/system-build/src/HomeHarbor.SystemBuild/ArchLocalPackageRepository.cs",
        "tools/system-build/src/HomeHarbor.SystemBuild/ArchPackageSetProvenance.cs",
        "tools/system-build/src/HomeHarbor.SystemBuild/HomeHarbor.SystemBuild.csproj",
        "tools/system-build/src/HomeHarbor.SystemBuild/RootlessBuildExecutor.cs",
        "tools/system-build/src/HomeHarbor.SystemBuild/SelinuxDependencyPackageSetProvenance.cs",
        "tools/system-build/src/HomeHarbor.SystemBuild/SelinuxPackageBuild.cs",
        "tools/system-build/src/HomeHarbor.SystemBuild/SelinuxPackageBuilder.cs",
        "tools/system-build/external/system-utils/src/HomeHarbor.Tooling/FileTreeCopier.cs",
        "tools/system-build/external/system-utils/src/HomeHarbor.Tooling/HomeHarbor.Tooling.csproj",
        "tools/system-build/external/system-utils/src/HomeHarbor.Tooling/ProcessRunner.cs"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string ComputeDependencyInputSha256(string root)
    {
        var repositoryRoot = Path.GetFullPath(root);
        var plan = SelinuxPackageBuildDescriptor.LoadDefaultPlan(repositoryRoot);
        var manifest = SelinuxPackageBuildDescriptor.DefaultManifestPath(repositoryRoot);
        var inputs = new HashSet<string>(StringComparer.Ordinal) { manifest };
        foreach (var recipe in plan.Recipes.Values)
        {
            _ = inputs.Add(recipe.Directory);
            inputs.UnionWith(EnumerateInputs(
                recipe.Directory,
                path => ArchPackageSetProvenance.IsMaintainedSource(recipe.Directory, path)));
        }

        var sharedKeys = Path.Combine(repositoryRoot, "packaging", "arch", "selinux", "keys");
        if (Directory.Exists(sharedKeys))
        {
            _ = inputs.Add(sharedKeys);
            inputs.UnionWith(EnumerateInputs(sharedKeys, _ => true));
        }

        foreach (var relativePath in BuildContractInputs)
        {
            var contractInput = Path.Combine(repositoryRoot, relativePath);
            if (File.Exists(contractInput))
            {
                _ = inputs.Add(contractInput);
            }
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes(
            $"homeharbor-selinux-dependencies\0{CurrentSchemaVersion}\0{TargetArchitectureName}\0"));
        foreach (var input in inputs.Order(StringComparer.Ordinal))
        {
            if (!SecurityGuards.IsInsideDirectory(input, repositoryRoot))
            {
                throw new InvalidOperationException("SELinux dependency input escapes the repository: " + input);
            }

            var relative = Path.GetRelativePath(repositoryRoot, input).Replace(Path.DirectorySeparatorChar, '/');
            var attributes = File.GetAttributes(input);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = FileTreeCopier.ReadSymbolicLink(
                    input,
                    (attributes & FileAttributes.Directory) != 0)
                    ?? throw new IOException("could not read SELinux dependency input link: " + input);
                digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0L\n" + target + "\0"));
                continue;
            }

            var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(input);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                digest.AppendData(Encoding.UTF8.GetBytes(
                    relative + "\0D" + mode.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\n"));
                continue;
            }

            digest.AppendData(Encoding.UTF8.GetBytes(
                relative + "\0F" + mode.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\n"));
            digest.AppendData(SHA256.HashData(File.ReadAllBytes(input)));
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    public static async Task WriteAsync(
        string root,
        string packageDirectory,
        string expectedDependencyInputSha256,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        RequireCurrentInput(root, expectedDependencyInputSha256);
        var packages = await InspectPackagesAsync(root, packageDirectory, runner, cancellationToken);
        RequireCurrentInput(root, expectedDependencyInputSha256);
        var provenance = new SelinuxDependencyPackageSetProvenance(
            CurrentSchemaVersion,
            TargetArchitectureName,
            expectedDependencyInputSha256,
            packages);
        await FileWrites.AtomicWriteTextAsync(
            Path.Combine(Path.GetFullPath(packageDirectory), FileName),
            JsonSerializer.Serialize(provenance, JsonOptions) + "\n",
            0644,
            cancellationToken);
    }

    public static async Task VerifyAsync(
        string root,
        string packageDirectory,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
        => _ = await VerifyAndReadAsync(root, packageDirectory, runner, cancellationToken);

    public static async Task ImportVerifiedAsync(
        string root,
        string sourceDirectory,
        string destinationDirectory,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        if (SecurityGuards.IsInsideDirectory(destination, source) ||
            SecurityGuards.IsInsideDirectory(source, destination))
        {
            throw new InvalidOperationException("SELinux dependency cache source and destination must be separate directories");
        }

        var provenance = await VerifyAndReadAsync(root, source, runner, cancellationToken);
        _ = Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new InvalidOperationException(
                "SELinux dependency package destination must be empty: " + destination);
        }

        foreach (var fileName in provenance.Packages.Keys.Order(StringComparer.Ordinal))
        {
            File.Copy(Path.Combine(source, fileName), Path.Combine(destination, fileName));
        }

        File.Copy(Path.Combine(source, FileName), Path.Combine(destination, FileName));
        await VerifyAsync(root, destination, runner, cancellationToken);
        File.Delete(Path.Combine(destination, FileName));
    }

    private static async Task<SelinuxDependencyPackageSetProvenance> VerifyAndReadAsync(
        string root,
        string packageDirectory,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException("SELinux dependency package cache is unavailable: " + directory);
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("SELinux dependency package cache must not be a symbolic link: " + directory);
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("SELinux dependency package cache has no regular provenance file: " + path);
        }

        var provenance = JsonSerializer.Deserialize<SelinuxDependencyPackageSetProvenance>(
            await File.ReadAllTextAsync(path, cancellationToken),
            JsonOptions) ?? throw new InvalidOperationException("SELinux dependency package provenance is empty: " + path);
        if (provenance.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                "unsupported SELinux dependency package provenance schema: " + provenance.SchemaVersion);
        }

        if (!string.Equals(provenance.TargetArchitecture, TargetArchitectureName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SELinux dependency packages target {provenance.TargetArchitecture}, not {TargetArchitectureName}");
        }

        RequireCurrentInput(root, provenance.DependencyInputSha256);
        var actual = await InspectPackagesAsync(root, directory, runner, cancellationToken);
        RequireCurrentInput(root, provenance.DependencyInputSha256);
        if (provenance.Packages is null ||
            provenance.Packages.Count != actual.Count ||
            provenance.Packages.Any(pair =>
                !actual.TryGetValue(pair.Key, out var entry) ||
                pair.Value is null ||
                !string.Equals(pair.Value.Name, entry.Name, StringComparison.Ordinal) ||
                !string.Equals(pair.Value.Version, entry.Version, StringComparison.Ordinal) ||
                !string.Equals(pair.Value.Architecture, entry.Architecture, StringComparison.Ordinal) ||
                !string.Equals(pair.Value.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "SELinux dependency package cache contents do not match their provenance");
        }

        return provenance;
    }

    private static async Task<IReadOnlyDictionary<string, SelinuxDependencyPackageEntry>> InspectPackagesAsync(
        string root,
        string packageDirectory,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException("SELinux dependency package directory is unavailable: " + directory);
        }

        var archives = new List<string>();
        foreach (var entry in Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if (name == FileName)
            {
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidOperationException("SELinux dependency provenance must be a regular file: " + entry);
                }

                continue;
            }

            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !name.Contains(".pkg.tar.", StringComparison.Ordinal) ||
                name.EndsWith(".sig", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("unexpected entry in SELinux dependency package cache: " + entry);
            }

            archives.Add(entry);
        }

        var plan = SelinuxPackageBuildDescriptor.LoadDefaultPlan(root);
        var expectedNames = plan.Recipes.Values
            .SelectMany(recipe => recipe.Packages)
            .ToHashSet(StringComparer.Ordinal);
        if (archives.Count != expectedNames.Count)
        {
            throw new InvalidOperationException(
                $"SELinux dependency package cache contains {archives.Count} archives; expected {expectedNames.Count}");
        }

        var actualNames = new HashSet<string>(StringComparer.Ordinal);
        var packages = new SortedDictionary<string, SelinuxDependencyPackageEntry>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            var result = await runner.RunAsync(
                "bsdtar",
                ["-xOf", archive, ".PKGINFO"],
                cancellationToken: cancellationToken);
            _ = result.EnsureSuccess("could not inspect SELinux dependency package archive " + archive);
            var name = RequiredPackageInfo(result.Stdout, "pkgname", archive);
            var version = RequiredPackageInfo(result.Stdout, "pkgver", archive);
            var architecture = RequiredPackageInfo(result.Stdout, "arch", archive);
            if (!expectedNames.Contains(name) || !actualNames.Add(name))
            {
                throw new InvalidOperationException(
                    "SELinux dependency package cache contains an unexpected or duplicate package: " + name);
            }

            if (architecture is not (TargetArchitectureName or "any"))
            {
                throw new InvalidOperationException(
                    $"SELinux dependency package {name} has unsupported architecture {architecture}");
            }

            await using var stream = File.OpenRead(archive);
            packages.Add(
                Path.GetFileName(archive),
                new SelinuxDependencyPackageEntry(
                    name,
                    version,
                    architecture,
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken))));
        }

        if (!actualNames.SetEquals(expectedNames))
        {
            var missing = expectedNames.Except(actualNames, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                "SELinux dependency package cache is missing declared packages: " + string.Join(", ", missing));
        }

        return packages;
    }

    private static string RequiredPackageInfo(string packageInfo, string key, string archive)
    {
        var values = packageInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0] == key)
            .Select(parts => parts[1])
            .ToArray();
        return values.Length == 1 && !string.IsNullOrWhiteSpace(values[0])
            ? values[0]
            : throw new InvalidOperationException($"package archive has invalid {key} metadata: {archive}");
    }

    private static void RequireCurrentInput(string root, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) ||
            expectedSha256.Length != 64 ||
            expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "SELinux dependency input SHA-256 must be 64 hexadecimal characters");
        }

        var current = ComputeDependencyInputSha256(root);
        if (!string.Equals(expectedSha256, current, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SELinux dependency package cache does not match the current package build inputs");
        }
    }

    private static IEnumerable<string> EnumerateInputs(
        string directory,
        Func<string, bool> include)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!include(entry))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                (attributes & FileAttributes.Directory) != 0)
            {
                yield return entry;
                foreach (var nested in EnumerateInputs(entry, include))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }
}
