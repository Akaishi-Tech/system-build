using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomeHarbor.Tooling;

public sealed record SystemImageBuildPlan(
    string Version,
    string Architecture,
    string BootMode,
    SystemImagePackagesPlan Packages,
    SystemImageRootPlan Rootfs,
    SystemImageRootPlan Recovery,
    IReadOnlyList<SystemImagePartitionPlan> Partitions,
    IReadOnlyList<SystemImageLogicalPartitionPlan> LogicalPartitions,
    SystemImageSuperPlan Super,
    IReadOnlyList<string> KernelArgs,
    SystemImageArtifactsPlan Artifacts);

public sealed record SystemImagePackagesPlan(
    IReadOnlyList<string> Rootfs,
    IReadOnlyList<string> Recovery);

public sealed record SystemImageRootPlan(
    string Hostname,
    IReadOnlyList<string> Directories,
    IReadOnlyList<SystemImageFilePlan> Files,
    IReadOnlyList<SystemImageFstabEntryPlan> Fstab,
    bool CreateEmptyCrypttab,
    IReadOnlyList<string> MkinitcpioHooks,
    IReadOnlyList<SystemImageUserPlan> Users,
    IReadOnlyList<SystemImageGeneratedUserPlan> GeneratedUsers,
    IReadOnlyList<SystemImageGroupPlan> Groups,
    IReadOnlyList<SystemImageSubIdPlan> SubIds,
    IReadOnlyList<string> LingerUsers,
    IReadOnlyList<string> Shells,
    IReadOnlyList<string> SystemdUnits);

public sealed record SystemImageFilePlan(
    string? Source,
    string Destination,
    string Mode,
    string? Content);

public sealed record SystemImageFstabEntryPlan(
    string Spec,
    string MountPoint,
    string FileSystem,
    string Options,
    int Dump,
    int Pass);

public sealed record SystemImageUserPlan(
    string Name,
    bool System,
    bool UserGroup,
    string? HomeDir,
    bool CreateHome,
    string? Shell,
    IReadOnlyList<string> Groups);

public sealed record SystemImageGeneratedUserPlan(
    string Prefix,
    int Start,
    int End,
    int Width,
    bool System,
    string? HomeDir,
    bool CreateHome,
    string? Shell,
    IReadOnlyList<string> Groups);

public sealed record SystemImageGroupPlan(
    string Name,
    bool System);

public sealed record SystemImageSubIdPlan(
    string Name,
    int Start,
    int Count);

public sealed record SystemImagePartitionPlan(
    string Name,
    string Label,
    string Size,
    long? SizeBytes,
    long? DataSizeBytes,
    string FileSystem,
    string Purpose,
    string? Payload);

public sealed record SystemImageLogicalPartitionPlan(
    string Name,
    string Parent,
    string Size,
    long SizeBytes,
    long DataSizeBytes,
    string FileSystem,
    string Purpose,
    string Payload);

public sealed record SystemImageSuperPlan(
    string Name,
    string GroupName,
    long MetadataSizeBytes,
    int MetadataSlots,
    long ReservedBytes,
    long GroupSizeBytes,
    long PartitionSizeBytes);

public sealed record SystemImageArtifactsPlan(
    SystemImageArtifactPlan Vmlinuz,
    SystemImageArtifactPlan Initramfs,
    SystemImageArtifactPlan Modules,
    SystemImageArtifactPlan Firmware,
    SystemImageArtifactPlan Rootfs,
    SystemImageArtifactPlan Recovery,
    SystemImageArtifactPlan VbmetaA,
    SystemImageArtifactPlan VbmetaB,
    SystemImageArtifactPlan Boot,
    SystemImageArtifactPlan Bootloader,
    SystemImageArtifactPlan Bootx64,
    SystemImageArtifactPlan SuperDump,
    SystemImageArtifactPlan Plan);

public sealed record SystemImageArtifactPlan(
    string Path,
    string? Sha256Path,
    string? DigestPath);

public sealed partial class SystemImageBuildDescriptor
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeTokenPattern();

    [GeneratedRegex("^[A-Za-z0-9@._+-]+$")]
    private static partial Regex SafePackagePattern();

    [GeneratedRegex("^[A-Za-z0-9@._+-]+$")]
    private static partial Regex SafeNamePattern();

    [GeneratedRegex("^[A-Za-z0-9._/+-]+$")]
    private static partial Regex SafeRelativePathPattern();

    [GeneratedRegex("^0[0-7]{3}$")]
    private static partial Regex SafeModePattern();

    [GeneratedRegex("(?m)^\\s*env\\s*:")]
    private static partial Regex ForbiddenEnvKeyPattern();

    public int SchemaVersion { get; set; }

    public string? Name { get; set; }

    public string? Architecture { get; set; }

    public string? BootMode { get; set; }

    public SystemImagePackagesDescriptor Packages { get; set; } = new();

    public SystemImageRootDescriptor Rootfs { get; set; } = new();

    public SystemImageRootDescriptor Recovery { get; set; } = new();

    public List<SystemImagePartitionDescriptor> Partitions { get; set; } = [];

    public List<SystemImageLogicalPartitionDescriptor> LogicalPartitions { get; set; } = [];

    public SystemImageSuperDescriptor Super { get; set; } = new();

    public List<string> KernelArgs { get; set; } = [];

    public SystemImageArtifactsDescriptor Artifacts { get; set; } = new();

    public static string DefaultManifestPath(string root)
        => Path.Combine(Path.GetFullPath(root), "system", "x86_64", "system", "manifest.yml");

    public static SystemImageBuildPlan LoadDefaultPlan(string root, string version)
        => LoadPlan(DefaultManifestPath(root), root, version);

    public static SystemImageBuildPlan LoadPlan(string manifestPath, string root, string version)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("system image manifest not found", manifestPath);
        }

        ValidateSafeToken(version, "version");
        var manifest = File.ReadAllText(manifestPath);
        if (ForbiddenEnvKeyPattern().IsMatch(manifest) || manifest.Contains("HOMEHARBOR_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system image manifests must not contain environment overrides: " + manifestPath);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var descriptor = deserializer.Deserialize<SystemImageBuildDescriptor>(manifest)
            ?? throw new InvalidOperationException("system image manifest is empty: " + manifestPath);
        return descriptor.ToPlan(Path.GetFullPath(root), version);
    }

    public SystemImageBuildPlan ToPlan(string root, string version)
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidOperationException("system image manifest requires schemaVersion=1");
        }

        if (!string.Equals(RequireName(Name, "system image manifest name"), "system", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system image manifest name must be system");
        }

        var architecture = RequireName(Architecture, "system image architecture");
        var bootMode = RequireName(BootMode, "system image boot mode");
        var rawUkiBootMode = SecureBootAssets.BootMode();
        var packagePlan = Packages.ToPlan();
        var rootfsPlan = Rootfs.ToPlan(root, version, "rootfs");
        var recoveryPlan = Recovery.ToPlan(root, version, "recovery");
        var partitions = Partitions.Select(partition => partition.ToPlan(rawUkiBootMode)).ToList();
        var logicalPartitions = LogicalPartitions.Select(logical => logical.ToPlan()).ToList();
        var superPlan = Super.ToPlan(logicalPartitions);
        var superPartition = partitions.SingleOrDefault(partition => partition.Name == superPlan.Name)
            ?? throw new InvalidOperationException($"system image partitions must include {superPlan.Name}");
        return superPartition.SizeBytes != superPlan.PartitionSizeBytes
            ? throw new InvalidOperationException($"{superPlan.Name} partition size must equal logical group plus reserved bytes")
            : new SystemImageBuildPlan(
            version,
            architecture,
            bootMode,
            packagePlan,
            rootfsPlan,
            recoveryPlan,
            partitions,
            logicalPartitions,
            superPlan,
            KernelArgs.Select(arg => Expand(RequireNonEmpty(arg, "kernel arg"), version, rawUkiBootMode)).ToList(),
            Artifacts.ToPlan(root, version));
    }

    internal static string ResolvePath(string root, string version, string value, string name)
    {
        var expanded = Expand(RequireNonEmpty(value, name), version, "");
        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        ValidateRelativePath(expanded, name);
        return Path.GetFullPath(Path.Combine(root, expanded));
    }

    internal static string Expand(string value, string version, string bootMode)
        => value.Replace("${VERSION}", version, StringComparison.Ordinal)
            .Replace("${BOOT_MODE}", bootMode, StringComparison.Ordinal);

    internal static string RequirePath(string value, string name)
    {
        var path = RequireNonEmpty(value, name);
        return !path.StartsWith('/') ||
            path.Contains("/../", StringComparison.Ordinal) ||
            path.EndsWith("/..", StringComparison.Ordinal) ||
            path.Contains("//", StringComparison.Ordinal) ||
            path.Contains('\\')
            ? throw new InvalidOperationException($"{name} must be an absolute image path")
            : path;
    }

    internal static string RequireMode(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value) || !SafeModePattern().IsMatch(value.Trim())
            ? throw new InvalidOperationException($"{name} must be an octal file mode such as 0644")
            : value.Trim();
    }

    internal static string RequireName(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value) || !SafeNamePattern().IsMatch(value.Trim())
            ? throw new InvalidOperationException($"{name} must use letters, numbers, dot, underscore, at, plus, or dash")
            : value.Trim();
    }

    internal static string RequireFstabValue(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsWhiteSpace) ||
            value.Contains('\\', StringComparison.Ordinal)
            ? throw new InvalidOperationException($"{name} must be a non-empty fstab value without whitespace")
            : value.Trim();
    }

    internal static void ValidateSafePackage(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafePackagePattern().IsMatch(value.Trim()))
        {
            throw new InvalidOperationException($"{name} must be a safe package name");
        }
    }

    internal static long MibToBytes(long? value, string name)
    {
        return value is null or <= 0 ? throw new InvalidOperationException($"{name} must be a positive MiB value") : checked(value.Value * MiB);
    }

    internal static long? OptionalMibToBytes(long? value, string name)
    {
        return value is null ? null : MibToBytes(value, name);
    }

    internal static long KibToBytes(long? value, string name)
    {
        return value is null or <= 0 ? throw new InvalidOperationException($"{name} must be a positive KiB value") : checked(value.Value * KiB);
    }

    private static string RequireNonEmpty(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required") : value.Trim();
    }

    private static void ValidateSafeToken(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeTokenPattern().IsMatch(value.Trim()))
        {
            throw new InvalidOperationException($"{name} must contain only letters, numbers, dot, underscore, and dash");
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

    internal static string SizeString(long? bytes)
        => bytes is null ? "remaining" : $"{bytes.Value / MiB}M";
}

public sealed class SystemImagePackagesDescriptor
{
    public List<string> Rootfs { get; set; } = [];

    public List<string> Recovery { get; set; } = [];

    public SystemImagePackagesPlan ToPlan()
    {
        ValidatePackages(Rootfs, "rootfs");
        ValidatePackages(Recovery, "recovery");
        return new SystemImagePackagesPlan(Rootfs.Select(item => item.Trim()).ToList(), Recovery.Select(item => item.Trim()).ToList());
    }

    private static void ValidatePackages(IReadOnlyList<string> packages, string name)
    {
        if (packages.Count == 0)
        {
            throw new InvalidOperationException($"system image {name} packages must not be empty");
        }

        foreach (var package in packages)
        {
            SystemImageBuildDescriptor.ValidateSafePackage(package, $"system image {name} package");
        }
    }
}

public sealed class SystemImageRootDescriptor
{
    public string? Hostname { get; set; }

    public List<string> Directories { get; set; } = [];

    public List<SystemImageFileDescriptor> Files { get; set; } = [];

    public List<SystemImageFstabEntryDescriptor> Fstab { get; set; } = [];

    public bool CreateEmptyCrypttab { get; set; }

    public List<string> MkinitcpioHooks { get; set; } = [];

    public List<SystemImageUserDescriptor> Users { get; set; } = [];

    public List<SystemImageGeneratedUserDescriptor> GeneratedUsers { get; set; } = [];

    public List<SystemImageGroupDescriptor> Groups { get; set; } = [];

    public List<SystemImageSubIdDescriptor> SubIds { get; set; } = [];

    public List<string> LingerUsers { get; set; } = [];

    public List<string> Shells { get; set; } = [];

    public List<string> SystemdUnits { get; set; } = [];

    public SystemImageRootPlan ToPlan(string root, string version, string name)
        => new(
            SystemImageBuildDescriptor.RequireName(Hostname, $"{name} hostname"),
            Directories.Select((directory, index) => SystemImageBuildDescriptor.RequirePath(directory, $"{name} directory {index + 1}")).ToList(),
            Files.Select((file, index) => file.ToPlan(root, version, $"{name} file {index + 1}")).ToList(),
            Fstab.Select((entry, index) => entry.ToPlan($"{name} fstab entry {index + 1}")).ToList(),
            CreateEmptyCrypttab,
            MkinitcpioHooks.Select(hook => SystemImageBuildDescriptor.RequireName(hook, $"{name} mkinitcpio hook")).ToList(),
            Users.Select((user, index) => user.ToPlan($"{name} user {index + 1}")).ToList(),
            GeneratedUsers.Select((user, index) => user.ToPlan($"{name} generated user {index + 1}")).ToList(),
            Groups.Select((group, index) => group.ToPlan($"{name} group {index + 1}")).ToList(),
            SubIds.Select((subId, index) => subId.ToPlan($"{name} subid {index + 1}")).ToList(),
            LingerUsers.Select(user => SystemImageBuildDescriptor.RequireName(user, $"{name} linger user")).ToList(),
            Shells.Select(shell => SystemImageBuildDescriptor.RequirePath(shell, $"{name} shell")).ToList(),
            SystemdUnits.Select(unit => SystemImageBuildDescriptor.RequireName(unit, $"{name} systemd unit")).ToList());
}

public sealed class SystemImageFileDescriptor
{
    public string? Source { get; set; }

    public string? Destination { get; set; }

    public string? Mode { get; set; }

    public string? Content { get; set; }

    public SystemImageFilePlan ToPlan(string root, string version, string name)
    {
        var source = string.IsNullOrWhiteSpace(Source)
            ? null
            : SystemImageBuildDescriptor.ResolvePath(root, version, Source, $"{name} source");
        var content = string.IsNullOrEmpty(Content) ? null : Content;
        return (source is null) == (content is null)
            ? throw new InvalidOperationException($"{name} must specify exactly one of source or content")
            : new SystemImageFilePlan(
            source,
            SystemImageBuildDescriptor.RequirePath(Destination ?? "", $"{name} destination"),
            SystemImageBuildDescriptor.RequireMode(Mode, $"{name} mode"),
            content);
    }
}

public sealed class SystemImageFstabEntryDescriptor
{
    public string? Spec { get; set; }

    public string? MountPoint { get; set; }

    public string? FileSystem { get; set; }

    public string? Options { get; set; }

    public int Dump { get; set; }

    public int Pass { get; set; }

    public SystemImageFstabEntryPlan ToPlan(string name)
        => new(
            SystemImageBuildDescriptor.RequireFstabValue(Spec, $"{name} spec"),
            SystemImageBuildDescriptor.RequirePath(MountPoint ?? "", $"{name} mount point"),
            SystemImageBuildDescriptor.RequireName(FileSystem, $"{name} filesystem"),
            string.IsNullOrWhiteSpace(Options) ? "defaults" : SystemImageBuildDescriptor.RequireFstabValue(Options, $"{name} options"),
            Dump,
            Pass);
}

public sealed class SystemImageUserDescriptor
{
    public string? Name { get; set; }

    public bool System { get; set; }

    public bool UserGroup { get; set; }

    public string? HomeDir { get; set; }

    public bool CreateHome { get; set; }

    public string? Shell { get; set; }

    public List<string> Groups { get; set; } = [];

    public SystemImageUserPlan ToPlan(string name)
        => new(
            SystemImageBuildDescriptor.RequireName(Name, $"{name} name"),
            System,
            UserGroup,
            string.IsNullOrWhiteSpace(HomeDir) ? null : SystemImageBuildDescriptor.RequirePath(HomeDir, $"{name} homeDir"),
            CreateHome,
            string.IsNullOrWhiteSpace(Shell) ? null : SystemImageBuildDescriptor.RequirePath(Shell, $"{name} shell"),
            Groups.Select(group => SystemImageBuildDescriptor.RequireName(group, $"{name} group")).ToList());
}

public sealed class SystemImageGeneratedUserDescriptor
{
    public string? Prefix { get; set; }

    public int Start { get; set; }

    public int End { get; set; }

    public int Width { get; set; }

    public bool System { get; set; }

    public string? HomeDir { get; set; }

    public bool CreateHome { get; set; }

    public string? Shell { get; set; }

    public List<string> Groups { get; set; } = [];

    public SystemImageGeneratedUserPlan ToPlan(string name)
    {
        return Start <= 0 || End < Start || Width <= 0
            ? throw new InvalidOperationException($"{name} must use a positive start/end/width range")
            : new SystemImageGeneratedUserPlan(
            SystemImageBuildDescriptor.RequireName(Prefix, $"{name} prefix"),
            Start,
            End,
            Width,
            System,
            string.IsNullOrWhiteSpace(HomeDir) ? null : SystemImageBuildDescriptor.RequirePath(HomeDir, $"{name} homeDir"),
            CreateHome,
            string.IsNullOrWhiteSpace(Shell) ? null : SystemImageBuildDescriptor.RequirePath(Shell, $"{name} shell"),
            Groups.Select(group => SystemImageBuildDescriptor.RequireName(group, $"{name} group")).ToList());
    }
}

public sealed class SystemImageGroupDescriptor
{
    public string? Name { get; set; }

    public bool System { get; set; }

    public SystemImageGroupPlan ToPlan(string name)
        => new(SystemImageBuildDescriptor.RequireName(Name, $"{name} name"), System);
}

public sealed class SystemImageSubIdDescriptor
{
    public string? Name { get; set; }

    public int Start { get; set; }

    public int Count { get; set; }

    public SystemImageSubIdPlan ToPlan(string name)
    {
        return Start <= 0 || Count <= 0
            ? throw new InvalidOperationException($"{name} must use positive start and count values")
            : new SystemImageSubIdPlan(
            SystemImageBuildDescriptor.RequireName(Name, $"{name} name"),
            Start,
            Count);
    }
}

public sealed class SystemImagePartitionDescriptor
{
    public string? Name { get; set; }

    public string? Label { get; set; }

    public long? SizeMib { get; set; }

    public string? Size { get; set; }

    public long? DataSizeMib { get; set; }

    public string? FileSystem { get; set; }

    public string? Purpose { get; set; }

    public string? Payload { get; set; }

    public SystemImagePartitionPlan ToPlan(string rawUkiBootMode)
    {
        var name = SystemImageBuildDescriptor.RequireName(Name, "partition name");
        var sizeBytes = SystemImageBuildDescriptor.OptionalMibToBytes(SizeMib, $"{name} partition sizeMib");
        var size = sizeBytes is null
            ? (string.IsNullOrWhiteSpace(Size) ? throw new InvalidOperationException($"{name} partition size or sizeMib is required") : Size.Trim())
            : SystemImageBuildDescriptor.SizeString(sizeBytes);
        var dataSizeBytes = SystemImageBuildDescriptor.OptionalMibToBytes(DataSizeMib, $"{name} partition dataSizeMib");
        if (dataSizeBytes is not null && sizeBytes is not null && dataSizeBytes > sizeBytes)
        {
            throw new InvalidOperationException($"{name} partition dataSizeMib must not exceed sizeMib");
        }

        var fileSystem = SystemImageBuildDescriptor.RequireName(FileSystem, $"{name} partition filesystem");
        if (fileSystem == "raw-uki" && (name == "boot_a" || name == "boot_b"))
        {
            fileSystem = rawUkiBootMode;
        }

        return new SystemImagePartitionPlan(
            name,
            Label?.Trim() ?? name,
            size,
            sizeBytes,
            dataSizeBytes,
            fileSystem,
            string.IsNullOrWhiteSpace(Purpose) ? name : Purpose.Trim(),
            string.IsNullOrWhiteSpace(Payload) ? null : SystemImageBuildDescriptor.RequireName(Payload, $"{name} partition payload"));
    }
}

public sealed class SystemImageLogicalPartitionDescriptor
{
    public string? Name { get; set; }

    public string? Parent { get; set; }

    public long? SizeMib { get; set; }

    public long? DataSizeMib { get; set; }

    public string? FileSystem { get; set; }

    public string? Purpose { get; set; }

    public string? Payload { get; set; }

    public SystemImageLogicalPartitionPlan ToPlan()
    {
        var name = SystemImageBuildDescriptor.RequireName(Name, "logical partition name");
        var sizeBytes = SystemImageBuildDescriptor.MibToBytes(SizeMib, $"{name} logical partition sizeMib");
        var dataSizeBytes = SystemImageBuildDescriptor.MibToBytes(DataSizeMib, $"{name} logical partition dataSizeMib");
        return dataSizeBytes > sizeBytes
            ? throw new InvalidOperationException($"{name} logical partition dataSizeMib must not exceed sizeMib")
            : new SystemImageLogicalPartitionPlan(
            name,
            SystemImageBuildDescriptor.RequireName(Parent, $"{name} logical partition parent"),
            SystemImageBuildDescriptor.SizeString(sizeBytes),
            sizeBytes,
            dataSizeBytes,
            SystemImageBuildDescriptor.RequireName(FileSystem, $"{name} logical partition filesystem"),
            string.IsNullOrWhiteSpace(Purpose) ? name : Purpose.Trim(),
            SystemImageBuildDescriptor.RequireName(Payload, $"{name} logical partition payload"));
    }
}

public sealed class SystemImageSuperDescriptor
{
    public string? Name { get; set; }

    public string? GroupName { get; set; }

    public long? MetadataSizeKib { get; set; }

    public int MetadataSlots { get; set; }

    public long? ReservedMib { get; set; }

    public SystemImageSuperPlan ToPlan(IReadOnlyList<SystemImageLogicalPartitionPlan> logicalPartitions)
    {
        if (MetadataSlots <= 0)
        {
            throw new InvalidOperationException("super metadataSlots must be positive");
        }

        var name = SystemImageBuildDescriptor.RequireName(Name, "super name");
        var groupSizeBytes = logicalPartitions
            .Where(logical => logical.Parent == name)
            .Sum(logical => logical.SizeBytes);
        if (groupSizeBytes <= 0)
        {
            throw new InvalidOperationException($"super {name} must contain logical partitions");
        }

        var reservedBytes = SystemImageBuildDescriptor.MibToBytes(ReservedMib, "super reservedMib");
        return new SystemImageSuperPlan(
            name,
            SystemImageBuildDescriptor.RequireName(GroupName, "super groupName"),
            SystemImageBuildDescriptor.KibToBytes(MetadataSizeKib, "super metadataSizeKib"),
            MetadataSlots,
            reservedBytes,
            groupSizeBytes,
            checked(groupSizeBytes + reservedBytes));
    }
}

public sealed class SystemImageArtifactsDescriptor
{
    public SystemImageArtifactDescriptor Vmlinuz { get; set; } = new();

    public SystemImageArtifactDescriptor Initramfs { get; set; } = new();

    public SystemImageArtifactDescriptor Modules { get; set; } = new();

    public SystemImageArtifactDescriptor Firmware { get; set; } = new();

    public SystemImageArtifactDescriptor Rootfs { get; set; } = new();

    public SystemImageArtifactDescriptor Recovery { get; set; } = new();

    public SystemImageArtifactDescriptor VbmetaA { get; set; } = new();

    public SystemImageArtifactDescriptor VbmetaB { get; set; } = new();

    public SystemImageArtifactDescriptor Boot { get; set; } = new();

    public SystemImageArtifactDescriptor Bootloader { get; set; } = new();

    public SystemImageArtifactDescriptor Bootx64 { get; set; } = new();

    public SystemImageArtifactDescriptor SuperDump { get; set; } = new();

    public SystemImageArtifactDescriptor Plan { get; set; } = new();

    public SystemImageArtifactsPlan ToPlan(string root, string version)
        => new(
            Vmlinuz.ToPlan(root, version, "vmlinuz artifact"),
            Initramfs.ToPlan(root, version, "initramfs artifact"),
            Modules.ToPlan(root, version, "modules artifact"),
            Firmware.ToPlan(root, version, "firmware artifact"),
            Rootfs.ToPlan(root, version, "rootfs artifact"),
            Recovery.ToPlan(root, version, "recovery artifact"),
            VbmetaA.ToPlan(root, version, "vbmetaA artifact"),
            VbmetaB.ToPlan(root, version, "vbmetaB artifact"),
            Boot.ToPlan(root, version, "boot artifact"),
            Bootloader.ToPlan(root, version, "bootloader artifact"),
            Bootx64.ToPlan(root, version, "bootx64 artifact"),
            SuperDump.ToPlan(root, version, "superDump artifact"),
            Plan.ToPlan(root, version, "plan artifact"));
}

public sealed class SystemImageArtifactDescriptor
{
    public string? Path { get; set; }

    public string? Sha256 { get; set; }

    public string? Digest { get; set; }

    public SystemImageArtifactPlan ToPlan(string root, string version, string name)
        => new(
            SystemImageBuildDescriptor.ResolvePath(root, version, Path ?? "", $"{name} path"),
            string.IsNullOrWhiteSpace(Sha256) ? null : SystemImageBuildDescriptor.ResolvePath(root, version, Sha256, $"{name} sha256"),
            string.IsNullOrWhiteSpace(Digest) ? null : SystemImageBuildDescriptor.ResolvePath(root, version, Digest, $"{name} digest"));
}
