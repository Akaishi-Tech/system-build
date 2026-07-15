namespace HomeHarbor.Tooling;

internal static class SelinuxPolicyValidator
{
    private const string PolicyType = SelinuxPolicyStoreSynchronizer.PolicyType;

    // libsemanage expands the refpolicy shorthand from the maintained seusers
    // input into these canonical MLS/MCS ranges when it builds the policy store.
    private static readonly string[] RequiredInstalledSeuserMappings =
    [
        "root:root:s0-s0:c0.c1023",
        "homeharbor-containers:staff_u:s0-s0",
        "recovery:unconfined_u:s0-s0",
        "__default__:user_u:s0-s0"
    ];

    private static readonly string[] RequiredModules =
    [
        "base",
        "container",
        "homeharbor"
    ];

    internal static async Task ValidateAsync(
        string rootfs,
        string label,
        RootlessBuildExecutor rootless,
        CancellationToken cancellationToken)
    {
        var policyType = ValidateFileSystem(rootfs, label);
        var result = await rootless.RunMappedChrootAsync(
            rootfs,
            "semodule",
            ["-s", policyType, "-l"],
            new CommandRunOptions(StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess($"could not inspect the {label} SELinux module store");
        ValidateModuleListing(result.Stdout, label);
    }

    internal static string ValidateFileSystem(string rootfs, string label)
    {
        var semanageConfig = ParseSettings(
            RequiredNonemptyFile(Path.Combine(rootfs, "etc", "selinux", "semanage.conf"), label));
        if (!semanageConfig.TryGetValue("store-root", out var storeRoot) || storeRoot != "/var/lib/selinux")
        {
            throw new InvalidOperationException(
                $"{label} must use the writable /var/lib/selinux module store required by libsemanage locks");
        }

        var config = RequiredNonemptyFile(Path.Combine(rootfs, "etc", "selinux", "config"), label);
        var settings = ParseSettings(config);
        if (!settings.TryGetValue("SELINUX", out var mode) || mode != "enforcing")
        {
            throw new InvalidOperationException($"{label} must configure SELINUX=enforcing");
        }

        if (!settings.TryGetValue("SELINUXTYPE", out var policyType) || policyType != PolicyType)
        {
            throw new InvalidOperationException($"{label} must configure SELINUXTYPE={PolicyType}");
        }

        var policyRoot = Path.Combine(rootfs, "etc", "selinux", PolicyType);
        var moduleStore = Path.Combine(rootfs, "var", "lib", "selinux", PolicyType, "active", "modules");
        if (!Directory.Exists(moduleStore) || !Directory.EnumerateFileSystemEntries(moduleStore).Any())
        {
            throw new InvalidOperationException(
                $"{label} must contain the built SELinux module store before image sealing: {moduleStore}");
        }

        _ = RequiredNonemptyFile(
            Path.Combine(policyRoot, "contexts", "files", "file_contexts"),
            label);

        var policyDirectory = Path.Combine(policyRoot, "policy");
        var policies = Directory.Exists(policyDirectory)
            ? Directory.GetFiles(policyDirectory, "policy.*", SearchOption.TopDirectoryOnly)
            : [];
        if (policies.Length != 1 || new FileInfo(policies[0]).Length == 0)
        {
            throw new InvalidOperationException(
                $"{label} must contain exactly one nonempty compiled SELinux policy, found {policies.Length}");
        }

        var seusers = File.ReadAllLines(RequiredNonemptyFile(Path.Combine(policyRoot, "seusers"), label))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
        if (!seusers.SetEquals(RequiredInstalledSeuserMappings))
        {
            throw new InvalidOperationException(
                $"{label} SELinux seusers mappings differ from the maintained appliance mapping");
        }

        return PolicyType;
    }

    private static IReadOnlyDictionary<string, string> ParseSettings(string path)
        => File.ReadAllLines(path)
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    internal static void ValidateModuleListing(string moduleListing, string label)
    {
        var installedModules = moduleListing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0)
            .SelectMany(parts => parts)
            .Select(value => value.EndsWith(".pp", StringComparison.Ordinal) ? value[..^3] : value)
            .ToHashSet(StringComparer.Ordinal);
        var missing = RequiredModules.Where(module => !installedModules.Contains(module)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"{label} SELinux module store is missing: {string.Join(", ", missing)}");
        }
    }

    private static string RequiredNonemptyFile(string path, string label)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"{label} is missing required nonempty SELinux file: {path}");
        }

        return path;
    }
}
