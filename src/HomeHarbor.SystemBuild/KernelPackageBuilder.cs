namespace HomeHarbor.Tooling;

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
            _ => throw new InvalidOperationException($"unsupported kernel package build type: {build.Type}")
        };

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
        await File.WriteAllTextAsync(Path.Combine(recoveryRootfs, "etc", "fstab"), "LABEL=state /var ext4 defaults 0 2\nLABEL=esp /efi vfat umask=0077,nofail,x-systemd.device-timeout=30s 0 2\n", cancellationToken);
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
