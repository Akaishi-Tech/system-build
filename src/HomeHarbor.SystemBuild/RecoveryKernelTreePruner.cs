namespace HomeHarbor.Tooling;

internal static class RecoveryKernelTreePruner
{
    internal static readonly string[] RequiredCoreModules =
    [
        "dm-mod",
        "dm-crypt",
        "dm-verity",
        "md-mod",
        "raid456",
        "erofs",
        "loop",
        "overlay",
        "ext4",
        "btrfs",
        "xfs",
        "vfat",
        "fat",
        "virtio",
        "virtio-pci",
        "virtio-blk",
        "virtio-net",
        "sd-mod",
        "ahci",
        "libahci",
        "usb-storage"
    ];

    private static readonly string[] OptionalModules =
    [
        "9p",
        "9pnet",
        "9pnet-virtio",
        "asix",
        "atlantic",
        "ax88179-178a",
        "bnx2",
        "bnx2x",
        "bnxt-en",
        "cdc-ether",
        "cdc-ncm",
        "e1000",
        "e1000e",
        "i40e",
        "ice",
        "igb",
        "igbvf",
        "igc",
        "ixgbe",
        "ixgbevf",
        "nvme",
        "nvme-auth",
        "nvme-core",
        "nvme-keyring",
        "r8152",
        "r8153-ecm",
        "r8169",
        "realtek",
        "tg3",
        "uas",
        "usbnet",
        "virtio-mmio",
        "virtio-rng"
    ];

    private static readonly string[] MetadataFiles =
    [
        "modules.builtin",
        "modules.builtin.modinfo",
        "modules.devname",
        "modules.order",
        "modules.softdep",
        "modules.weakdep",
        "pkgbase"
    ];

    private static readonly string[] FirmwareDirectories =
    [
        "bnx2",
        "bnx2x",
        "intel/ice",
        "rtl_nic",
        "tigon"
    ];

    public static RecoveryKernelPruneResult Prune(
        string sourceModulesRoot,
        string sourceFirmwareRoot,
        string destinationModulesRoot,
        string destinationFirmwareRoot)
    {
        var sourceRelease = ResolveSingleKernelRelease(sourceModulesRoot);
        var kernelRelease = Path.GetFileName(sourceRelease)
            ?? throw new InvalidOperationException("could not resolve recovery kernel release");
        var destinationRelease = Path.Combine(destinationModulesRoot, kernelRelease);

        DeleteDirectory(destinationModulesRoot);
        DeleteDirectory(destinationFirmwareRoot);
        _ = Directory.CreateDirectory(destinationRelease);
        _ = Directory.CreateDirectory(destinationFirmwareRoot);
        File.SetUnixFileMode(destinationModulesRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(destinationRelease, File.GetUnixFileMode(sourceRelease));
        File.SetUnixFileMode(destinationFirmwareRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var modulePaths = BuildModulePathMap(sourceRelease);
        var builtInModules = ReadBuiltInModules(sourceRelease);
        var dependencies = ReadModuleDependencies(sourceRelease);
        var softDependencies = ReadSoftDependencies(sourceRelease);

        foreach (var module in RequiredCoreModules.Select(NormalizeModuleName))
        {
            if (!modulePaths.ContainsKey(module) && !builtInModules.Contains(module))
            {
                throw new InvalidOperationException($"recovery kernel module is required but was not found as a file or built-in module: {module}");
            }
        }

        foreach (var metadata in MetadataFiles)
        {
            var source = Path.Combine(sourceRelease, metadata);
            if (File.Exists(source))
            {
                CopyFile(source, Path.Combine(destinationRelease, metadata));
            }
        }

        var copiedPaths = new HashSet<string>(StringComparer.Ordinal);
        var visitedModules = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Module, bool Required)>();
        foreach (var module in RequiredCoreModules)
        {
            queue.Enqueue((NormalizeModuleName(module), true));
        }

        foreach (var module in OptionalModules)
        {
            queue.Enqueue((NormalizeModuleName(module), false));
        }

        while (queue.Count > 0)
        {
            var (module, required) = queue.Dequeue();
            if (!visitedModules.Add(module))
            {
                continue;
            }

            if (!modulePaths.TryGetValue(module, out var relativePath))
            {
                if (required && !builtInModules.Contains(module))
                {
                    throw new InvalidOperationException($"recovery kernel module dependency was not found as a file or built-in module: {module}");
                }

                continue;
            }

            CopyModule(sourceRelease, destinationRelease, relativePath, copiedPaths);
            if (dependencies.TryGetValue(relativePath, out var moduleDependencies))
            {
                foreach (var dependency in moduleDependencies)
                {
                    queue.Enqueue((ModuleNameFromRelativePath(dependency), true));
                }
            }

            if (softDependencies.TryGetValue(module, out var moduleSoftDependencies))
            {
                foreach (var softDependency in moduleSoftDependencies)
                {
                    queue.Enqueue((softDependency, true));
                }
            }
        }

        var copiedFirmwareDirectories = 0;
        foreach (var firmwareDirectory in FirmwareDirectories)
        {
            var source = Path.Combine(sourceFirmwareRoot, firmwareDirectory);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(destinationFirmwareRoot, firmwareDirectory);
            FileTreeCopier.CopyDirectory(source, destination);
            copiedFirmwareDirectories++;
        }

        return new RecoveryKernelPruneResult(kernelRelease, copiedPaths.Count, copiedFirmwareDirectories);
    }

    private static string ResolveSingleKernelRelease(string modulesRoot)
    {
        if (!Directory.Exists(modulesRoot))
        {
            throw new DirectoryNotFoundException(modulesRoot);
        }

        var releases = Directory.GetDirectories(modulesRoot)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return releases.Length != 1
            ? throw new InvalidOperationException($"expected exactly one recovery kernel modules directory, found {releases.Length}")
            : releases[0];
    }

    private static Dictionary<string, string> BuildModulePathMap(string releaseRoot)
    {
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(releaseRoot, "*.ko*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(releaseRoot, file));
            if (!TryModuleNameFromRelativePath(relativePath, out var moduleName))
            {
                continue;
            }

            modules[moduleName] = relativePath;
        }

        return modules;
    }

    private static HashSet<string> ReadBuiltInModules(string releaseRoot)
    {
        var path = Path.Combine(releaseRoot, "modules.builtin");
        var modules = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return modules;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (TryModuleNameFromRelativePath(trimmed, out var moduleName))
            {
                _ = modules.Add(moduleName);
            }
        }

        return modules;
    }

    private static Dictionary<string, string[]> ReadModuleDependencies(string releaseRoot)
    {
        var path = Path.Combine(releaseRoot, "modules.dep");
        var dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return dependencies;
        }

        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var module = NormalizeRelativePath(line[..separator].Trim());
            ValidateSafeRelativeModulePath(module);
            var values = line[(separator + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeRelativePath)
                .Select(path =>
                {
                    ValidateSafeRelativeModulePath(path);
                    return path;
                })
                .ToArray();
            dependencies[module] = values;
        }

        return dependencies;
    }

    private static Dictionary<string, string[]> ReadSoftDependencies(string releaseRoot)
    {
        var path = Path.Combine(releaseRoot, "modules.softdep");
        var dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return dependencies;
        }

        foreach (var line in File.ReadLines(path))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 3 || !string.Equals(tokens[0], "softdep", StringComparison.Ordinal))
            {
                continue;
            }

            var module = NormalizeModuleName(tokens[1]);
            var values = new List<string>();
            for (var i = 2; i < tokens.Length; i++)
            {
                if (string.Equals(tokens[i], "pre:", StringComparison.Ordinal) ||
                    string.Equals(tokens[i], "post:", StringComparison.Ordinal))
                {
                    continue;
                }

                values.Add(NormalizeModuleName(tokens[i]));
            }

            if (values.Count > 0)
            {
                dependencies[module] = [.. values];
            }
        }

        return dependencies;
    }

    private static void CopyModule(
        string sourceRelease,
        string destinationRelease,
        string relativePath,
        HashSet<string> copiedPaths)
    {
        if (!copiedPaths.Add(relativePath))
        {
            return;
        }

        CopyFile(Path.Combine(sourceRelease, relativePath), Path.Combine(destinationRelease, relativePath));
    }

    private static void CopyFile(string source, string destination)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    private static bool TryModuleNameFromRelativePath(string relativePath, out string moduleName)
    {
        moduleName = string.Empty;
        var fileName = Path.GetFileName(relativePath);
        foreach (var suffix in new[] { ".ko.zst", ".ko.xz", ".ko.gz", ".ko" })
        {
            if (!fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            moduleName = NormalizeModuleName(fileName[..^suffix.Length]);
            return true;
        }

        return false;
    }

    private static string ModuleNameFromRelativePath(string relativePath)
    {
        return TryModuleNameFromRelativePath(relativePath, out var moduleName)
            ? moduleName
            : throw new InvalidOperationException("module dependency path does not point to a kernel module: " + relativePath);
    }

    private static string NormalizeModuleName(string value)
        => value.Trim().Replace('_', '-');

    private static string NormalizeRelativePath(string value)
        => value.Replace('\\', '/').Trim();

    private static void ValidateSafeRelativeModulePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException("kernel module dependency path is invalid: " + relativePath);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

internal sealed record RecoveryKernelPruneResult(
    string KernelRelease,
    int CopiedModules,
    int CopiedFirmwareDirectories);
