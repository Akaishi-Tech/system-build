namespace HomeHarbor.Tooling;

internal static class MainFirmwareTreePruner
{
    internal static readonly string[] AllowedModulePathPrefixes =
    [
        "kernel/drivers/net/ethernet/",
        "kernel/drivers/net/usb/",
        "kernel/drivers/net/phy/",
        "kernel/drivers/net/mdio/",
        "kernel/drivers/net/pcs/",
        "kernel/drivers/infiniband/hw/hfi1/",
        "kernel/drivers/ata/",
        "kernel/drivers/scsi/",
        "kernel/drivers/nvme/",
        "kernel/drivers/usb/storage/",
        "kernel/drivers/mmc/host/"
    ];

    private static readonly string[] ForcedFirmwareDirectories =
    [
        "rtl_nic",
        "bnx2",
        "bnx2x",
        "tigon",
        "intel/ice"
    ];

    private static readonly string[] FirmwarePathSuffixes =
    [
        "",
        ".zst",
        ".xz",
        ".gz"
    ];

    public static async Task<MainFirmwarePruneResult> PruneAsync(
        string modulesRoot,
        string firmwareRoot,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        var releaseRoot = ResolveSingleKernelRelease(modulesRoot);
        var kernelRelease = Path.GetFileName(releaseRoot)
            ?? throw new InvalidOperationException("could not resolve kernel release for firmware pruning");
        if (!Directory.Exists(firmwareRoot))
        {
            throw new DirectoryNotFoundException(firmwareRoot);
        }

        var firmwareRootFull = Path.GetFullPath(firmwareRoot);
        var entriesBefore = EnumerateEntries(firmwareRootFull);
        var originalBytes = entriesBefore.Sum(EntrySize);
        var keepEntries = new HashSet<string>(StringComparer.Ordinal);
        var missingFirmwareReferences = 0;
        var modulesScanned = 0;
        var modulesWithFirmware = 0;
        var declaredFirmwareFiles = new HashSet<string>(StringComparer.Ordinal);

        KeepWhenceFiles(firmwareRootFull, keepEntries);
        foreach (var directory in ForcedFirmwareDirectories)
        {
            AddDirectoryEntries(firmwareRootFull, directory, keepEntries);
        }

        foreach (var module in EnumerateModuleFiles(releaseRoot))
        {
            var relativeModule = NormalizeRelativePath(Path.GetRelativePath(releaseRoot, module));
            if (!IsAllowedModulePath(relativeModule))
            {
                continue;
            }

            modulesScanned++;
            var result = await runner.RunAsync(
                "modinfo",
                ["-F", "firmware", module],
                new CommandRunOptions(ThrowOnStartFailure: true),
                cancellationToken);
            _ = result.EnsureSuccess("modinfo firmware extraction failed for " + relativeModule);

            var firmwareNames = result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            if (firmwareNames.Length > 0)
            {
                modulesWithFirmware++;
            }

            foreach (var firmwareName in firmwareNames)
            {
                var relativeFirmware = NormalizeRelativePath(firmwareName);
                ValidateSafeRelativeFirmwarePath(relativeFirmware);
                _ = declaredFirmwareFiles.Add(relativeFirmware);
                if (!AddFirmwareFileCandidates(firmwareRootFull, relativeFirmware, keepEntries))
                {
                    missingFirmwareReferences++;
                }
            }
        }

        var removedEntries = PruneEntries(firmwareRootFull, keepEntries);
        var entriesAfter = EnumerateEntries(firmwareRootFull);
        var keptBytes = entriesAfter.Sum(EntrySize);
        return new MainFirmwarePruneResult(
            kernelRelease,
            modulesScanned,
            modulesWithFirmware,
            declaredFirmwareFiles.Count,
            missingFirmwareReferences,
            entriesAfter.Count,
            removedEntries,
            originalBytes,
            keptBytes);
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
            ? throw new InvalidOperationException($"expected exactly one kernel modules directory for firmware pruning, found {releases.Length}")
            : releases[0];
    }

    private static IEnumerable<string> EnumerateModuleFiles(string releaseRoot)
        => Directory.EnumerateFiles(releaseRoot, "*.ko*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);

    private static bool IsAllowedModulePath(string relativeModule)
        => AllowedModulePathPrefixes.Any(prefix => relativeModule.StartsWith(prefix, StringComparison.Ordinal));

    private static void KeepWhenceFiles(string firmwareRoot, HashSet<string> keepEntries)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(firmwareRoot, "WHENCE*", SearchOption.TopDirectoryOnly))
        {
            AddExistingEntry(firmwareRoot, entry, keepEntries);
        }
    }

    private static void AddDirectoryEntries(string firmwareRoot, string relativeDirectory, HashSet<string> keepEntries)
    {
        var directory = Path.Combine(firmwareRoot, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in EnumerateEntries(directory))
        {
            AddExistingEntry(firmwareRoot, entry, keepEntries);
        }
    }

    private static bool AddFirmwareFileCandidates(string firmwareRoot, string relativeFirmware, HashSet<string> keepEntries)
    {
        var found = false;
        foreach (var suffix in FirmwarePathSuffixes)
        {
            var candidate = Path.Combine(firmwareRoot, relativeFirmware + suffix);
            if (!EntryExists(candidate))
            {
                continue;
            }

            AddExistingEntry(firmwareRoot, candidate, keepEntries);
            found = true;
        }

        return found;
    }

    private static void AddExistingEntry(string firmwareRoot, string entry, HashSet<string> keepEntries)
    {
        var fullPath = Path.GetFullPath(entry);
        if (!IsUnderRoot(firmwareRoot, fullPath) || !keepEntries.Add(fullPath))
        {
            return;
        }

        AddSymlinkTarget(firmwareRoot, fullPath, keepEntries);
    }

    private static void AddSymlinkTarget(string firmwareRoot, string entry, HashSet<string> keepEntries)
    {
        var target = LinkTarget(entry);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var targetPath = ResolveFirmwareLinkTarget(firmwareRoot, entry, target);
        if (targetPath is null || !IsUnderRoot(firmwareRoot, targetPath) || !EntryExists(targetPath))
        {
            return;
        }

        AddExistingEntry(firmwareRoot, targetPath, keepEntries);
    }

    private static string? ResolveFirmwareLinkTarget(string firmwareRoot, string linkPath, string target)
    {
        if (!Path.IsPathRooted(target))
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));
        }

        const string finalFirmwareRoot = "/usr/lib/firmware/";
        return target.StartsWith(finalFirmwareRoot, StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(firmwareRoot, target[finalFirmwareRoot.Length..]))
            : null;
    }

    private static int PruneEntries(string firmwareRoot, HashSet<string> keepEntries)
    {
        var removed = 0;
        foreach (var entry in EnumerateEntries(firmwareRoot)
            .OrderByDescending(path => path.Length)
            .ThenByDescending(path => path, StringComparer.Ordinal))
        {
            var fullPath = Path.GetFullPath(entry);
            if (IsRealDirectory(fullPath))
            {
                if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
                {
                    Directory.Delete(fullPath);
                    removed++;
                }

                continue;
            }

            if (keepEntries.Contains(fullPath))
            {
                continue;
            }

            DeleteEntry(fullPath);
            removed++;
        }

        return removed;
    }

    private static IReadOnlyList<string> EnumerateEntries(string root)
    {
        var entries = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entries.Add(entry);
                if (IsRealDirectory(entry))
                {
                    stack.Push(entry);
                }
            }
        }

        return entries;
    }

    private static bool EntryExists(string path)
        => File.Exists(path) ||
            Directory.Exists(path) ||
            new FileInfo(path).LinkTarget is not null ||
            new DirectoryInfo(path).LinkTarget is not null;

    private static string? LinkTarget(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return null;
        }

        return (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).LinkTarget
            : new FileInfo(path).LinkTarget;
    }

    private static bool IsRealDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Directory) != 0 &&
            (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static long EntrySize(string path)
    {
        if (IsRealDirectory(path))
        {
            return 0;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    private static void DeleteEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        File.Delete(path);
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." &&
            !relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static string NormalizeRelativePath(string value)
        => value.Replace('\\', '/').Trim();

    private static void ValidateSafeRelativeFirmwarePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException("firmware path reported by modinfo is invalid: " + relativePath);
        }
    }
}

internal sealed record MainFirmwarePruneResult(
    string KernelRelease,
    int ModulesScanned,
    int ModulesWithFirmware,
    int DeclaredFirmwareFiles,
    int MissingFirmwareReferences,
    int KeptEntries,
    int RemovedEntries,
    long OriginalBytes,
    long KeptBytes);
