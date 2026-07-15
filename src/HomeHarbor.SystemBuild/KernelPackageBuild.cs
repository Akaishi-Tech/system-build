using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomeHarbor.Tooling;

public sealed record KernelPackageBuildPlan(IReadOnlyList<KernelPackageBuildChannelPlan> Channels);

public sealed record KernelPackageBuildChannelPlan(
    string Name,
    KernelPackageInputPlan Kernel,
    KernelPackageInputPlan? Module,
    IReadOnlyList<KernelPackageFilePlan> RequiredFiles,
    IReadOnlyList<KernelPackageBuildCommandPlan> ArtifactBuilds,
    IReadOnlyList<KernelPackageAddonPlan> Addons);

public sealed record KernelPackageInputPlan(
    string Package,
    string Origin,
    KernelPackageSourcePlan? Source);

public sealed record KernelPackageSourcePlan(
    string? GitUrl,
    string? GitRef,
    string? Path,
    string? PackageFile,
    string? PackageOutput,
    IReadOnlyList<string> PgpKeys);

public sealed record KernelPackageFilePlan(string Label, string Path);

public sealed record KernelPackageBuildCommandPlan(
    string Label,
    string Type,
    IReadOnlyList<string> Outputs);

public sealed record KernelPackageAddonPlan(
    string Key,
    string Path,
    string Origin,
    KernelPackageSourcePlan? Source,
    KernelPackageBuildCommandPlan? Build);

public sealed partial class KernelPackageBuildDescriptor
{
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeTokenPattern();

    [GeneratedRegex("^[a-z0-9._-]+$")]
    private static partial Regex SafeAddonKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9._/+-]+$")]
    private static partial Regex SafeRelativePathPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{16,40}$")]
    private static partial Regex PgpKeyPattern();

    [GeneratedRegex("(?m)^\\s*env\\s*:")]
    private static partial Regex ForbiddenEnvKeyPattern();

    private static readonly HashSet<string> ValidOrigins = ["upstream-arch-binary", "source-build", "local-artifact"];
    private static readonly HashSet<string> ValidBuildTypes = ["zfs-lts-artifacts", "zfs-utils-addon"];
    private static readonly string[] RequiredChannels = ["generic", "zfs"];

    public int SchemaVersion { get; set; }

    public string? Name { get; set; }

    public KernelPackageInputDescriptor Kernel { get; set; } = new();

    public KernelPackageInputDescriptor? Module { get; set; }

    public KernelPackageArtifactsDescriptor Artifacts { get; set; } = new();

    public List<KernelPackageBuildDescriptorItem> ArtifactBuilds { get; set; } = [];

    public List<KernelPackageAddonDescriptor> Addons { get; set; } = [];

    public static KernelPackageBuildPlan LoadPlan(string kernelRootPath, string root, string version)
    {
        if (!Directory.Exists(kernelRootPath))
        {
            throw new DirectoryNotFoundException("kernel package descriptor root not found: " + kernelRootPath);
        }

        ValidateSafeToken(version, "version");
        var manifestPaths = Directory.GetDirectories(kernelRootPath)
            .Select(directory => Path.Combine(directory, "manifest.yml"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (manifestPaths.Count == 0)
        {
            throw new InvalidOperationException("kernel package descriptor root must contain system/x86_64/kernel/{name}/manifest.yml files");
        }

        var channels = manifestPaths
            .Select(path =>
            {
                var plan = Load(path).ToPlan(Path.GetFullPath(root), version);
                var directoryName = Path.GetFileName(Path.GetDirectoryName(path));
                return !string.Equals(plan.Name, directoryName, StringComparison.Ordinal)
                    ? throw new InvalidOperationException($"kernel package manifest name must match its directory: {path}")
                    : plan;
            })
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            if (!seen.Add(channel.Name))
            {
                throw new InvalidOperationException($"kernel package descriptor declares duplicate kernel: {channel.Name}");
            }
        }

        foreach (var required in RequiredChannels)
        {
            if (!seen.Contains(required))
            {
                throw new InvalidOperationException($"kernel package descriptors must declare {required} kernel");
            }
        }

        return new KernelPackageBuildPlan(channels);
    }

    private static KernelPackageBuildDescriptor Load(string manifestPath)
    {
        var manifest = File.ReadAllText(manifestPath);
        if (ForbiddenEnvKeyPattern().IsMatch(manifest) || manifest.Contains("HOMEHARBOR_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("kernel package manifests must not contain environment overrides: " + manifestPath);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<KernelPackageBuildDescriptor>(manifest)
            ?? throw new InvalidOperationException("kernel package manifest is empty: " + manifestPath);
    }

    public KernelPackageBuildChannelPlan ToPlan(string root, string version)
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidOperationException("kernel package manifest requires schemaVersion=1");
        }

        var name = KernelChannel.Require(Name, "kernel package manifest name");
        var requiredFiles = Artifacts.ToFilePlans(root, version);
        return new KernelPackageBuildChannelPlan(
            name,
            ToInputPlan(Kernel, $"{name} kernel", root, version),
            Module is null ? null : ToInputPlan(Module, $"{name} module", root, version),
            requiredFiles,
            ArtifactBuilds.Select(build => build.ToPlan(root, version, "")).ToList(),
            Addons.Select(addon => addon.ToPlan(root, version)).ToList());
    }

    internal static KernelPackageInputPlan ToInputPlan(KernelPackageInputDescriptor input, string name, string root, string version)
    {
        ValidateSafeToken(input.Package, $"{name} package");
        ValidateOrigin(input.Origin, $"{name} origin");
        return new KernelPackageInputPlan(
            input.Package!.Trim(),
            input.Origin!.Trim(),
            input.Source?.ToPlan(root, version, $"{name} source"));
    }

    internal static string ResolvePath(string root, string version, string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required");
        }

        var expanded = Expand(value.Trim(), version, "");
        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        ValidateRelativePath(expanded, name);
        return Path.GetFullPath(Path.Combine(root, expanded));
    }

    internal static string RequireBuildType(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value) || !ValidBuildTypes.Contains(value.Trim())
            ? throw new InvalidOperationException($"{name} must be zfs-lts-artifacts or zfs-utils-addon")
            : value.Trim();
    }

    internal static string Expand(string value, string version, string output)
        => value.Replace("${VERSION}", version, StringComparison.Ordinal)
            .Replace("${OUTPUT}", output, StringComparison.Ordinal);

    internal static void ValidateAddonKey(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeAddonKeyPattern().IsMatch(value.Trim()))
        {
            throw new InvalidOperationException($"{name} must use lowercase letters, numbers, dot, underscore, or dash");
        }
    }

    private static void ValidateSafeToken(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeTokenPattern().IsMatch(value.Trim()))
        {
            throw new InvalidOperationException($"{name} must contain only letters, numbers, dot, underscore, or dash");
        }
    }

    private static void ValidateOrigin(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !ValidOrigins.Contains(value.Trim()))
        {
            throw new InvalidOperationException($"{name} must be upstream-arch-binary, source-build, or local-artifact");
        }
    }

    private static void ValidateRelativePath(string value, string name)
    {
        if (!SafeRelativePathPattern().IsMatch(value) ||
            value.StartsWith("../", StringComparison.Ordinal) ||
            value.Contains("/../", StringComparison.Ordinal) ||
            value.EndsWith("/..", StringComparison.Ordinal) ||
            value == ".." ||
            value == "." ||
            value.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} must be a safe relative path");
        }
    }

    internal static string? OptionalGitRef(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        ValidateRelativePath(trimmed, name);
        return trimmed;
    }

    internal static IReadOnlyList<string> NormalizePgpKeys(IEnumerable<string>? values, string name)
    {
        if (values is null)
        {
            return [];
        }

        var keys = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !PgpKeyPattern().IsMatch(value.Trim()))
            {
                throw new InvalidOperationException($"{name} must contain OpenPGP key ids or fingerprints using only hex characters");
            }

            keys.Add(value.Trim().ToUpperInvariant());
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }
}

public sealed class KernelPackageInputDescriptor
{
    public string? Package { get; set; }

    public string? Origin { get; set; }

    public KernelPackageSourceDescriptor? Source { get; set; }
}

public sealed class KernelPackageSourceDescriptor
{
    public string? GitUrl { get; set; }

    public string? GitRef { get; set; }

    public List<string> PgpKeys { get; set; } = [];

    public string? Path { get; set; }

    public string? PackageFile { get; set; }

    public string? PackageOutput { get; set; }

    public KernelPackageSourcePlan ToPlan(string root, string version, string name)
        => new(
            GitUrl,
            KernelPackageBuildDescriptor.OptionalGitRef(GitRef, $"{name} gitRef"),
            string.IsNullOrWhiteSpace(Path) ? null : KernelPackageBuildDescriptor.ResolvePath(root, version, Path, $"{name} path"),
            string.IsNullOrWhiteSpace(PackageFile) ? null : KernelPackageBuildDescriptor.ResolvePath(root, version, PackageFile, $"{name} packageFile"),
            string.IsNullOrWhiteSpace(PackageOutput) ? null : KernelPackageBuildDescriptor.ResolvePath(root, version, PackageOutput, $"{name} packageOutput"),
            KernelPackageBuildDescriptor.NormalizePgpKeys(PgpKeys, $"{name} pgpKeys"));
}

public sealed class KernelPackageArtifactsDescriptor
{
    public string? Vmlinuz { get; set; }

    public string? Initramfs { get; set; }

    public string? Modules { get; set; }

    public string? Firmware { get; set; }

    public string? Recovery { get; set; }

    public IReadOnlyList<KernelPackageFilePlan> ToFilePlans(string root, string version)
    {
        var descriptors = new (string Label, string? Path)[]
        {
            ("vmlinuz", Vmlinuz),
            ("initramfs", Initramfs),
            ("modules", Modules),
            ("firmware", Firmware),
            ("recovery", Recovery)
        };
        return descriptors
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => new KernelPackageFilePlan(
                item.Label,
                KernelPackageBuildDescriptor.ResolvePath(root, version, item.Path!, $"{item.Label} artifact")))
            .ToList();
    }
}

public sealed class KernelPackageBuildDescriptorItem
{
    public string? Label { get; set; }

    public string? Type { get; set; }

    public List<string> Outputs { get; set; } = [];

    public KernelPackageBuildCommandPlan ToPlan(string root, string version, string output)
    {
        var label = string.IsNullOrWhiteSpace(Label) ? "artifact builder" : Label.Trim();
        return new KernelPackageBuildCommandPlan(
            label,
            KernelPackageBuildDescriptor.RequireBuildType(Type, $"{label} type"),
            Outputs.Select(item => KernelPackageBuildDescriptor.ResolvePath(
                root,
                version,
                KernelPackageBuildDescriptor.Expand(item, version, output),
                $"{label} output")).ToList());
    }
}

public sealed class KernelPackageAddonDescriptor
{
    public string? Key { get; set; }

    public string? Origin { get; set; }

    public string? Image { get; set; }

    public KernelPackageSourceDescriptor? Source { get; set; }

    public KernelPackageBuildDescriptorItem? Build { get; set; }

    public KernelPackageAddonPlan ToPlan(string root, string version)
    {
        KernelPackageBuildDescriptor.ValidateAddonKey(Key, "kernel addon key");
        var origin = Origin ?? throw new InvalidOperationException($"kernel addon {Key} origin is required");
        var input = KernelPackageBuildDescriptor.ToInputPlan(
            new KernelPackageInputDescriptor { Package = Key, Origin = origin, Source = Source },
            $"kernel addon {Key}",
            root,
            version);
        var path = KernelPackageBuildDescriptor.ResolvePath(root, version, Image ?? "", $"kernel addon {Key} image");
        return new KernelPackageAddonPlan(Key!.Trim(), path, input.Origin, input.Source, Build?.ToPlan(root, version, path));
    }
}
