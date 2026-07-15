using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeHarbor.Tooling;

public sealed record ReleaseArtifactBuildResult(
    string ReleaseDirectory,
    string SystemOta,
    string KernelOta,
    string ChannelFile,
    string LatestChannelFile,
    string InstallerIso);

public sealed partial class ReleaseArtifactBuilder(
    string root,
    string version,
    SystemImageBuildPlan plan,
    ICommandRunner? runner = null)
{
    internal static readonly string LiveInstallerPolicyModulesErofsPath =
        "/var/lib/selinux/refpolicy-arch/active/modules";
    internal static readonly string SystemPolicyStoreErofsPath =
        "/usr/lib/homeharbor/selinux-store/refpolicy-arch";
    internal static readonly string LiveInstallerPayloadErofsDirectory =
        "/opt/homeharbor-installer/payloads";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] LiveInstallerAdditionalPackages =
    [
        "gpm",
        "homeharbor-installer",
        "libnm-selinux",
        "mkinitcpio-archiso",
        "networkmanager-selinux",
        "openssh-selinux",
        "sudo-selinux"
    ];

    private static readonly string[] RequiredLiveInstallerPackages =
    [
        "audit",
        "checkpolicy",
        "coreutils-selinux",
        "dbus-broker-selinux",
        "dbus-selinux",
        "device-mapper-selinux",
        "erofs-utils-selinux",
        "findutils-selinux",
        "homeharbor-installer",
        "homeharbor-selinux-policy",
        "iproute2-selinux",
        "libnm-selinux",
        "libselinux",
        "libsemanage",
        "libsepol",
        "mkinitcpio-archiso",
        "networkmanager-selinux",
        "openssh-selinux",
        "pam-selinux",
        "pambase-selinux",
        "policycoreutils",
        "psmisc-selinux",
        "secilc",
        "selinux-refpolicy-arch",
        "semodule-utils",
        "shadow-selinux",
        "sudo-selinux",
        "systemd-libs-selinux",
        "systemd-resolvconf-selinux",
        "systemd-selinux",
        "systemd-sysvcompat-selinux",
        "util-linux-libs-selinux",
        "util-linux-selinux"
    ];

    private static readonly HashSet<string> ForbiddenGenericLiveInstallerPackages = new(
    [
        "coreutils",
        "crun",
        "dbus",
        "dbus-broker",
        "dbus-docs",
        "device-mapper",
        "erofs-utils",
        "erofsfuse",
        "findutils",
        "iproute2",
        "krun",
        "libnm",
        "lvm2",
        "nm-cloud-setup",
        "networkmanager",
        "networkmanager-docs",
        "openssh",
        "pam",
        "pambase",
        "psmisc",
        "shadow",
        "sudo",
        "systemd",
        "systemd-libs",
        "systemd-resolvconf",
        "systemd-sysvcompat",
        "systemd-tests",
        "systemd-ukify",
        "util-linux",
        "util-linux-libs"
    ],
    StringComparer.Ordinal);

    private static readonly string[] LiveInstallerProfileBootConfigurationPaths =
    [
        "grub/grub.cfg",
        "grub/loopback.cfg",
        "syslinux/syslinux-linux.cfg"
    ];

    private static readonly string[] LiveInstallerIsoBootConfigurationPaths =
    [
        "boot/grub/grub.cfg",
        "boot/grub/loopback.cfg",
        "boot/syslinux/syslinux-linux.cfg"
    ];

    private static readonly string[] CanonicalLiveInstallerSelinuxArguments =
        SecureBootAssets.SelinuxArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static readonly HashSet<string> CanonicalLiveInstallerSelinuxArgumentKeys =
        CanonicalLiveInstallerSelinuxArguments
            .Select(KernelArgumentKey)
            .ToHashSet(StringComparer.Ordinal);

    private readonly string _root = BuildKeyDefaults.Apply(root);
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();
    private readonly string _work = Path.Combine(Path.GetFullPath(root), ".work", "release", version);
    private readonly string _imageWork = Path.Combine(Path.GetFullPath(root), ".work", "image");
    private readonly string _releaseRoot = Path.Combine(Path.GetFullPath(root), "artifacts", "channels");
    private readonly string _releaseDirectory = Path.Combine(Path.GetFullPath(root), "artifacts", "channels", version);

    public async Task<ReleaseArtifactBuildResult> BuildAsync(CancellationToken cancellationToken = default)
    {
        if (!SecurityGuards.IsSafeVersion(version))
        {
            throw new InvalidOperationException("release version must contain only letters, numbers, dot, underscore, and dash: " + version);
        }

        var channel = ReleaseChannel.Require(
            Env.String("HOMEHARBOR_RELEASE_CHANNEL", Env.String("HOMEHARBOR_ISO_CHANNEL", Env.String("HOMEHARBOR_CHANNEL", ReleaseChannel.Dev))),
            "release channel");
        const string kernelChannel = "generic";
        var createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var releaseSequence = ReleaseSequence.RequireEnvironment();

        await RequireToolsAsync(["bsdtar", "getfattr", "mkarchiso", "openssl", "repo-add"], cancellationToken);
        await DeleteManagedReleaseWorkDirectoryAsync(_root, _work, _runner, cancellationToken);
        _ = Directory.CreateDirectory(_work);
        DeleteIfExists(_releaseDirectory);
        _ = Directory.CreateDirectory(_releaseDirectory);
        _ = Directory.CreateDirectory(_releaseRoot);

        var keys = await ResolveReleaseKeysAsync(channel, cancellationToken);
        await CopyPackagesAsync(cancellationToken);

        var systemOta = await BuildSystemOtaAsync(channel, createdAt, releaseSequence, keys, cancellationToken);
        var kernelOta = await BuildKernelOtaAsync(channel, kernelChannel, createdAt, releaseSequence, keys, cancellationToken);
        var payloadChannelFile = await WriteChannelMetadataAsync(
            Path.Combine(_releaseDirectory, "channel-" + channel + ".json"),
            channel,
            systemOta,
            kernelChannel,
            kernelOta,
            installerIso: null,
            releaseSequence,
            cancellationToken);

        var iso = await BuildFullInstallerIsoAsync(channel, systemOta, kernelOta, payloadChannelFile, keys.PublicKey, cancellationToken);
        var channelFile = await WriteChannelMetadataAsync(
            Path.Combine(_releaseDirectory, "channel-" + channel + ".json"),
            channel,
            systemOta,
            kernelChannel,
            kernelOta,
            iso,
            releaseSequence,
            cancellationToken);
        var latestChannelFile = await WriteChannelMetadataAsync(
            Path.Combine(_releaseRoot, "channel.json"),
            channel,
            systemOta,
            kernelChannel,
            kernelOta,
            iso,
            releaseSequence,
            cancellationToken);

        Console.WriteLine("Built release artifacts in " + _releaseDirectory);
        Console.WriteLine(iso);
        return new ReleaseArtifactBuildResult(_releaseDirectory, systemOta, kernelOta, channelFile, latestChannelFile, iso);
    }

    private async Task<string> BuildSystemOtaAsync(
        string channel,
        string createdAt,
        long releaseSequence,
        ReleaseKeys keys,
        CancellationToken cancellationToken)
    {
        var top = "homeharbor-system-ota-" + version;
        var root = Path.Combine(_work, "system-ota", top);
        _ = Directory.CreateDirectory(root);

        var rootPartitionBytes = plan.LogicalPartitions.Single(partition => partition.Name == "root_a").SizeBytes;
        var completeRoot = RequireCompleteLogicalPairArtifact(_imageWork, "root", rootPartitionBytes);
        await CopyReleaseFileAsync(completeRoot, Path.Combine(root, "rootfs.img"), cancellationToken);
        await CopyShaAsync(completeRoot, Path.Combine(root, "rootfs.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.VbmetaA.Path, Path.Combine(root, "vbmeta_a.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.VbmetaA.Path, Path.Combine(root, "vbmeta_a.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.VbmetaB.Path, Path.Combine(root, "vbmeta_b.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.VbmetaB.Path, Path.Combine(root, "vbmeta_b.img.sha256"), cancellationToken);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["type"] = "full-system",
            ["packageKind"] = "system",
            ["releaseSequence"] = releaseSequence,
            ["version"] = version,
            ["channel"] = channel,
            ["createdAt"] = createdAt,
            ["bootMode"] = SecureBootAssets.BootMode(),
            ["rootfsHash"] = await Sha256HexAsync(completeRoot, cancellationToken),
            ["vbmetaAHash"] = await Sha256HexAsync(plan.Artifacts.VbmetaA.Path, cancellationToken),
            ["vbmetaBHash"] = await Sha256HexAsync(plan.Artifacts.VbmetaB.Path, cancellationToken),
            ["vbmetaADigest"] = await ReadDigestAsync(plan.Artifacts.VbmetaA, cancellationToken),
            ["vbmetaBDigest"] = await ReadDigestAsync(plan.Artifacts.VbmetaB, cancellationToken)
        };
        await WriteSignedManifestAsync(Path.Combine(root, "manifest.json"), manifest, keys, cancellationToken);

        var bundle = Path.Combine(_releaseDirectory, top + ".tar.gz");
        await CreateTarGzAsync(Path.GetDirectoryName(root)!, top, bundle, cancellationToken);
        return bundle;
    }

    private async Task<string> BuildKernelOtaAsync(
        string channel,
        string kernelChannel,
        string createdAt,
        long releaseSequence,
        ReleaseKeys keys,
        CancellationToken cancellationToken)
    {
        var top = "homeharbor-kernel-" + kernelChannel + "-ota-" + version;
        var root = Path.Combine(_work, "kernel-ota", top);
        _ = Directory.CreateDirectory(root);

        var modulesPartitionBytes = plan.LogicalPartitions.Single(partition => partition.Name == "modules_a").SizeBytes;
        var completeModules = RequireCompleteLogicalPairArtifact(_imageWork, "modules", modulesPartitionBytes);
        await CopyReleaseFileAsync(completeModules, Path.Combine(root, "modules.img"), cancellationToken);
        await CopyShaAsync(completeModules, Path.Combine(root, "modules.img.sha256"), cancellationToken);
        var firmwarePartitionBytes = plan.LogicalPartitions.Single(partition => partition.Name == "firmware_a").SizeBytes;
        var completeFirmware = RequireCompleteLogicalPairArtifact(_imageWork, "firmware", firmwarePartitionBytes);
        await CopyReleaseFileAsync(completeFirmware, Path.Combine(root, "firmware.img"), cancellationToken);
        await CopyShaAsync(completeFirmware, Path.Combine(root, "firmware.img.sha256"), cancellationToken);
        var recoveryPartitionBytes = plan.Partitions.Single(partition => partition.Name == "recovery_a").SizeBytes
            ?? throw new InvalidOperationException("recovery partition size is not fixed");
        var completeRecovery = RequireCompleteRecoveryPartitionArtifact(_imageWork, recoveryPartitionBytes);
        await CopyReleaseFileAsync(completeRecovery, Path.Combine(root, "recovery.img"), cancellationToken);
        await CopyShaAsync(completeRecovery, Path.Combine(root, "recovery.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.Boot.Path, Path.Combine(root, "boot.efi"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Boot.Path, Path.Combine(root, "boot.efi.sha256"), cancellationToken);

        await CopyReleaseFileAsync(plan.Artifacts.Bootloader.Path, Path.Combine(root, "HomeHarborBoot.efi"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Bootloader.Path, Path.Combine(root, "HomeHarborBoot.efi.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.Bootx64.Path, Path.Combine(root, "BOOTX64.EFI"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Bootx64.Path, Path.Combine(root, "BOOTX64.EFI.sha256"), cancellationToken);
        var mokManager = Path.Combine(_imageWork, "mnt", "EFI", "BOOT", "mmx64.efi");
        if (SecureBootAssets.IsEnabled())
        {
            RequireFile(mokManager, "secure boot MokManager artifact not found; run system-build with Secure Boot enabled");
            await CopyOptionalReleaseFileWithShaAsync(
                mokManager,
                Path.Combine(root, "mmx64.efi"),
                Path.Combine(root, "mmx64.efi.sha256"),
                cancellationToken);
        }
        await WriteVeritySidecarsAsync(root, cancellationToken);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["type"] = "kernel-only",
            ["packageKind"] = "kernel",
            ["releaseSequence"] = releaseSequence,
            ["version"] = version,
            ["channel"] = channel,
            ["kernelChannel"] = kernelChannel,
            ["createdAt"] = createdAt,
            ["bootMode"] = SecureBootAssets.BootMode(),
            ["kernelRelease"] = ResolveKernelRelease(),
            ["modulesHash"] = await Sha256HexAsync(completeModules, cancellationToken),
            ["firmwareHash"] = await Sha256HexAsync(completeFirmware, cancellationToken),
            ["recoveryHash"] = await Sha256HexAsync(completeRecovery, cancellationToken),
            ["bootHash"] = await Sha256HexAsync(plan.Artifacts.Boot.Path, cancellationToken),
            ["bootloaderHash"] = await Sha256HexAsync(plan.Artifacts.Bootloader.Path, cancellationToken),
            ["fallbackBootHash"] = await Sha256HexAsync(plan.Artifacts.Bootx64.Path, cancellationToken)
        };

        if (File.Exists(mokManager))
        {
            manifest["mokManagerHash"] = await Sha256HexAsync(mokManager, cancellationToken);
        }

        await WriteSignedManifestAsync(Path.Combine(root, "manifest.json"), manifest, keys, cancellationToken);

        var bundle = Path.Combine(_releaseDirectory, top + ".tar.gz");
        await CreateTarGzAsync(Path.GetDirectoryName(root)!, top, bundle, cancellationToken);
        return bundle;
    }

    internal static string RequireCompleteRecoveryPartitionArtifact(string imageWork, long expectedBytes)
        => RequireCompleteLogicalPairArtifact(imageWork, "recovery", expectedBytes);

    internal static string RequireCompleteLogicalPairArtifact(string imageWork, string baseName, long expectedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedBytes);

        var logicalA = Path.Combine(imageWork, baseName + "_a.logical");
        var logicalB = Path.Combine(imageWork, baseName + "_b.logical");
        RequireFile(logicalA, $"complete {baseName} A partition artifact is missing; run system-build first");
        RequireFile(logicalB, $"complete {baseName} B partition artifact is missing; run system-build first");
        if (new FileInfo(logicalA).Length != expectedBytes || new FileInfo(logicalB).Length != expectedBytes)
        {
            throw new InvalidOperationException($"complete {baseName} partition artifact has an unexpected size");
        }
        if (!string.Equals(Sha256Hex(logicalA), Sha256Hex(logicalB), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"complete {baseName} A/B partition artifacts differ");
        }

        return logicalA;
    }

    private async Task<string> BuildFullInstallerIsoAsync(
        string channel,
        string systemOta,
        string kernelOta,
        string channelFile,
        string publicKey,
        CancellationToken cancellationToken)
    {
        var packageDirectory = Path.Combine(_releaseDirectory, "packages");
        var packageArchives = Directory.GetFiles(packageDirectory, "*.pkg.tar.*")
            .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(Path.GetFileName)
            .ToArray();
        await RunAsync(
            "repo-add",
            ["homeharbor-local.db.tar.gz", .. packageArchives!],
            cancellationToken,
            packageDirectory);

        var isoWork = Path.Combine(_work, "mkarchiso");
        var erofs = await SelinuxErofsTool.CreateAsync(
            packageDirectory,
            Path.Combine(_work, "live-installer-erofs-tool"),
            _runner,
            cancellationToken);
        var erofsWrapperDirectory = await erofs.CreatePathWrappersAsync(
            Path.Combine(_work, "live-installer-erofs-path"),
            cancellationToken);

        var profile = Path.Combine(_work, "iso-profile");
        CopyDirectory("/usr/share/archiso/configs/baseline", profile);
        await PrepareIsoProfileAsync(
            profile,
            packageDirectory,
            isoWork,
            systemOta,
            kernelOta,
            channelFile,
            publicKey,
            channel,
            cancellationToken);

        var isoOut = Path.Combine(_work, "iso-out");
        _ = Directory.CreateDirectory(isoOut);
        try
        {
            await RunMkarchisoAsync(isoWork, isoOut, profile, erofsWrapperDirectory, cancellationToken);

            var builtIso = Directory.GetFiles(isoOut, "*.iso", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("mkarchiso did not produce an ISO in " + isoOut);
            await ValidateLiveInstallerIsoAsync(
                builtIso,
                erofsWrapperDirectory,
                Path.Combine(
                    _imageWork,
                    "rootfs",
                    "usr",
                    "lib",
                    "homeharbor",
                    "selinux-store",
                    "refpolicy-arch"),
                systemOta,
                cancellationToken);
            var finalIso = Path.Combine(_releaseDirectory, "homeharbor-full-live-installer-" + channel + "-" + version + ".iso");
            File.Move(builtIso, finalIso, overwrite: true);
            await WriteShaFileAsync(finalIso, finalIso + ".sha256", cancellationToken);
            return finalIso;
        }
        finally
        {
            await DeleteManagedReleaseWorkDirectoryAsync(_root, isoWork, _runner, CancellationToken.None);
        }
    }

    internal static async Task DeleteManagedReleaseWorkDirectoryAsync(
        string root,
        string path,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var managedRoot = Path.Combine(fullRoot, ".work", "release");
        var fullPath = Path.GetFullPath(path);
        if (!SecurityGuards.IsInsideDirectory(fullPath, managedRoot) ||
            string.Equals(fullPath, managedRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("refusing to remove unsafe release build work directory: " + fullPath);
        }

        RequireNoSymlinkInManagedPath(fullRoot, fullPath);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            return;
        }

        try
        {
            DeleteIfExists(fullPath);
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // mkarchiso runs under sudo and may leave real-root-owned work after an
            // interrupted or older build. The path is constrained above before
            // crossing the privilege boundary.
        }

        var result = await runner.RunAsync(
            "sudo",
            ["-n", "rm", "-rf", "--", fullPath],
            new CommandRunOptions(Timeout: TimeSpan.FromMinutes(5), StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess("could not remove privileged mkarchiso work directory");
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new InvalidOperationException("privileged mkarchiso work directory still exists after cleanup: " + fullPath);
        }
    }

    private static void RequireNoSymlinkInManagedPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo? entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (entry?.LinkTarget is not null)
            {
                throw new InvalidOperationException("refusing privileged cleanup through a symbolic link: " + current);
            }
        }
    }

    private async Task PrepareIsoProfileAsync(
        string profile,
        string packageDirectory,
        string isoWork,
        string systemOta,
        string kernelOta,
        string channelFile,
        string publicKey,
        string channel,
        CancellationToken cancellationToken)
    {
        var liveRootfsFileContexts = Path.Combine(
            Path.GetFullPath(isoWork),
            "x86_64",
            "airootfs",
            "etc",
            "selinux",
            "refpolicy-arch",
            "contexts",
            "files",
            "file_contexts");
        var erofsOptions = string.Join(
            ' ',
            LiveInstallerErofsOptions(liveRootfsFileContexts)
                .Select(SelinuxErofsTool.BashSingleQuote));
        var profileDef = $$"""
            #!/usr/bin/env bash
            # shellcheck disable=SC2034

            iso_name="homeharbor-full-live-installer-{{channel}}"
            iso_label="{{IsoLabel(channel, version)}}"
            iso_publisher="HomeHarbor <https://github.com/akaishi-tech/home-harbor>"
            iso_application="HomeHarbor Full Live Installer"
            iso_version="{{version}}"
            install_dir="hh"
            buildmodes=('iso')
            bootmodes=('bios.syslinux'
                       'uefi.grub')
            pacman_conf="pacman.conf"
            airootfs_image_type="erofs"
            airootfs_image_tool_options=({{erofsOptions}})
            bootstrap_tarball_compression=(xz -9e)
            file_permissions=(
              ["/etc/shadow"]="0:0:400"
              ["/root"]="0:0:750"
              ["/root/.bash_profile"]="0:0:700"
              ["/root/customize_airootfs.sh"]="0:0:755"
            )
            """;
        await FileWrites.AtomicWriteTextAsync(Path.Combine(profile, "profiledef.sh"), profileDef, 0755, cancellationToken);

        var packages = BuildLiveInstallerPackageList(
            await File.ReadAllLinesAsync(Path.Combine(profile, "packages.x86_64"), cancellationToken),
            plan.Packages.Recovery);
        ValidateLiveInstallerPackageList(string.Join('\n', packages));
        await FileWrites.AtomicWriteTextAsync(
            Path.Combine(profile, "packages.x86_64"),
            string.Join('\n', packages) + "\n",
            0644,
            cancellationToken);

        await ArchLocalPackageRepositoryBuilder.WritePacmanConfigAsync(
            Path.Combine(profile, "pacman.conf"),
            packageDirectory,
            cancellationToken);

        foreach (var relativePath in LiveInstallerProfileBootConfigurationPaths)
        {
            var path = Path.Combine(profile, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var contents = await File.ReadAllTextAsync(path, cancellationToken);
            await FileWrites.AtomicWriteTextAsync(
                path,
                AddLiveInstallerSelinuxKernelArguments(contents),
                0644,
                cancellationToken);
        }

        var payloadDirectory = Path.Combine(profile, "airootfs", "opt", "homeharbor-installer", "payloads");
        _ = Directory.CreateDirectory(payloadDirectory);
        await CopyReleaseFileAsync(systemOta, Path.Combine(payloadDirectory, Path.GetFileName(systemOta)), cancellationToken);
        await CopyReleaseFileAsync(kernelOta, Path.Combine(payloadDirectory, Path.GetFileName(kernelOta)), cancellationToken);
        await CopyReleaseFileAsync(channelFile, Path.Combine(payloadDirectory, Path.GetFileName(channelFile)), cancellationToken);
        await CopyReleaseFileAsync(publicKey, Path.Combine(payloadDirectory, "release.pub.pem"), cancellationToken);
        await InstallLiveInstallerTrustAnchorAsync(profile, publicKey, cancellationToken);

        await WriteLiveInstallerStartupAsync(profile, cancellationToken);
    }

    internal static IReadOnlyList<string> BuildLiveInstallerPackageList(
        IEnumerable<string> baselinePackages,
        IEnumerable<string> recoveryPackages)
    {
        ArgumentNullException.ThrowIfNull(baselinePackages);
        ArgumentNullException.ThrowIfNull(recoveryPackages);

        return baselinePackages
            .Concat(recoveryPackages)
            .Concat(LiveInstallerAdditionalPackages)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Trim())
            .Where(package => !string.Equals(package, "homeharbor-recovery", StringComparison.Ordinal))
            .Where(package => !ForbiddenGenericLiveInstallerPackages.Contains(package))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static void ValidateLiveInstallerPackageList(string packageList)
    {
        ArgumentNullException.ThrowIfNull(packageList);
        if (packageList.Contains("archlinuxhardened", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("live installer package list must not reference archlinuxhardened binaries");
        }

        var installed = packageList
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.Ordinal);

        var missing = RequiredLiveInstallerPackages
            .Where(package => !installed.Contains(package))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "live installer is missing required SELinux-enabled packages: " + string.Join(", ", missing));
        }

        var generic = installed
            .Where(ForbiddenGenericLiveInstallerPackages.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (generic.Length > 0)
        {
            throw new InvalidOperationException(
                "live installer resolved forbidden generic packages instead of SELinux variants: " + string.Join(", ", generic));
        }
    }

    internal static string AddLiveInstallerSelinuxKernelArguments(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = contents.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hasFinalNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n');
        var targetCount = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsLiveInstallerKernelArgumentLine(lines[index]))
            {
                continue;
            }

            targetCount++;
            var indentationLength = lines[index].Length - lines[index].TrimStart().Length;
            var indentation = lines[index][..indentationLength];
            var arguments = lines[index]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(argument => !CanonicalLiveInstallerSelinuxArgumentKeys.Contains(KernelArgumentKey(argument)))
                .ToArray();
            lines[index] = indentation + string.Join(
                ' ',
                arguments.Concat(CanonicalLiveInstallerSelinuxArguments));
        }

        if (targetCount == 0)
        {
            throw new InvalidOperationException("live installer boot configuration has no Linux kernel command line");
        }

        var result = string.Join(newline, hasFinalNewline ? lines[..^1] : lines);
        return hasFinalNewline ? result + newline : result;
    }

    internal static void ValidateLiveInstallerBootConfiguration(string path, string contents)
    {
        var kernelLines = contents
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(IsLiveInstallerKernelArgumentLine)
            .ToArray();
        if (kernelLines.Length == 0)
        {
            throw new InvalidOperationException("live installer boot configuration has no Linux kernel command line: " + path);
        }

        foreach (var line in kernelLines)
        {
            var arguments = line
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(argument => CanonicalLiveInstallerSelinuxArgumentKeys.Contains(KernelArgumentKey(argument)))
                .GroupBy(KernelArgumentKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var expected in CanonicalLiveInstallerSelinuxArguments)
            {
                var key = KernelArgumentKey(expected);
                if (!arguments.TryGetValue(key, out var matches) ||
                    matches.Length != 1 ||
                    !string.Equals(matches[0], expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"live installer boot configuration {path} must contain exactly one canonical '{expected}' argument");
                }
            }
        }
    }

    private static string KernelArgumentKey(string argument)
    {
        var separator = argument.IndexOf('=');
        return separator < 0 ? argument : argument[..separator];
    }

    private static bool IsLiveInstallerKernelArgumentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("APPEND ", StringComparison.OrdinalIgnoreCase) ||
               (trimmed.StartsWith("linux ", StringComparison.Ordinal) &&
                trimmed.Contains("/vmlinuz-linux", StringComparison.Ordinal));
    }

    internal static async Task InstallLiveInstallerTrustAnchorAsync(
        string profile,
        string publicKey,
        CancellationToken cancellationToken)
    {
        RequireFile(publicKey, "release public key not found");
        var destination = Path.Combine(profile, "airootfs", "etc", "homeharbor", "release.pub.pem");
        await FileWrites.CopyFileAsync(publicKey, destination, 0644, cancellationToken);
    }

    private static async Task WriteLiveInstallerStartupAsync(string profile, CancellationToken cancellationToken)
    {
        var ttyDropIn = Path.Combine(profile, "airootfs", "etc", "systemd", "system", "getty@tty1.service.d", "autologin.conf");
        await FileWrites.AtomicWriteTextAsync(ttyDropIn, """
            [Service]
            ExecStart=
            ExecStart=-/usr/bin/agetty --noreset --noclear --autologin root - ${TERM}
            """.Replace("            ", string.Empty, StringComparison.Ordinal), cancellationToken: cancellationToken);

        var serialDropIn = Path.Combine(profile, "airootfs", "etc", "systemd", "system", "serial-getty@ttyS0.service.d", "autologin.conf");
        await FileWrites.AtomicWriteTextAsync(serialDropIn, """
            [Service]
            ExecStart=
            ExecStart=-/usr/bin/agetty --autologin root --keep-baud 115200,57600,38400,9600 %I ${TERM}
            """.Replace("            ", string.Empty, StringComparison.Ordinal), cancellationToken: cancellationToken);

        await FileWrites.AtomicWriteTextAsync(Path.Combine(profile, "airootfs", "root", ".bash_profile"), """
            if [ -z "${HOMEHARBOR_INSTALLER_STARTED:-}" ]; then
              case "$(tty)" in
                /dev/tty1|/dev/ttyS0)
                  export HOMEHARBOR_INSTALLER_STARTED=1
                  export HOMEHARBOR_INSTALLER_TUI_COLOR_MODE=kernel-16
                  export HOMEHARBOR_INSTALLER_TUI_SIZE_DETECTION=polling
                  printf '\nHomeHarbor Live Installer\n\n'
                  exec /usr/lib/homeharbor/installer/HomeHarbor.Installer --mode full --payload-dir /opt/homeharbor-installer/payloads
                  ;;
              esac
            fi
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0700, cancellationToken);

        await FileWrites.AtomicWriteTextAsync(Path.Combine(profile, "airootfs", "root", "customize_airootfs.sh"), """
            #!/usr/bin/env bash
            set -euo pipefail
            systemctl enable serial-getty@ttyS0.service
            systemctl enable getty@tty1.service
            systemctl enable NetworkManager.service systemd-resolved.service qemu-guest-agent.service auditd.service
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0755, cancellationToken);
    }

    private async Task<string> WriteChannelMetadataAsync(
        string path,
        string channel,
        string systemOta,
        string kernelChannel,
        string kernelOta,
        string? installerIso,
        long releaseSequence,
        CancellationToken cancellationToken)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["channel"] = channel,
            ["currentVersion"] = version,
            ["releaseSequence"] = releaseSequence,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["systemOta"] = new JsonObject
            {
                ["bundle"] = BundleNode(systemOta)
            },
            ["kernelChannels"] = new JsonObject
            {
                [kernelChannel] = new JsonObject
                {
                    ["bundle"] = BundleNode(kernelOta)
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(installerIso))
        {
            root["installerIsos"] = new JsonObject
            {
                ["full"] = BundleNode(installerIso)
            };
        }

        await FileWrites.AtomicWriteTextAsync(path, root.ToJsonString(JsonOptions) + "\n", cancellationToken: cancellationToken);
        return path;
    }

    private JsonObject BundleNode(string path)
        => new()
        {
            ["file"] = version + "/" + Path.GetFileName(path),
            ["sha256"] = Sha256Hex(path)
        };

    private async Task WriteSignedManifestAsync(
        string path,
        JsonObject manifest,
        ReleaseKeys keys,
        CancellationToken cancellationToken)
    {
        var unsigned = manifest.ToJsonString();
        using var doc = JsonDocument.Parse(unsigned);
        var payload = OtaManifestVerifier.CanonicalPayload(doc.RootElement);
        var payloadSha = Sha256Hex(Encoding.UTF8.GetBytes(payload));
        var payloadPath = Path.Combine(_work, "manifest-payload-" + Guid.NewGuid().ToString("N") + ".json");
        var signaturePath = Path.Combine(_work, "manifest-signature-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(payloadPath, payload, cancellationToken);
        try
        {
            await RunAsync("openssl", ["pkeyutl", "-sign", "-inkey", keys.PrivateKey, "-rawin", "-in", payloadPath, "-out", signaturePath], cancellationToken);
            manifest["signatureAlgorithm"] = "Ed25519";
            manifest["signedPayloadSha256"] = payloadSha;
            manifest["signingKeyId"] = keys.KeyId;
            manifest["signature"] = Convert.ToBase64String(await File.ReadAllBytesAsync(signaturePath, cancellationToken));
            await FileWrites.AtomicWriteTextAsync(path, manifest.ToJsonString(JsonOptions) + "\n", 0644, cancellationToken);
        }
        finally
        {
            TryDeleteFile(payloadPath);
            TryDeleteFile(signaturePath);
        }
    }

    private async Task WriteVeritySidecarsAsync(string root, CancellationToken cancellationToken)
    {
        foreach (var partition in new[] { "modules_a", "modules_b", "firmware_a", "firmware_b", "recovery_a", "recovery_b" })
        {
            var vbmeta = Path.Combine(_imageWork, partition + ".vbmeta");
            if (!File.Exists(vbmeta))
            {
                throw new FileNotFoundException("verity sidecar source is missing; run system-build before release-build", vbmeta);
            }

            var output = await CaptureAsync(AvbHelperPath(), ["descriptor", vbmeta, AvbPartitionNames.DescriptorName(partition)], cancellationToken);
            var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
            var verity = string.Join(
                ':',
                Descriptor(values, "HASH_ALGORITHM"),
                Descriptor(values, "DATA_BLOCK_SIZE"),
                Descriptor(values, "HASH_BLOCK_SIZE"),
                Descriptor(values, "DATA_BLOCKS"),
                Descriptor(values, "TREE_OFFSET"),
                Descriptor(values, "SALT"),
                Descriptor(values, "ROOT_DIGEST"));
            await FileWrites.AtomicWriteTextAsync(Path.Combine(root, partition + ".verity"), verity + "\n", 0644, cancellationToken);
        }
    }

    private async Task CopyPackagesAsync(CancellationToken cancellationToken)
    {
        var source = Directory.Exists(Path.Combine(_imageWork, "packages"))
            ? Path.Combine(_imageWork, "packages")
            : Path.Combine(_root, "artifacts", "packages", version);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("HomeHarbor package directory not found; run system-build first: " + source);
        }

        var destination = Path.Combine(_releaseDirectory, "packages");
        await CopyVerifiedPackageSetAsync(_root, version, source, destination, cancellationToken);

        if (Directory.GetFiles(destination, "homeharbor-installer-*.pkg.tar.*").Length == 0)
        {
            throw new InvalidOperationException("release package set is missing homeharbor-installer");
        }
    }

    internal static async Task CopyVerifiedPackageSetAsync(
        string root,
        string version,
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await ArchPackageSetProvenance.VerifyAsync(root, version, source, cancellationToken);
        _ = Directory.CreateDirectory(destination);
        foreach (var package in Directory.GetFiles(source, "*.pkg.tar.*", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            await CopyReleaseFileAsync(package, Path.Combine(destination, Path.GetFileName(package)), cancellationToken);
        }

        await CopyReleaseFileAsync(
            Path.Combine(source, ArchPackageSetProvenance.FileName),
            Path.Combine(destination, ArchPackageSetProvenance.FileName),
            cancellationToken);
        await ArchPackageSetProvenance.VerifyAsync(root, version, destination, cancellationToken);
    }

    private async Task<ReleaseKeys> ResolveReleaseKeysAsync(string channel, CancellationToken cancellationToken)
    {
        var privateKey = Env.Optional("HOMEHARBOR_RELEASE_PRIVATE_KEY");
        var publicKey = Env.Optional("HOMEHARBOR_RELEASE_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(privateKey) && !string.IsNullOrWhiteSpace(publicKey))
        {
            RequireFile(privateKey, "release private key not found");
            RequireFile(publicKey, "release public key not found");
            return new ReleaseKeys(privateKey, publicKey, Env.String("HOMEHARBOR_RELEASE_KEY_ID", channel + "-local"));
        }

        if (channel != ReleaseChannel.Dev)
        {
            throw new InvalidOperationException("HOMEHARBOR_RELEASE_PRIVATE_KEY and HOMEHARBOR_RELEASE_PUBLIC_KEY are required for " + channel + " releases");
        }

        var keyDir = Path.Combine(_work, "dev-release-key");
        _ = Directory.CreateDirectory(keyDir);
        privateKey = Path.Combine(keyDir, "release.pem");
        publicKey = Path.Combine(keyDir, "release.pub.pem");
        await RunAsync("openssl", ["genpkey", "-algorithm", "Ed25519", "-out", privateKey], cancellationToken);
        await RunAsync("openssl", ["pkey", "-in", privateKey, "-pubout", "-out", publicKey], cancellationToken);
        await RunAsync("chmod", ["0600", privateKey], cancellationToken);
        await RunAsync("chmod", ["0644", publicKey], cancellationToken);
        return new ReleaseKeys(privateKey, publicKey, "dev-local");
    }

    private async Task RunMkarchisoAsync(
        string work,
        string output,
        string profile,
        string erofsWrapperDirectory,
        CancellationToken cancellationToken)
    {
        await RunPrivilegedWithToolPathAsync(
            erofsWrapperDirectory,
            "/usr/bin/mkarchiso",
            ["-v", "-w", work, "-o", output, profile],
            cancellationToken,
            timeout: TimeSpan.FromHours(3));
    }

    private async Task ValidateLiveInstallerIsoAsync(
        string iso,
        string erofsWrapperDirectory,
        string expectedPolicyStore,
        string systemOta,
        CancellationToken cancellationToken)
    {
        _ = SelinuxPolicyStoreSynchronizer.RequireValidSeed(expectedPolicyStore);
        RequireFile(systemOta, "system OTA is missing for live policy validation");
        var rootPartitionBytes = plan.LogicalPartitions.Single(partition => partition.Name == "root_a").SizeBytes;
        var expectedSystemRootfs = RequireCompleteLogicalPairArtifact(_imageWork, "root", rootPartitionBytes);

        var packageList = await CaptureAsync(
            "bsdtar",
            ["-xOf", iso, "hh/pkglist.x86_64.txt"],
            cancellationToken);
        ValidateLiveInstallerPackageList(packageList);

        foreach (var relativePath in LiveInstallerIsoBootConfigurationPaths)
        {
            var contents = await CaptureAsync(
                "bsdtar",
                ["-xOf", iso, relativePath],
                cancellationToken);
            ValidateLiveInstallerBootConfiguration(relativePath, contents);
        }

        var validationRoot = Path.Combine(_work, "live-installer-iso-validation");
        _ = Directory.CreateDirectory(validationRoot);
        try
        {
            await RunAsync(
                "bsdtar",
                [
                    "-xf",
                    iso,
                    "-C",
                    validationRoot,
                    "hh/x86_64/airootfs.erofs"
                ],
                cancellationToken);
            var image = Path.Combine(validationRoot, "hh", "x86_64", "airootfs.erofs");
            RequireFile(image, "live installer EROFS image is missing from the ISO");

            var installer = Path.Combine(validationRoot, "HomeHarbor.Installer");
            await RunPrivilegedWithToolPathAsync(
                erofsWrapperDirectory,
                "fsck.erofs",
                [
                    "--extract=" + installer,
                    "--no-preserve",
                    "--xattrs",
                    "--path=/usr/lib/homeharbor/installer/HomeHarbor.Installer",
                    image
                ],
                cancellationToken,
                timeout: TimeSpan.FromMinutes(10));
            RequireFile(installer, "could not extract the live installer executable for SELinux validation");

            var context = await CaptureAsync(
                "getfattr",
                ["--only-values", "-n", "security.selinux", installer],
                cancellationToken);
            var normalizedContext = context.TrimEnd('\0', '\r', '\n');
            const string expectedContext = "system_u:object_r:homeharbor_exec_t:s0";
            if (!string.Equals(normalizedContext, expectedContext, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"live installer executable has SELinux context '{normalizedContext}', expected '{expectedContext}'");
            }

            var embeddedSystemOta = Path.Combine(validationRoot, Path.GetFileName(systemOta));
            await RunAsync(
                Path.Combine(erofsWrapperDirectory, "fsck.erofs"),
                [
                    "--extract=" + embeddedSystemOta,
                    "--no-preserve-owner",
                    "--preserve-perms",
                    "--path=" + LiveInstallerPayloadErofsDirectory + "/" + Path.GetFileName(systemOta),
                    image
                ],
                cancellationToken,
                timeout: TimeSpan.FromMinutes(10));
            RequireFile(embeddedSystemOta, "system OTA is missing from the exact live installer EROFS");
            await RequireSameFileContentAsync(
                systemOta,
                embeddedSystemOta,
                "system OTA changed inside the live installer EROFS",
                cancellationToken);

            var embeddedSystemRootfs = Path.Combine(validationRoot, "embedded-system-rootfs.img");
            await ExtractSystemOtaRootfsAsync(
                embeddedSystemOta,
                embeddedSystemRootfs,
                rootPartitionBytes,
                cancellationToken);

            await RequireSameFileContentAsync(
                expectedSystemRootfs,
                embeddedSystemRootfs,
                "system root logical image changed inside the embedded installer OTA",
                cancellationToken);

            var extractedSystemPolicyStore = Path.Combine(validationRoot, "system-policy-store");
            await RunAsync(
                Path.Combine(erofsWrapperDirectory, "fsck.erofs"),
                [
                    "--extract=" + extractedSystemPolicyStore,
                    "--no-preserve-owner",
                    "--preserve-perms",
                    "--path=" + SystemPolicyStoreErofsPath,
                    embeddedSystemRootfs
                ],
                cancellationToken,
                timeout: TimeSpan.FromMinutes(10));
            _ = SelinuxPolicyStoreSynchronizer.RequireValidSeed(extractedSystemPolicyStore);
            await ValidateFileTreeContentAsync(
                expectedPolicyStore,
                extractedSystemPolicyStore,
                "system EROFS immutable SELinux policy store",
                cancellationToken);

            var extractedPolicyModules = Path.Combine(validationRoot, "live-policy-modules");
            await RunAsync(
                Path.Combine(erofsWrapperDirectory, "fsck.erofs"),
                [
                    "--extract=" + extractedPolicyModules,
                    "--no-preserve-owner",
                    "--preserve-perms",
                    "--path=" + LiveInstallerPolicyModulesErofsPath,
                    image
                ],
                cancellationToken,
                timeout: TimeSpan.FromMinutes(10));
            await ValidateFileTreeContentAsync(
                Path.Combine(expectedPolicyStore, "active", "modules"),
                extractedPolicyModules,
                "live installer SELinux module store",
                cancellationToken);
            await ValidateFileTreeContentAsync(
                Path.Combine(extractedSystemPolicyStore, "active", "modules"),
                extractedPolicyModules,
                "system/live EROFS SELinux module store",
                cancellationToken);
        }
        finally
        {
            await DeleteManagedReleaseWorkDirectoryAsync(
                _root,
                validationRoot,
                _runner,
                CancellationToken.None);
        }
    }

    private async Task ExtractSystemOtaRootfsAsync(
        string systemOta,
        string destination,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var expectedMember = "homeharbor-system-ota-" + version + "/rootfs.img";
        await using var input = File.OpenRead(systemOta);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var extracted = false;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (!string.Equals(entry.Name.TrimEnd('/'), expectedMember, StringComparison.Ordinal))
            {
                continue;
            }

            if (extracted)
            {
                throw new InvalidOperationException("system OTA contains duplicate rootfs.img members");
            }

            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile ||
                entry.DataStream is null)
            {
                throw new InvalidOperationException("system OTA rootfs.img is not a regular file");
            }

            if (entry.Length != expectedBytes)
            {
                throw new InvalidOperationException(
                    $"embedded system OTA rootfs has {entry.Length} bytes, expected {expectedBytes}");
            }

            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await entry.DataStream.CopyToAsync(output, 1024 * 1024, cancellationToken);
            extracted = true;
        }

        if (!extracted)
        {
            throw new InvalidOperationException("system OTA is missing its exact rootfs.img member");
        }
    }

    internal static IReadOnlyList<string> LiveInstallerErofsOptions(string fileContexts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileContexts);

        // erofs-utils 1.9.2 can prepend zeroes to incompressible inline data
        // when ztailpacking is enabled. Policy-store CIL files hit that path,
        // so keep the live image on ordinary LZMA compression until upstream
        // ships a fixed release.
        return
        [
            "-zlzma,109",
            "--mount-point=/",
            "--file-contexts=" + Path.GetFullPath(fileContexts)
        ];
    }

    internal static async Task ValidateFileTreeContentAsync(
        string expectedRoot,
        string actualRoot,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!Directory.Exists(expectedRoot))
        {
            throw new DirectoryNotFoundException($"{label} source directory is missing: {expectedRoot}");
        }
        if (!Directory.Exists(actualRoot))
        {
            throw new DirectoryNotFoundException($"{label} extracted directory is missing: {actualRoot}");
        }

        var expected = await DescribeFileTreeAsync(expectedRoot, cancellationToken);
        var actual = await DescribeFileTreeAsync(actualRoot, cancellationToken);
        var missing = expected.Keys.Except(actual.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpected = actual.Keys.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw new InvalidOperationException(
                $"{label} entry set differs after EROFS extraction; missing=[{string.Join(", ", missing)}], " +
                $"unexpected=[{string.Join(", ", unexpected)}]");
        }

        foreach (var relativePath in expected.Keys.Order(StringComparer.Ordinal))
        {
            if (expected[relativePath] != actual[relativePath])
            {
                throw new InvalidOperationException(
                    $"{label} entry changed inside EROFS: {relativePath} " +
                    $"({actual[relativePath]} != {expected[relativePath]})");
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, FileTreeEntry>> DescribeFileTreeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, FileTreeEntry>(StringComparer.Ordinal);
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        rootInfo.Refresh();
        var rootMetadata = ReadFileTreeMetadata(rootInfo.FullName);
        if (rootMetadata.Type != FileTreeEntryType.Directory)
        {
            throw new InvalidOperationException("file tree root must be a real directory: " + root);
        }

        entries.Add(".", FileTreeEntry.Directory(rootMetadata.Mode));
        await AddDirectoryEntriesAsync(rootInfo, rootInfo.FullName, entries, cancellationToken);
        return entries;
    }

    private static async Task AddDirectoryEntriesAsync(
        DirectoryInfo directory,
        string root,
        IDictionary<string, FileTreeEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.Refresh();
            var relativePath = Path.GetRelativePath(root, entry.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            var metadata = ReadFileTreeMetadata(entry.FullName);
            if (metadata.Type == FileTreeEntryType.SymbolicLink)
            {
                var target = entry.LinkTarget;
                if (string.IsNullOrWhiteSpace(target))
                {
                    throw new IOException("could not read symbolic link target: " + entry.FullName);
                }

                entries.Add(relativePath, FileTreeEntry.SymbolicLink(metadata.Mode, target));
                continue;
            }

            if (metadata.Type == FileTreeEntryType.Directory)
            {
                entries.Add(relativePath, FileTreeEntry.Directory(metadata.Mode));
                await AddDirectoryEntriesAsync(
                    new DirectoryInfo(entry.FullName),
                    root,
                    entries,
                    cancellationToken);
                continue;
            }

            if (metadata.Type != FileTreeEntryType.RegularFile)
            {
                throw new InvalidOperationException(
                    $"unsupported file tree entry type {metadata.Type}: {entry.FullName}");
            }

            var file = new FileInfo(entry.FullName);
            entries.Add(
                relativePath,
                FileTreeEntry.RegularFile(
                    metadata.Mode,
                    file.Length,
                    await Sha256HexAsync(entry.FullName, cancellationToken)));
        }
    }

    private static FileTreeMetadata ReadFileTreeMetadata(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("EROFS policy tree validation requires Linux statx");
        }

        const int bufferBytes = 256;
        const int statxModeOffset = 28;
        const int atFileSystemWorkingDirectory = -100;
        const int atSymlinkNoFollow = 0x100;
        const uint statxBasicStats = 0x7ff;
        const int fileTypeMask = 0xf000;
        const int permissionMask = 0x0fff;
        var buffer = Marshal.AllocHGlobal(bufferBytes);
        try
        {
            if (NativeMethods.Statx(
                    atFileSystemWorkingDirectory,
                    path,
                    atSymlinkNoFollow,
                    statxBasicStats,
                    buffer) != 0)
            {
                throw new IOException(
                    $"could not inspect EROFS policy tree entry {path}: errno {Marshal.GetLastPInvokeError()}");
            }

            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, statxModeOffset));
            var type = (mode & fileTypeMask) switch
            {
                0x4000 => FileTreeEntryType.Directory,
                0x8000 => FileTreeEntryType.RegularFile,
                0xa000 => FileTreeEntryType.SymbolicLink,
                0x1000 => FileTreeEntryType.Fifo,
                0xc000 => FileTreeEntryType.Socket,
                0x2000 => FileTreeEntryType.CharacterDevice,
                0x6000 => FileTreeEntryType.BlockDevice,
                _ => FileTreeEntryType.Unknown
            };
            return new FileTreeMetadata(type, (UnixFileMode)(mode & permissionMask));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private async Task RunPrivilegedWithToolPathAsync(
        string toolDirectory,
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var directory = Path.GetFullPath(toolDirectory);
        if (directory.IndexOfAny(['\0', '\r', '\n', ':']) >= 0)
        {
            throw new InvalidOperationException("privileged tool directory contains an invalid PATH character");
        }

        var toolPath = directory + ":/usr/local/sbin:/usr/local/bin:/usr/bin:/bin";
        var command = Environment.UserName == "root" ? "/usr/bin/env" : "sudo";
        var args = Environment.UserName == "root"
            ? new[] { "PATH=" + toolPath, executable }.Concat(arguments)
            : new[] { "-n", "/usr/bin/env", "PATH=" + toolPath, executable }.Concat(arguments);
        await RunAsync(command, args, cancellationToken, timeout: timeout);
    }

    private async Task RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        var result = await _runner.RunAsync(
            fileName,
            arguments,
            new CommandRunOptions(WorkingDirectory: workingDirectory, Timeout: timeout, StreamOutput: true, StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess();
    }

    private async Task<string> CaptureAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(fileName, arguments, cancellationToken: cancellationToken);
        _ = result.EnsureSuccess();
        return result.Stdout;
    }

    private async Task RequireToolsAsync(IEnumerable<string> tools, CancellationToken cancellationToken)
    {
        foreach (var tool in tools)
        {
            var result = await _runner.RunAsync("sh", ["-c", "command -v \"$1\" >/dev/null", "sh", tool], cancellationToken: cancellationToken);
            _ = result.EnsureSuccess("missing required tool: " + tool);
        }
    }

    private string ResolveKernelRelease()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(_imageWork, "modules-root", "usr", "lib", "modules"),
                     Path.Combine(_imageWork, "recovery-rootfs", "usr", "lib", "modules"),
                     Path.Combine(_imageWork, "rootfs", "usr", "lib", "modules")
                 })
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var releases = Directory.GetDirectories(candidate).Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Order(StringComparer.Ordinal).ToArray();
            if (releases.Length == 1)
            {
                return releases[0]!;
            }
        }

        throw new InvalidOperationException("could not resolve kernel release from build work directory");
    }

    private string AvbHelperPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(_root, "artifacts", "homeharbor-avb"),
                     Path.Combine(_imageWork, "homeharbor-avb")
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("homeharbor-avb helper not found; run system-build first");
    }

    private static async Task CreateTarGzAsync(string sourceDirectory, string topDirectory, string output, CancellationToken cancellationToken)
    {
        await using var file = File.Create(output);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        await using var writer = new TarWriter(gzip);
        foreach (var path in OrderOtaBundleEntries(sourceDirectory, topDirectory))
        {
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
            if (Directory.Exists(path))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, relative + "/"));
                continue;
            }

            var entry = new PaxTarEntry(TarEntryType.RegularFile, relative)
            {
                DataStream = File.OpenRead(path)
            };
            writer.WriteEntry(entry);
            await entry.DataStream.DisposeAsync();
        }

        _ = cancellationToken;
    }

    internal static IReadOnlyList<string> OrderOtaBundleEntries(string sourceDirectory, string topDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(sourceDirectory), topDirectory);
        var manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest))
        {
            throw new FileNotFoundException("OTA bundle manifest is missing", manifest);
        }

        return Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => string.Equals(Path.GetFullPath(path), manifest, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
        {
            throw new DirectoryNotFoundException("directory not found: " + source);
        }

        _ = Directory.CreateDirectory(destination);
        CopyDirectoryContents(sourceInfo, destination);
    }

    private static void CopyDirectoryContents(DirectoryInfo source, string destination)
    {
        foreach (var entry in source.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var output = Path.Combine(destination, entry.Name);
            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                DeleteIfExists(output);
                _ = entry.Attributes.HasFlag(FileAttributes.Directory)
                    ? Directory.CreateSymbolicLink(output, entry.LinkTarget)
                    : File.CreateSymbolicLink(output, entry.LinkTarget);

                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                _ = Directory.CreateDirectory(output);
                CopyDirectoryContents((DirectoryInfo)entry, output);
                continue;
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(entry.FullName, output, overwrite: true);
        }
    }

    private static async Task CopyReleaseFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        RequireFile(source, "release input not found");
        await FileWrites.CopyFileAsync(source, destination, cancellationToken: cancellationToken);
    }

    internal static async Task CopyOptionalReleaseFileWithShaAsync(
        string source,
        string destination,
        string shaDestination,
        CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            await FileWrites.CopyFileAsync(source, destination, cancellationToken: cancellationToken);
            await WriteShaFileAsync(source, shaDestination, cancellationToken);
        }
    }

    private static async Task CopyShaAsync(string source, string destination, CancellationToken cancellationToken)
        => await WriteShaFileAsync(source, destination, cancellationToken);

    private static async Task WriteShaFileAsync(string source, string destination, CancellationToken cancellationToken)
        => await FileWrites.AtomicWriteTextAsync(destination, await Sha256HexAsync(source, cancellationToken) + "  " + Path.GetFileName(source) + "\n", 0644, cancellationToken);

    private static async Task<string> ReadDigestAsync(SystemImageArtifactPlan artifact, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifact.DigestPath) || !File.Exists(artifact.DigestPath))
        {
            throw new FileNotFoundException("vbmeta digest artifact not found", artifact.DigestPath);
        }

        var digest = (await File.ReadAllTextAsync(artifact.DigestPath, cancellationToken))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return digest.Length == 64 && digest.All(Uri.IsHexDigit)
            ? digest.ToLowerInvariant()
            : throw new InvalidOperationException("invalid vbmeta digest: " + artifact.DigestPath);
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static async Task RequireSameFileContentAsync(
        string expected,
        string actual,
        string label,
        CancellationToken cancellationToken)
    {
        RequireFile(expected, label + " expected file is missing");
        RequireFile(actual, label + " actual file is missing");
        var expectedLength = new FileInfo(expected).Length;
        var actualLength = new FileInfo(actual).Length;
        if (expectedLength != actualLength)
        {
            throw new InvalidOperationException(
                $"{label}: length {actualLength} != {expectedLength}");
        }

        var expectedDigest = await Sha256HexAsync(expected, cancellationToken);
        var actualDigest = await Sha256HexAsync(actual, cancellationToken);
        if (!string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: SHA-256 {actualDigest} != {expectedDigest}");
        }
    }

    private static string Sha256Hex(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static string Sha256Hex(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Descriptor(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("AVB descriptor output is missing " + name);

    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(label + ": " + path, path);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string IsoLabel(string channel, string version)
    {
        var value = ("HH_" + channel + "_" + version).ToUpperInvariant();
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            _ = builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return builder.Length <= 32 ? builder.ToString() : builder.ToString(0, 32);
    }

    private sealed record FileTreeEntry(
        string Type,
        UnixFileMode Mode,
        long Length,
        string? Sha256,
        string? LinkTarget)
    {
        public static FileTreeEntry Directory(UnixFileMode mode)
            => new("directory", mode, 0, null, null);

        public static FileTreeEntry RegularFile(UnixFileMode mode, long length, string sha256)
            => new("file", mode, length, sha256, null);

        public static FileTreeEntry SymbolicLink(UnixFileMode mode, string target)
            => new("symlink", mode, 0, null, target);
    }

    private readonly record struct FileTreeMetadata(FileTreeEntryType Type, UnixFileMode Mode);

    private enum FileTreeEntryType
    {
        Unknown,
        RegularFile,
        Directory,
        SymbolicLink,
        Fifo,
        Socket,
        CharacterDevice,
        BlockDevice
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int Statx(
            int directoryFileDescriptor,
            string path,
            int flags,
            uint mask,
            IntPtr buffer);
    }

    private sealed record ReleaseKeys(string PrivateKey, string PublicKey, string KeyId);
}
