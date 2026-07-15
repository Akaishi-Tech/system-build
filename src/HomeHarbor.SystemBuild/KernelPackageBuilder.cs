namespace HomeHarbor.Tooling;

internal sealed record ArchPkgbuildPrerequisites(
    IReadOnlyList<string> ValidPgpKeys,
    IReadOnlyList<string> MakeDependencies);

public sealed class KernelPackageBuilder(
    string root,
    string version,
    KernelPackageBuildChannelPlan channel,
    ICommandRunner? runner = null)
{
    private readonly string _root = BuildKeyDefaults.Apply(root);
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();
    private readonly RootlessBuildExecutor _rootless = new(runner ?? new ProcessCommandRunner());
    private readonly string _work = Path.Combine(Path.GetFullPath(root), ".work", "kernel", channel.Name, "build");

    public async Task BuildMissingAsync(CancellationToken cancellationToken = default)
    {
        if (channel.RequiredFiles.Any(file => !File.Exists(file.Path)))
        {
            foreach (var build in channel.ArtifactBuilds)
            {
                await ExecuteBuildAsync(build, null, cancellationToken);
            }
        }

        foreach (var addon in channel.Addons.Where(addon => !File.Exists(addon.Path)))
        {
            if (addon.Build is null)
            {
                throw new InvalidOperationException($"kernel addon {addon.Key} is missing and has no build step: {addon.Path}");
            }

            await ExecuteBuildAsync(addon.Build, addon, cancellationToken);
        }
    }

    private Task ExecuteBuildAsync(
        KernelPackageBuildCommandPlan build,
        KernelPackageAddonPlan? addon,
        CancellationToken cancellationToken)
        => build.Type switch
        {
            "zfs-lts-artifacts" => BuildZfsLtsArtifactsAsync(cancellationToken),
            "zfs-utils-addon" => BuildZfsUtilsAddonAsync(addon ?? throw new InvalidOperationException("zfs-utils addon build requires an addon"), cancellationToken),
            "arch-pkgbuild" => BuildArchPkgbuildAsync(
                channel.Kernel.Source?.PackageOutput ?? Path.Combine(_root, "artifacts", "kernel-packages", version, channel.Name),
                cancellationToken),
            _ => throw new InvalidOperationException($"unsupported kernel package build type: {build.Type}")
        };

    public async Task<IReadOnlyList<string>> BuildArchPkgbuildAsync(
        string packageOutput,
        CancellationToken cancellationToken = default)
    {
        if (channel.Kernel.Origin != "source-build")
        {
            throw new InvalidOperationException("arch-pkgbuild requires kernel origin=source-build");
        }
        var source = channel.Kernel.Source
            ?? throw new InvalidOperationException("arch-pkgbuild requires a pinned kernel source");
        if (string.IsNullOrWhiteSpace(source.GitUrl) ||
            string.IsNullOrWhiteSpace(source.GitRef) ||
            source.GitRef.Length != 40 ||
            !source.GitRef.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("arch-pkgbuild requires gitUrl and a full 40-hex gitRef");
        }

        var checkout = source.Path ?? Path.Combine(_work, "source");
        var managedRoot = Path.Combine(_root, ".work");
        if (!SecurityGuards.IsInsideDirectory(checkout, managedRoot) ||
            string.Equals(Path.GetFullPath(checkout), Path.GetFullPath(managedRoot), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("arch-pkgbuild source path must be a child of the repository .work directory");
        }

        await _rootless.RequireReadyAsync(cancellationToken);
        foreach (var tool in new[] { "git", "makepkg", "updpkgsums", "bsdtar", "pacman" })
        {
            await NeedAsync(tool, cancellationToken);
        }
        await DeleteWorkDirectoryAsync(checkout, cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
        await RunAsync(
            "git",
            ["clone", "--filter=blob:none", "--no-checkout", source.GitUrl, checkout],
            cancellationToken);
        await RunAsync(
            "git",
            ["-C", checkout, "checkout", "--detach", source.GitRef],
            cancellationToken);
        var head = (await CaptureAsync(
            "git",
            ["-C", checkout, "rev-parse", "HEAD"],
            cancellationToken)).Trim();
        if (!string.Equals(head, source.GitRef, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"kernel checkout resolved to {head}, not pinned ref {source.GitRef}");
        }

        var pkgbuild = Path.Combine(checkout, "PKGBUILD");
        var config = Path.Combine(checkout, "config");
        if (!File.Exists(pkgbuild) || !File.Exists(config))
        {
            throw new InvalidOperationException("Arch kernel packaging checkout must contain PKGBUILD and config");
        }
        RewriteKernelPackageBase(pkgbuild, channel.Kernel.Package);
        MergeKernelConfig(config, channel.ConfigFragments);
        ValidateRequiredKernelConfig(config, channel.RequiredConfig);
        await RunAsCurrentUserAsync("updpkgsums", [], cancellationToken, workingDirectory: checkout);

        var output = Path.GetFullPath(packageOutput);
        _ = Directory.CreateDirectory(output);
        var buildRoot = Path.Combine(_work, "makepkg");
        var gnupgHome = Path.Combine(_work, "gnupg");
        await DeleteWorkDirectoryAsync(gnupgHome, cancellationToken);
        _ = Directory.CreateDirectory(buildRoot);
        _ = Directory.CreateDirectory(gnupgHome);
        File.SetUnixFileMode(
            gnupgHome,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>
        {
            ["BUILDDIR"] = buildRoot,
            ["GNUPGHOME"] = gnupgHome,
            ["HOME"] = Path.Combine(_work, "home"),
            ["LOGNAME"] = Environment.UserName,
            ["PKGDEST"] = output,
            ["SRCDEST"] = Path.Combine(_work, "sources"),
            ["USER"] = Environment.UserName,
            ["XDG_CACHE_HOME"] = Path.Combine(_work, "home", ".cache")
        };
        _ = Directory.CreateDirectory(environment["HOME"]);
        _ = Directory.CreateDirectory(environment["SRCDEST"]);
        var sourceInfo = await _runner.RunAsync(
            "makepkg",
            ["--printsrcinfo"],
            RootlessBuildExecutor.IsolatedOptions(new CommandRunOptions(
                WorkingDirectory: checkout,
                StreamError: true,
                Timeout: TimeSpan.FromMinutes(1),
                EnvironmentOverride: environment)),
            cancellationToken);
        _ = sourceInfo.EnsureSuccess("could not read Arch kernel PKGBUILD metadata");
        var prerequisites = ParseArchPkgbuildPrerequisites(
            sourceInfo.Stdout,
            CurrentMakepkgArchitecture());
        if (prerequisites.ValidPgpKeys.Count > 0)
        {
            await NeedAsync("gpg", cancellationToken);
            await ImportExactArchPgpKeysAsync(
                prerequisites.ValidPgpKeys,
                checkout,
                gnupgHome,
                environment,
                cancellationToken);
        }
        await RequireArchMakeDependenciesAsync(
            prerequisites.MakeDependencies,
            environment,
            cancellationToken);

        var result = await _runner.RunAsync(
            "makepkg",
            ["--force", "--cleanbuild", "--clean", "--nodeps", "--noconfirm"],
            RootlessBuildExecutor.IsolatedOptions(new CommandRunOptions(
                WorkingDirectory: checkout,
                StreamOutput: true,
                StreamError: true,
                EnvironmentOverride: environment)),
            cancellationToken);
        _ = result.EnsureSuccess("Arch linux-lts PKGBUILD failed");

        var packages = Directory.GetFiles(output, channel.Kernel.Package + "-*.pkg.tar.*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException($"arch-pkgbuild produced no {channel.Kernel.Package} package archives");
        }
        await ValidateBuiltKernelConfigAsync(packages, cancellationToken);
        return packages;
    }

    internal static ArchPkgbuildPrerequisites ParseArchPkgbuildPrerequisites(
        string sourceInfo,
        string architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture) || architecture.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("makepkg architecture is invalid");
        }

        var validPgpKeys = new List<string>();
        var makeDependencies = new List<string>();
        foreach (var raw in sourceInfo.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key == "validpgpkeys")
            {
                validPgpKeys.Add(NormalizeFullOpenPgpFingerprint(value));
            }
            else if (key == "makedepends" || key == "makedepends_" + architecture)
            {
                if (value.Length == 0 || value[0] == '-' || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
                {
                    throw new InvalidOperationException("PKGBUILD contains an invalid makedepends entry");
                }
                makeDependencies.Add(value);
            }
        }

        return new ArchPkgbuildPrerequisites(
            validPgpKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            makeDependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<string> ParsePrimaryOpenPgpFingerprints(string colonOutput)
    {
        var fingerprints = new List<string>();
        var awaitingPrimaryFingerprint = false;
        foreach (var raw in colonOutput.Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split(':');
            if (fields.Length == 0)
            {
                continue;
            }

            if (fields[0] == "pub")
            {
                awaitingPrimaryFingerprint = true;
                continue;
            }
            if (fields[0] == "sub" || fields[0] == "sec" || fields[0] == "ssb")
            {
                awaitingPrimaryFingerprint = false;
                continue;
            }
            if (awaitingPrimaryFingerprint && fields[0] == "fpr")
            {
                if (fields.Length <= 9)
                {
                    throw new InvalidOperationException("gpg returned a malformed primary-key fingerprint record");
                }
                fingerprints.Add(NormalizeFullOpenPgpFingerprint(fields[9]));
                awaitingPrimaryFingerprint = false;
            }
        }

        return fingerprints.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static void RequireExactPrimaryOpenPgpFingerprints(
        IReadOnlyList<string> requested,
        string colonOutput)
    {
        var expected = requested.Select(NormalizeFullOpenPgpFingerprint)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = ParsePrimaryOpenPgpFingerprints(colonOutput).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "isolated OpenPGP keyring did not contain exactly the requested primary fingerprints; " +
                "requested=" + string.Join(',', expected) + "; imported=" + string.Join(',', actual));
        }
    }

    internal static void RequireSatisfiedMakeDependencies(
        IReadOnlyList<string> requested,
        CommandResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var missing = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var detail = missing.Length > 0
            ? string.Join(", ", missing)
            : string.Join(", ", requested);
        throw new InvalidOperationException(
            "Arch kernel build prerequisites are not satisfied: " + detail + ". " +
            "Install packages satisfying these makedepends expressions and rerun; " +
            "makepkg --nodeps is intentionally gated by this preflight." +
            (string.IsNullOrWhiteSpace(result.Stderr)
                ? string.Empty
                : Environment.NewLine + result.Stderr.Trim()));
    }

    private static string NormalizeFullOpenPgpFingerprint(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length is not (40 or 64) || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "validpgpkeys must contain full 40- or 64-hex OpenPGP fingerprints, not short key IDs");
        }
        return normalized;
    }

    private static string CurrentMakepkgArchitecture()
        => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "i686",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.Arm => "armv7h",
            var architecture => throw new InvalidOperationException(
                "unsupported makepkg host architecture: " + architecture)
        };

    private async Task ImportExactArchPgpKeysAsync(
        IReadOnlyList<string> fingerprints,
        string checkout,
        string gnupgHome,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        foreach (var fingerprint in fingerprints)
        {
            var pinnedKeyFile = RootPathGuard.RequireChildPath(
                Path.Combine(checkout, "keys", "pgp", fingerprint + ".asc"),
                checkout,
                "repository-pinned OpenPGP key");
            if (!File.Exists(pinnedKeyFile))
            {
                throw new InvalidOperationException(
                    "pinned Arch kernel packaging checkout does not contain a regular OpenPGP key file for " +
                    fingerprint + ": " + pinnedKeyFile);
            }

            var import = await _runner.RunAsync(
                "gpg",
                [
                    "--no-options",
                    "--batch",
                    "--homedir", gnupgHome,
                    "--import", pinnedKeyFile
                ],
                RootlessBuildExecutor.IsolatedOptions(new CommandRunOptions(
                    StreamOutput: true,
                    StreamError: true,
                    Timeout: TimeSpan.FromMinutes(2),
                    EnvironmentOverride: environment)),
                cancellationToken);
            _ = import.EnsureSuccess(
                "could not import repository-pinned OpenPGP fingerprint " + fingerprint);
        }

        var listed = await _runner.RunAsync(
            "gpg",
            ["--no-options", "--batch", "--homedir", gnupgHome, "--with-colons", "--fingerprint", "--list-keys"],
            RootlessBuildExecutor.IsolatedOptions(new CommandRunOptions(
                StreamError: true,
                Timeout: TimeSpan.FromMinutes(1),
                EnvironmentOverride: environment)),
            cancellationToken);
        _ = listed.EnsureSuccess("could not verify the isolated OpenPGP keyring");
        RequireExactPrimaryOpenPgpFingerprints(fingerprints, listed.Stdout);
    }

    private async Task RequireArchMakeDependenciesAsync(
        IReadOnlyList<string> dependencies,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        if (dependencies.Count == 0)
        {
            return;
        }

        var result = await _runner.RunAsync(
            "pacman",
            ["-T", "--", .. dependencies],
            RootlessBuildExecutor.IsolatedOptions(new CommandRunOptions(
                StreamError: true,
                Timeout: TimeSpan.FromMinutes(1),
                EnvironmentOverride: environment)),
            cancellationToken);
        RequireSatisfiedMakeDependencies(dependencies, result);
    }

    private static void RewriteKernelPackageBase(string pkgbuild, string packageName)
    {
        var lines = File.ReadAllLines(pkgbuild).ToList();
        var index = lines.FindIndex(line => line.StartsWith("pkgbase=", StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException("Arch kernel PKGBUILD has no pkgbase assignment");
        }
        lines[index] = "pkgbase=" + packageName;
        File.WriteAllLines(pkgbuild, lines);
    }

    private static void MergeKernelConfig(string configPath, IReadOnlyList<string> fragments)
    {
        var values = ParseKernelConfig(File.ReadAllLines(configPath));
        foreach (var fragment in fragments)
        {
            if (!File.Exists(fragment))
            {
                throw new FileNotFoundException("kernel config fragment not found", fragment);
            }
            foreach (var (key, value) in ParseKernelConfig(File.ReadAllLines(fragment)))
            {
                values[key] = value;
            }
        }

        File.WriteAllLines(
            configPath,
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value == "n" ? $"# {pair.Key} is not set" : $"{pair.Key}={pair.Value}"));
    }

    private static Dictionary<string, string> ParseKernelConfig(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("# CONFIG_", StringComparison.Ordinal) && line.EndsWith(" is not set", StringComparison.Ordinal))
            {
                var key = line[2..^11];
                result[key] = "n";
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals > 0 && line.StartsWith("CONFIG_", StringComparison.Ordinal))
            {
                result[line[..equals]] = line[(equals + 1)..];
            }
        }
        return result;
    }

    private static void ValidateRequiredKernelConfig(string configPath, IReadOnlyList<string> required)
    {
        var values = ParseKernelConfig(File.ReadLines(configPath));
        foreach (var requirement in required)
        {
            var equals = requirement.IndexOf('=');
            var key = requirement[..equals];
            var value = requirement[(equals + 1)..];
            if (!values.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"kernel config requires {requirement}, got {key}={actual ?? "missing"}");
            }
        }
    }

    private async Task ValidateBuiltKernelConfigAsync(
        IReadOnlyList<string> packages,
        CancellationToken cancellationToken)
    {
        var headers = packages.FirstOrDefault(path => Path.GetFileName(path).StartsWith(channel.Kernel.Package + "-headers-", StringComparison.Ordinal));
        if (headers is null)
        {
            throw new InvalidOperationException("arch-pkgbuild did not produce the kernel headers archive needed for config validation");
        }
        var result = await _runner.RunAsync(
            "bsdtar",
            ["-tf", headers],
            cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("could not inspect built kernel headers archive");
        var configMember = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(path => path.EndsWith("/build/.config", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("built kernel headers archive has no .config");
        var config = await _runner.RunAsync(
            "bsdtar",
            ["-xOf", headers, configMember],
            cancellationToken: cancellationToken);
        _ = config.EnsureSuccess("could not extract built kernel config");
        var temp = Path.Combine(_work, "validated.config");
        await File.WriteAllTextAsync(temp, config.Stdout, cancellationToken);
        ValidateRequiredKernelConfig(temp, channel.RequiredConfig);
    }

    private async Task BuildZfsLtsArtifactsAsync(CancellationToken cancellationToken)
    {
        var kernelPackage = channel.Kernel.Package;
        var kernelOrigin = channel.Kernel.Origin;
        var module = channel.Module ?? throw new InvalidOperationException("zfs LTS build requires a module package");
        if (kernelPackage != "linux-lts" || kernelOrigin != "upstream-arch-binary")
        {
            throw new InvalidOperationException($"zfs channel must use linux-lts from upstream-arch-binary, got {kernelPackage} from {kernelOrigin}");
        }

        await _rootless.RequireReadyAsync(cancellationToken);
        await NeedAsync("pacstrap", cancellationToken);
        await NeedAsync("arch-chroot", cancellationToken);
        await NeedAsync("bsdtar", cancellationToken);
        await NeedAsync("repo-add", cancellationToken);
        await NeedAsync("dump.erofs", cancellationToken);
        await NeedAsync("modinfo", cancellationToken);
        await NeedAsync("cc", cancellationToken);
        await NeedAsync("zstd", cancellationToken);

        await DeleteWorkDirectoryAsync(_work, cancellationToken);
        _ = Directory.CreateDirectory(_work);
        var rootfs = Path.Combine(_work, "rootfs");
        var recoveryRootfs = Path.Combine(_work, "recovery-rootfs");
        _ = Directory.CreateDirectory(rootfs);
        _ = Directory.CreateDirectory(recoveryRootfs);

        var packageRepository = await RequireSystemPackageRepositoryAsync(
            Path.Combine(_work, "repository"),
            cancellationToken);
        var systemPlan = SystemImageBuildDescriptor.LoadDefaultPlan(_root, version);

        var modulePackageFile = await ResolveModulePackageAsync(module, cancellationToken);
        var buildRootPackages = systemPlan.Packages.Recovery
            .Concat([
                kernelPackage,
                "linux-firmware-broadcom",
                "linux-firmware-intel",
                "linux-firmware-realtek",
                "linux-firmware-other",
                "linux-firmware-whence",
                "mkinitcpio",
                "cryptsetup",
                "mdadm",
                "btrfs-progs",
                "xfsprogs"
            ])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await RunPacstrapAsync(
            rootfs,
            buildRootPackages,
            cancellationToken,
            pacmanConfig: packageRepository.PacmanConfigPath);
        PrepareWritableRootfs(rootfs);

        var moduleRoot = Path.Combine(_work, "module-root");
        _ = Directory.CreateDirectory(moduleRoot);
        await RunMappedRootAsync("bsdtar", ["-xpf", modulePackageFile, "-C", moduleRoot, "usr/lib/modules/*"], cancellationToken);
        CopyDirectory(Path.Combine(moduleRoot, "usr", "lib", "modules"), Path.Combine(rootfs, "usr", "lib", "modules"));

        var kernelReleases = Directory.GetDirectories(Path.Combine(rootfs, "usr", "lib", "modules"))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (kernelReleases.Length != 1)
        {
            throw new InvalidOperationException($"expected exactly one zfs linux-lts modules directory, found {kernelReleases.Length}");
        }

        var kernelRelease = kernelReleases[0]!;
        await KernelConfigValidator.ValidateAsync(
            Path.Combine(rootfs, "usr", "lib", "modules", kernelRelease, "vmlinuz"),
            Path.Combine(_work, "kernel-config"),
            _runner,
            cancellationToken);
        if (Directory.GetFiles(Path.Combine(rootfs, "usr", "lib", "modules", kernelRelease), "zfs.ko*", SearchOption.AllDirectories).Length == 0)
        {
            throw new InvalidOperationException($"{module.Package} did not provide zfs.ko for {kernelRelease}");
        }

        var avbHelper = Path.Combine(_work, "homeharbor-avb");
        var initHelper = Path.Combine(_work, "homeharbor-verity");
        await BuildAvbHelperAsync(avbHelper, cancellationToken);
        await BuildInitHelperAsync(initHelper, cancellationToken);
        InstallFile(avbHelper, Path.Combine(rootfs, "usr", "lib", "homeharbor", "homeharbor-avb"), executable: true);
        InstallFile(initHelper, Path.Combine(rootfs, "boot", "init", "homeharbor-verity"), executable: true);
        InstallFile(SystemUtilityAssets.RequireAsset(_root, "os", "mkinitcpio", "install", "homeharbor-verity"), Path.Combine(rootfs, "etc", "initcpio", "install", "homeharbor-verity"), executable: true);
        InstallFile(SystemUtilityAssets.RequireAsset(_root, "os", "mkinitcpio", "hooks", "homeharbor-verity"), Path.Combine(rootfs, "etc", "initcpio", "hooks", "homeharbor-verity"), executable: true);
        RewriteMkinitcpioHooks(Path.Combine(rootfs, "etc", "mkinitcpio.conf"));

        await RunMappedChrootAsync(rootfs, "depmod", ["-a", kernelRelease], cancellationToken);
        await RunMappedChrootAsync(rootfs, "mkinitcpio", ["-p", kernelPackage], cancellationToken);

        var vmlinuz = RequiredFile("vmlinuz");
        var initramfs = RequiredFile("initramfs");
        var modules = RequiredFile("modules");
        var firmware = RequiredFile("firmware");
        var recovery = RequiredFile("recovery");
        InstallFile(Path.Combine(rootfs, "boot", "vmlinuz-" + kernelPackage), vmlinuz);
        InstallFile(Path.Combine(rootfs, "boot", "initramfs-" + kernelPackage + ".img"), initramfs);
        WriteSha256(vmlinuz);
        WriteSha256(initramfs);

        var modulesRoot = Path.Combine(_work, "modules-root");
        var firmwareRoot = Path.Combine(_work, "firmware-root");
        Directory.Move(Path.Combine(rootfs, "usr", "lib", "modules"), modulesRoot);
        Directory.Move(Path.Combine(rootfs, "usr", "lib", "firmware"), firmwareRoot);
        var erofs = await SelinuxErofsTool.CreateAsync(
            packageRepository.PackageDirectory,
            Path.Combine(_work, "selinux-erofs-tool"),
            _runner,
            cancellationToken);
        await BuildRecoveryRootfsAsync(
            recoveryRootfs,
            modulesRoot,
            firmwareRoot,
            avbHelper,
            kernelRelease,
            vmlinuz,
            initramfs,
            recovery,
            packageRepository,
            erofs,
            cancellationToken);
        var firmwarePrune = await MainFirmwareTreePruner.PruneAsync(modulesRoot, firmwareRoot, _runner, cancellationToken);
        Console.WriteLine($"Pruned firmware tree for {firmwarePrune.KernelRelease}: kept {firmwarePrune.KeptEntries} entries ({firmwarePrune.KeptBytes} bytes), removed {firmwarePrune.RemovedEntries} entries ({firmwarePrune.OriginalBytes - firmwarePrune.KeptBytes} bytes)");
        var fileContexts = SelinuxErofsTool.RequireFileContexts(rootfs);
        await erofs.BuildAsync(modules, modulesRoot, fileContexts, "/usr/lib/modules", ["-zlz4hc,12"], cancellationToken);
        await erofs.BuildAsync(firmware, firmwareRoot, fileContexts, "/usr/lib/firmware", ["-zlz4hc,12"], cancellationToken);
        await RunAsync("dump.erofs", ["-s", modules], cancellationToken);
        await RunAsync("dump.erofs", ["-s", firmware], cancellationToken);
        WriteSha256(modules);
        WriteSha256(firmware);
    }

    private async Task BuildRecoveryRootfsAsync(
        string recoveryRootfs,
        string modulesRoot,
        string firmwareRoot,
        string avbHelper,
        string kernelRelease,
        string vmlinuz,
        string initramfs,
        string recovery,
        ArchLocalPackageRepository packageRepository,
        SelinuxErofsTool erofs,
        CancellationToken cancellationToken)
    {
        var systemPlan = SystemImageBuildDescriptor.LoadDefaultPlan(_root, version);
        await RunPacstrapAsync(
            recoveryRootfs,
            systemPlan.Packages.Recovery,
            cancellationToken,
            pacmanConfig: packageRepository.PacmanConfigPath);
        PrepareWritableRootfs(recoveryRootfs);
        InstallFile(avbHelper, Path.Combine(recoveryRootfs, "usr", "lib", "homeharbor", "homeharbor-avb"), executable: true);
        _ = Directory.CreateDirectory(Path.Combine(recoveryRootfs, "etc", "homeharbor"));
        _ = Directory.CreateDirectory(Path.Combine(recoveryRootfs, "efi"));
        var recoveryDataMountPoint = Directory.CreateDirectory(Path.Combine(recoveryRootfs, "homeharbor-data"));
        File.SetUnixFileMode(
            recoveryDataMountPoint.FullName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        await File.WriteAllTextAsync(
            Path.Combine(recoveryRootfs, "etc", "fstab"),
            SystemImageBuilder.RenderPlanFstab(SystemImageBuilder.MergeRecoveryFstab(
                systemPlan.Rootfs.Fstab,
                systemPlan.Recovery.Fstab)),
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(recoveryRootfs, "etc", "hostname"), "homeharbor-recovery\n", cancellationToken);
        var shells = Path.Combine(recoveryRootfs, "etc", "shells");
        var shell = "/usr/lib/homeharbor/recovery/HomeHarbor.Recovery";
        var existingShells = File.Exists(shells) ? await File.ReadAllTextAsync(shells, cancellationToken) : string.Empty;
        if (!existingShells.Split('\n').Contains(shell))
        {
            await File.AppendAllTextAsync(shells, shell + "\n", cancellationToken);
        }

        await RunMappedChrootAsync(recoveryRootfs, "useradd", ["--system", "--user-group", "--home-dir", "/var/lib/homeharbor/recovery", "--create-home", "--shell", shell, "recovery"], cancellationToken, allowFailure: true);
        await RunMappedChrootAsync(recoveryRootfs, "systemctl", ["enable", .. systemPlan.Recovery.SystemdUnits], cancellationToken);
        _ = RecoveryKernelTreePruner.Prune(
            modulesRoot,
            firmwareRoot,
            Path.Combine(recoveryRootfs, "usr", "lib", "modules"),
            Path.Combine(recoveryRootfs, "usr", "lib", "firmware"));
        await RunMappedChrootAsync(recoveryRootfs, "depmod", ["-a", kernelRelease], cancellationToken);
        await SelinuxPolicyValidator.ValidateAsync(
            recoveryRootfs,
            "ZFS recovery rootfs",
            _rootless,
            cancellationToken);
        _ = SelinuxPolicyStoreSynchronizer.PrepareImmutableSeed(recoveryRootfs);

        var recoveryBoot = Path.Combine(_work, "recovery_boot.efi");
        await BuildUkiAsync(recoveryBoot, vmlinuz, initramfs, Path.Combine(recoveryRootfs, "etc", "os-release"), kernelRelease, SecureBootAssets.RecoveryCmdline(), cancellationToken);
        InstallFile(recoveryBoot, Path.Combine(recoveryRootfs, "boot", "recovery_boot.efi"));
        var hints = Path.Combine(_work, "recovery-compress-hints");
        await File.WriteAllTextAsync(hints, "0 boot/recovery_boot[.]efi\n", cancellationToken);
        await erofs.BuildAsync(
            recovery,
            recoveryRootfs,
            SelinuxErofsTool.RequireFileContexts(recoveryRootfs),
            "/",
            ["-E^inline_data", "-zlz4hc,12", "--compress-hints=" + hints],
            cancellationToken);
        await RunAsync("dump.erofs", ["-s", recovery], cancellationToken);
        WriteSha256(recovery);
    }

    private async Task BuildZfsUtilsAddonAsync(KernelPackageAddonPlan addon, CancellationToken cancellationToken)
    {
        if (addon.Key != "zfs-utils")
        {
            throw new InvalidOperationException($"unsupported addon build: {addon.Key}");
        }

        await _rootless.RequireReadyAsync(cancellationToken);
        await NeedAsync("git", cancellationToken);
        await NeedAsync("makepkg", cancellationToken);
        await NeedAsync("bsdtar", cancellationToken);
        await NeedAsync("repo-add", cancellationToken);
        await NeedAsync("dump.erofs", cancellationToken);
        var work = Path.Combine(_work, addon.Key);
        var packageOutput = addon.Source?.PackageOutput ?? Path.Combine(work, "packages");
        await DeleteWorkDirectoryAsync(work, cancellationToken);
        _ = Directory.CreateDirectory(Path.Combine(work, "payload-root"));
        _ = Directory.CreateDirectory(packageOutput);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(addon.Path)!);

        var package = addon.Source?.PackageFile;
        if (string.IsNullOrWhiteSpace(package))
        {
            if (Directory.GetFiles(packageOutput, "zfs-utils-*.pkg.tar.*").Length == 0)
            {
                if (addon.Origin != "source-build")
                {
                    throw new InvalidOperationException($"kernel addon {addon.Key} is marked {addon.Origin}, but no package file was provided");
                }

                await BuildSourcePackageAsync(
                    "zfs-utils",
                    addon.Source,
                    Path.Combine(work, "package-build"),
                    packageOutput,
                    cancellationToken);
            }

            package = Directory.GetFiles(packageOutput, "zfs-utils-*.pkg.tar.*").Order(StringComparer.Ordinal).LastOrDefault()
                ?? throw new InvalidOperationException($"no zfs-utils package found in {packageOutput}");
        }
        else if (!File.Exists(package))
        {
            throw new FileNotFoundException("kernel addon zfs-utils packageFile is missing", package);
        }

        await RunMappedRootAsync("bsdtar", ["-xpf", package, "-C", Path.Combine(work, "payload-root"), "usr/*"], cancellationToken);
        if (!File.Exists(Path.Combine(work, "payload-root", "usr", "bin", "zfs")) ||
            !File.Exists(Path.Combine(work, "payload-root", "usr", "bin", "zpool")))
        {
            throw new InvalidOperationException("zfs-utils addon must contain usr/bin/zfs and usr/bin/zpool");
        }

        var packageRepository = await RequireSystemPackageRepositoryAsync(
            Path.Combine(work, "repository"),
            cancellationToken);
        var policyRootfs = Path.Combine(work, "policy-rootfs");
        _ = Directory.CreateDirectory(policyRootfs);
        await RunPacstrapAsync(
            policyRootfs,
            ["base", "selinux-refpolicy-arch", "homeharbor-selinux-policy"],
            cancellationToken,
            pacmanConfig: packageRepository.PacmanConfigPath);
        PrepareWritableRootfs(policyRootfs);
        await SelinuxPolicyValidator.ValidateAsync(
            policyRootfs,
            "ZFS addon policy rootfs",
            _rootless,
            cancellationToken);
        var erofs = await SelinuxErofsTool.CreateAsync(
            packageRepository.PackageDirectory,
            Path.Combine(work, "selinux-erofs-tool"),
            _runner,
            cancellationToken);
        await erofs.BuildAsync(
            addon.Path,
            Path.Combine(work, "payload-root"),
            SelinuxErofsTool.RequireFileContexts(policyRootfs),
            "/",
            ["-zlz4hc,12"],
            cancellationToken);
        await RunAsync("dump.erofs", ["-s", addon.Path], cancellationToken);
        WriteSha256(addon.Path);
    }

    private async Task<string> ResolveModulePackageAsync(KernelPackageInputPlan module, CancellationToken cancellationToken)
    {
        var packageName = module.Package;
        if (!string.IsNullOrWhiteSpace(module.Source?.PackageFile))
        {
            return !File.Exists(module.Source.PackageFile)
                ? throw new FileNotFoundException($"{packageName} packageFile is missing", module.Source.PackageFile)
                : Path.GetFullPath(module.Source.PackageFile);
        }

        var output = module.Source?.PackageOutput ?? Path.Combine(_work, "packages");
        _ = Directory.CreateDirectory(output);
        if (SelectPackageFile(output, packageName) is null)
        {
            if (module.Origin != "source-build")
            {
                throw new InvalidOperationException($"{packageName} is marked {module.Origin}, but no package file was provided");
            }

            await BuildSourcePackageAsync(
                packageName,
                module.Source,
                Path.Combine(_work, "module-src"),
                output,
                cancellationToken);
        }

        return SelectPackageFile(output, packageName)
            ?? throw new InvalidOperationException($"no {packageName} package found in {output}");
    }

    private async Task<ArchLocalPackageRepository> RequireSystemPackageRepositoryAsync(
        string configDirectory,
        CancellationToken cancellationToken)
    {
        var packageDirectory = Path.Combine(_root, ".work", "image", "packages");
        await ArchPackageSetProvenance.VerifyAsync(
            _root,
            version,
            packageDirectory,
            cancellationToken);
        if (!Directory.Exists(packageDirectory) ||
            Directory.GetFiles(packageDirectory, "homeharbor-recovery-*.pkg.tar.*", SearchOption.TopDirectoryOnly).Length != 1 ||
            Directory.GetFiles(packageDirectory, "selinux-refpolicy-arch-*.pkg.tar.*", SearchOption.TopDirectoryOnly).Length != 1 ||
            Directory.GetFiles(packageDirectory, "erofs-utils-selinux-*.pkg.tar.*", SearchOption.TopDirectoryOnly).Length != 1)
        {
            throw new InvalidOperationException(
                "the locally built HomeHarbor SELinux package set is unavailable; run system-build before building kernel-channel artifacts: " +
                packageDirectory);
        }

        return await ArchLocalPackageRepositoryBuilder.CreateAsync(
            packageDirectory,
            configDirectory,
            _runner,
            cancellationToken);
    }

    private static string? SelectPackageFile(string output, string packageName)
        => Directory.GetFiles(output, packageName + "-*.pkg.tar.*")
            .Where(file => string.Equals(PackageNameFromPackageFile(file), packageName, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .LastOrDefault();

    private static string? PackageNameFromPackageFile(string packageFile)
    {
        var fileName = Path.GetFileName(packageFile);
        var marker = fileName.IndexOf(".pkg.tar.", StringComparison.Ordinal);
        if (marker <= 0)
        {
            return null;
        }

        var stem = fileName[..marker];
        for (var i = 0; i < 3; i++)
        {
            var separator = stem.LastIndexOf('-');
            if (separator <= 0)
            {
                return null;
            }

            stem = stem[..separator];
        }

        return stem.Length == 0 ? null : stem;
    }

    private async Task BuildSourcePackageAsync(
        string packageName,
        KernelPackageSourcePlan? source,
        string work,
        string output,
        CancellationToken cancellationToken)
    {
        await NeedAsync("git", cancellationToken);
        await NeedAsync("makepkg", cancellationToken);
        await NeedAsync("rsync", cancellationToken);
        if (source?.PgpKeys.Count > 0)
        {
            await NeedAsync("gpg", cancellationToken);
        }

        await _rootless.RequireNonRootAsync(cancellationToken);
        _ = Directory.CreateDirectory(work);
        _ = Directory.CreateDirectory(output);
        var src = Path.Combine(work, "src");
        if (!string.IsNullOrWhiteSpace(source?.Path))
        {
            if (!Directory.Exists(source.Path))
            {
                throw new DirectoryNotFoundException($"{packageName} source directory not found: {source.Path}");
            }

            await RunAsCurrentUserAsync("rsync", ["-a", "--delete", source.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, src + Path.DirectorySeparatorChar], cancellationToken);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(source?.GitUrl))
            {
                throw new InvalidOperationException($"{packageName} source-build requires source.path or source.gitUrl");
            }

            var cloneArguments = new List<string> { "clone", "--depth=1" };
            if (!string.IsNullOrWhiteSpace(source.GitRef))
            {
                cloneArguments.Add("--branch");
                cloneArguments.Add(source.GitRef);
                cloneArguments.Add("--single-branch");
            }

            cloneArguments.Add(source.GitUrl);
            cloneArguments.Add(src);
            await RunAsCurrentUserAsync(
                "git",
                cloneArguments,
                cancellationToken,
                environment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
        }

        await ImportPgpKeysAsync(source?.PgpKeys ?? [], cancellationToken);
        await RunAsCurrentUserAsync("makepkg", ["--force", "--noconfirm"], cancellationToken, workingDirectory: src);
        foreach (var file in Directory.GetFiles(src, packageName + "-*.pkg.tar.*"))
        {
            File.Copy(file, Path.Combine(output, Path.GetFileName(file)), overwrite: true);
        }
    }

    private async Task ImportPgpKeysAsync(IEnumerable<string> pgpKeys, CancellationToken cancellationToken)
    {
        foreach (var pgpKey in pgpKeys.Distinct(StringComparer.Ordinal))
        {
            var present = await _runner.RunAsync(
                "gpg",
                ["--batch", "--list-keys", pgpKey],
                new CommandRunOptions(ThrowOnStartFailure: false),
                cancellationToken);
            if (present.ExitCode == 0)
            {
                continue;
            }

            var result = await _runner.RunAsync(
                "gpg",
                ["--batch", "--keyserver", "hkps://keyserver.ubuntu.com", "--recv-keys", pgpKey],
                new CommandRunOptions(StreamOutput: true, StreamError: true, Timeout: TimeSpan.FromMinutes(2)),
                cancellationToken);
            _ = result.EnsureSuccess("could not import required OpenPGP key " + pgpKey);
        }
    }

    private async Task BuildAvbHelperAsync(string output, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await RunAsync("cc", ["-O2", "-Wall", "-Wextra", "-o", output, SystemUtilityAssets.RequireAsset(_root, "boot", "avb", "homeharbor-avb.c"), "-lcrypto"], cancellationToken);
        SetExecutable(output);
    }

    private async Task BuildInitHelperAsync(string output, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await RunAsync(
            "cc",
            HomeHarborInitHelperBuild.CompileArguments(
                output,
                SystemUtilityAssets.RequireAsset(_root, "boot", "init", "homeharbor-verity.c")),
            cancellationToken);
        SetExecutable(output);
    }

    private async Task BuildUkiAsync(
        string output,
        string linux,
        string initrd,
        string osRelease,
        string uname,
        string cmdline,
        CancellationToken cancellationToken)
    {
        var ukify = await FindUkifyAsync(cancellationToken);
        var cmdlineFile = Path.Combine(_work, "cmdline-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(cmdlineFile, cmdline + "\n", cancellationToken);
        var args = new List<string>
        {
            "build",
            "--linux=" + linux,
            "--initrd=" + initrd,
            "--os-release=@" + osRelease,
            "--uname=" + uname,
            "--cmdline=@" + cmdlineFile,
            "--output=" + output
        };
        if (SecureBootAssets.IsEnabled())
        {
            var (Key, Certificate) = SecureBootAssets.RequireSigningAssets();
            args.Insert(args.Count - 1, "--secureboot-private-key=" + Key);
            args.Insert(args.Count - 1, "--secureboot-certificate=" + Certificate);
        }

        await RunAsync(ukify, args, cancellationToken);
        File.Delete(cmdlineFile);
    }

    private async Task<string> FindUkifyAsync(CancellationToken cancellationToken)
    {
        if (File.Exists("/usr/lib/systemd/ukify"))
        {
            return "/usr/lib/systemd/ukify";
        }

        var result = await _runner.RunAsync("ukify", ["--version"], cancellationToken: cancellationToken);
        return result.ExitCode == 0
            ? "ukify"
            : throw new InvalidOperationException("missing required Secure Boot tool: ukify or /usr/lib/systemd/ukify");
    }

    private string RequiredFile(string label)
        => channel.RequiredFiles.Single(file => file.Label == label).Path;

    private async Task NeedAsync(string command, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(command, ["--version"], new CommandRunOptions(ThrowOnStartFailure: false), cancellationToken);
        if (result.ExitCode == 127)
        {
            throw new InvalidOperationException("missing required tool: " + command);
        }
    }

    private async Task RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        bool allowFailure = false)
    {
        var result = await _runner.RunAsync(
            fileName,
            arguments,
            new CommandRunOptions(StandardInput: standardInput, StreamOutput: true, StreamError: true),
            cancellationToken);
        if (!allowFailure)
        {
            _ = result.EnsureSuccess();
        }
    }

    private async Task<string> CaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            fileName,
            arguments,
            new CommandRunOptions(StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess();
        return result.Stdout;
    }

    private async Task RunPacstrapAsync(
        string rootfs,
        IEnumerable<string> packages,
        CancellationToken cancellationToken,
        string? standardInput = null,
        string? pacmanConfig = null)
    {
        var result = await _rootless.RunPacstrapAsync(
            rootfs,
            packages,
            new CommandRunOptions(StandardInput: standardInput, StreamOutput: true, StreamError: true),
            pacmanConfig,
            cancellationToken: cancellationToken);
        _ = result.EnsureSuccess();
    }

    private async Task RunMappedRootAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        bool allowFailure = false)
    {
        var result = await _rootless.RunMappedRootAsync(
            fileName,
            arguments,
            new CommandRunOptions(StandardInput: standardInput, StreamOutput: true, StreamError: true),
            cancellationToken);
        if (!allowFailure)
        {
            _ = result.EnsureSuccess();
        }
    }

    private async Task RunMappedChrootAsync(
        string rootfs,
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        bool allowFailure = false)
    {
        var result = await _rootless.RunMappedChrootAsync(
            rootfs,
            fileName,
            arguments,
            new CommandRunOptions(StandardInput: standardInput, StreamOutput: true, StreamError: true),
            cancellationToken);
        if (!allowFailure)
        {
            _ = result.EnsureSuccess();
        }
    }

    private async Task RunAsCurrentUserAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        await _rootless.RequireNonRootAsync(cancellationToken);
        var options = RootlessBuildExecutor.IsolatedOptions(new CommandRunOptions(
            WorkingDirectory: workingDirectory,
            StreamOutput: true,
            StreamError: true,
            EnvironmentOverride: environment));
        var isolatedEnvironment = new Dictionary<string, string>(options.Environment, StringComparer.Ordinal)
        {
            ["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["LOGNAME"] = Environment.UserName,
            ["USER"] = Environment.UserName
        };
        options = options with { EnvironmentOverride = isolatedEnvironment };
        var result = await _runner.RunAsync(
            fileName,
            arguments,
            options,
            cancellationToken);
        _ = result.EnsureSuccess();
    }

    private async Task DeleteWorkDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path) || Path.GetFullPath(path) == Path.GetPathRoot(Path.GetFullPath(path)))
        {
            throw new InvalidOperationException("refusing to remove unsafe kernel build work directory: " + path);
        }

        var result = await _rootless.RunMappedRootAsync(
            "rm",
            ["-rf", "--", path],
            new CommandRunOptions(StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess("could not remove existing kernel build work directory; old rootful files or stale mounts may remain");
    }

    private static void RewriteMkinitcpioHooks(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("HOOKS=", StringComparison.Ordinal))
            {
                lines[i] = "HOOKS=(base udev autodetect modconf kms keyboard keymap consolefont block homeharbor-verity filesystems fsck)";
            }
        }

        File.WriteAllLines(path, lines);
    }

    private static void InstallFile(string source, string destination, bool executable = false)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        if (executable)
        {
            SetExecutable(destination);
        }
    }

    private static void WriteSha256(string path)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        File.WriteAllText(path + ".sha256", hash + "\n");
    }

    private static void CopyDirectory(string source, string destination)
    {
        FileTreeCopier.CopyDirectory(source, destination);
    }

    private static void SetExecutable(string path)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void PrepareWritableRootfs(string rootfs)
    {
        File.SetUnixFileMode(
            rootfs,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        RootlessBuildExecutor.ConfigureSystemdResolved(rootfs);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
