using HomeHarbor.Tooling;

namespace HomeHarbor.SystemBuild.Tests;

[TestClass]
public sealed class SystemImageProfileTests
{
    [TestMethod]
    public void SchemaOne_Defaults_To_HomeHarbor_Compatibility_Profile()
    {
        var product = new SystemImageProductDescriptor().ToPlan(1);
        var security = new SystemImageSecurityDescriptor
        {
            SecureBoot = false
        }.ToPlan(1);

        Assert.AreEqual("homeharbor", product.Id);
        Assert.AreEqual("EFI/HomeHarbor/boot_state.json", product.BootStatePath);
        Assert.IsTrue(security.Selinux);
        Assert.IsFalse(security.SecureBoot);
        Assert.IsTrue(security.Avb);
    }

    [TestMethod]
    public void SchemaTwo_Requires_Explicit_Security_Profile()
    {
        var descriptor = new SystemImageSecurityDescriptor();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => descriptor.ToPlan(2));

        StringAssert.Contains(exception.Message, "explicit security.selinux");
    }

    [TestMethod]
    public void Product_Profile_Rejects_Boot_State_Outside_Efi_Directory()
    {
        var descriptor = BreakwaterProduct();
        descriptor.BootStatePath = "EFI/Other/boot_state.json";

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => descriptor.ToPlan(2));

        StringAssert.Contains(exception.Message, "below product efiDirectory");
    }

    [TestMethod]
    public void Remaining_Partition_Can_Declare_A_Minimum_Size()
    {
        var descriptor = new SystemImagePartitionDescriptor
        {
            Name = "state",
            Size = "remaining",
            MinimumSizeMib = 2048,
            FileSystem = "ext4"
        };

        var plan = descriptor.ToPlan("raw-uki");

        Assert.AreEqual("remaining", plan.Size);
        Assert.AreEqual(2048L * 1024 * 1024, plan.MinimumSizeBytes);
    }

    [TestMethod]
    public void Fixed_Partition_Rejects_A_Minimum_Size()
    {
        var descriptor = new SystemImagePartitionDescriptor
        {
            Name = "state",
            SizeMib = 2048,
            MinimumSizeMib = 1024,
            FileSystem = "ext4"
        };

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => descriptor.ToPlan("raw-uki"));
    }

    [TestMethod]
    public void Kernel_Config_Can_Validate_Without_Selinux_Options()
    {
        const string config = """
            CONFIG_SECURITY=y
            CONFIG_SECURITYFS=y
            CONFIG_SECURITY_NETWORK=y
            CONFIG_AUDIT=y
            CONFIG_AUDITSYSCALL=y
            CONFIG_TMPFS=y
            CONFIG_TMPFS_XATTR=y
            """;

        KernelConfigValidator.ValidateConfig(config, "no-selinux", requireSelinux: false);
        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => KernelConfigValidator.ValidateConfig(config, "selinux", requireSelinux: true));
    }

    [TestMethod]
    public async Task Kernel_Boot_Artifacts_Use_The_Installed_Custom_Pkgbase()
    {
        var rootfs = Path.Combine(Path.GetTempPath(), "custom-kernel-rootfs-" + Guid.NewGuid().ToString("N"));
        const string kernelRelease = "6.18.38-3-lts-breakwater";
        try
        {
            var modules = Path.Combine(rootfs, "usr", "lib", "modules", kernelRelease);
            var boot = Path.Combine(rootfs, "boot");
            Directory.CreateDirectory(modules);
            Directory.CreateDirectory(boot);
            await File.WriteAllTextAsync(Path.Combine(modules, "pkgbase"), "linux-lts-breakwater\n");
            await File.WriteAllBytesAsync(Path.Combine(boot, "vmlinuz-linux-lts-breakwater"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(boot, "initramfs-linux-lts-breakwater.img"), [2]);

            var artifacts = SystemImageBuilder.ResolveKernelBootArtifacts(rootfs, kernelRelease);

            Assert.AreEqual(
                Path.Combine(boot, "vmlinuz-linux-lts-breakwater"),
                artifacts.Vmlinuz);
            Assert.AreEqual(
                Path.Combine(boot, "initramfs-linux-lts-breakwater.img"),
                artifacts.Initramfs);
        }
        finally
        {
            if (Directory.Exists(rootfs))
            {
                Directory.Delete(rootfs, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Kernel_Boot_Artifacts_Do_Not_Fall_Back_To_Another_Kernel_Name()
    {
        var rootfs = Path.Combine(Path.GetTempPath(), "custom-kernel-rootfs-" + Guid.NewGuid().ToString("N"));
        const string kernelRelease = "6.18.38-3-lts-breakwater";
        try
        {
            var modules = Path.Combine(rootfs, "usr", "lib", "modules", kernelRelease);
            var boot = Path.Combine(rootfs, "boot");
            Directory.CreateDirectory(modules);
            Directory.CreateDirectory(boot);
            await File.WriteAllTextAsync(Path.Combine(modules, "pkgbase"), "linux-lts-breakwater\n");
            await File.WriteAllBytesAsync(Path.Combine(boot, "vmlinuz-linux"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(boot, "initramfs-linux.img"), [2]);

            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => SystemImageBuilder.ResolveKernelBootArtifacts(rootfs, kernelRelease));

            StringAssert.Contains(exception.Message, "vmlinuz-linux-lts-breakwater");
        }
        finally
        {
            if (Directory.Exists(rootfs))
            {
                Directory.Delete(rootfs, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Product_Header_Uses_Product_Efi_Variable_Namespace()
    {
        var descriptor = BreakwaterProduct();
        descriptor.EfiVariablePrefix = "Breakwater";
        descriptor.EfiVendorGuid = "12345678-9abc-def0-1234-56789abcdef0";
        var header = BuildToolCommands.RenderProductHeader(
            descriptor.ToPlan(2),
            new SystemImageSecurityPlan(false, false, true, true));

        StringAssert.Contains(header, "#define ARCH_AB_BOOT_NEXT_NAME L\"BreakwaterBootNext\"");
        StringAssert.Contains(
            header,
            "#define ARCH_AB_VBMETA_WARNING_DISABLED_NAME L\"BreakwaterVbmetaPreflightWarningDisabled\"");
        StringAssert.Contains(
            header,
            "#define ARCH_AB_SECURE_BOOT_WARNING_DISABLED_NAME L\"BreakwaterSecureBootWarningDisabled\"");
        StringAssert.Contains(
            header,
            "#define ARCH_AB_EFI_VENDOR_GUID {0x12345678, 0x9ABC, 0xDEF0, {0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0}}");
        StringAssert.Contains(header, "#define ARCH_AB_WARN_SECURE_BOOT 0");
        StringAssert.Contains(header, "#define ARCH_AB_AVB_FAIL_CLOSED 1");
    }

    [TestMethod]
    public void Initial_Image_Uses_Profile_Canonical_Selector_Path()
    {
        var esp = Path.Combine(Path.GetTempPath(), "breakwater-esp");

        var selector = SystemImageBuilder.ProductSelectorPathFor(
            BreakwaterProduct().ToPlan(2),
            esp);

        Assert.AreEqual(
            Path.Combine(esp, "EFI", "Breakwater", "BreakwaterBoot.efi"),
            selector);
    }

    [TestMethod]
    public void Recovery_Fstab_Is_Rendered_From_The_Consumer_Plan()
    {
        var entries = SystemImageBuilder.MergeRecoveryFstab(
            [
                new SystemImageFstabEntryPlan("LABEL=BREAKWATER-STATE", "/var", "ext4", "defaults,nodev,nosuid", 0, 2),
                new SystemImageFstabEntryPlan("LABEL=OLD-ESP", "/efi", "vfat", "defaults", 0, 2)
            ],
            [
                new SystemImageFstabEntryPlan("LABEL=BW-ESP", "/efi", "vfat", "umask=0077,nofail", 0, 2)
            ]);
        var fstab = SystemImageBuilder.RenderPlanFstab(entries);

        Assert.AreEqual(
            "LABEL=BREAKWATER-STATE /var ext4 defaults,nodev,nosuid 0 2\n" +
            "LABEL=BW-ESP /efi vfat umask=0077,nofail 0 2\n",
            fstab);
        Assert.IsFalse(fstab.Contains("LABEL=state", StringComparison.Ordinal));
        Assert.IsFalse(fstab.Contains("LABEL=esp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void No_Selinux_Live_Installer_Uses_Baseline_And_Product_Packages()
    {
        var product = BreakwaterProduct().ToPlan(2);
        var packages = ReleaseArtifactBuilder.BuildLiveInstallerPackageList(
            ["base", "linux", "mkinitcpio-archiso"],
            ["breakwater-recovery", "iproute2"],
            product,
            selinux: false);

        CollectionAssert.Contains(packages.ToArray(), "breakwater-installer");
        CollectionAssert.Contains(packages.ToArray(), "iproute2");
        CollectionAssert.DoesNotContain(packages.ToArray(), "breakwater-recovery");
        Assert.IsFalse(packages.Any(package => package.StartsWith("homeharbor-", StringComparison.Ordinal)));
        ReleaseArtifactBuilder.ValidateLiveInstallerPackageList(
            string.Join('\n', packages),
            product,
            selinux: false);
        CollectionAssert.AreEqual(
            new[] { "-zlzma,109", "-E", "ztailpacking" },
            ReleaseArtifactBuilder.BaselineLiveInstallerErofsOptions().ToArray());
    }

    [TestMethod]
    public void No_Selinux_Live_Installer_Rejects_Selinux_Inputs()
    {
        var product = BreakwaterProduct().ToPlan(2);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ReleaseArtifactBuilder.ValidateLiveInstallerPackageList(
                "base\nmkinitcpio-archiso\nbreakwater-installer\nlibselinux\n",
                product,
                selinux: false));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ReleaseArtifactBuilder.ValidateBaselineLiveInstallerBootConfiguration(
                "grub.cfg",
                "linux /breakwater/boot/x86_64/vmlinuz-linux selinux=1 enforcing=1"));

        ReleaseArtifactBuilder.ValidateBaselineLiveInstallerBootConfiguration(
            "grub.cfg",
            "linux /breakwater/boot/x86_64/vmlinuz-linux audit=1 lsm=landlock,lockdown,yama,integrity,bpf");
    }

    [TestMethod]
    public async Task Live_Installer_Trust_Anchor_Uses_Product_Path()
    {
        var root = Path.Combine(Path.GetTempPath(), "breakwater-iso-profile-" + Guid.NewGuid().ToString("N"));
        var key = Path.Combine(root, "release.pub.pem");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(key, "test-key");
            await ReleaseArtifactBuilder.InstallLiveInstallerTrustAnchorAsync(
                root,
                key,
                BreakwaterProduct().ToPlan(2),
                CancellationToken.None);

            var installed = Path.Combine(root, "airootfs", "etc", "breakwater", "release.pub.pem");
            Assert.IsTrue(File.Exists(installed));
            Assert.AreEqual("test-key", await File.ReadAllTextAsync(installed));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Release_Build_Resolves_One_Key_Pair_Before_Image_And_Release()
    {
        var root = Path.Combine(Path.GetTempPath(), "breakwater-release-keys-" + Guid.NewGuid().ToString("N"));
        var privateKey = Path.Combine(root, "release.pem");
        var publicKey = Path.Combine(root, "release.pub.pem");
        var names = new[]
        {
            "ARCH_AB_RELEASE_PRIVATE_KEY",
            "ARCH_AB_RELEASE_PUBLIC_KEY",
            "ARCH_AB_RELEASE_KEY_ID"
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(privateKey, "private-test-key");
            await File.WriteAllTextAsync(publicKey, "public-test-key");
            Environment.SetEnvironmentVariable(names[0], privateKey);
            Environment.SetEnvironmentVariable(names[1], publicKey);
            Environment.SetEnvironmentVariable(names[2], "test-key");
            var product = BreakwaterProduct().ToPlan(2);
            var order = new List<string>();

            await ReleaseBuildSigningCoordinator.RunAsync(
                root,
                "1.2.3",
                product,
                _ =>
                {
                    order.Add("image");
                    Assert.AreEqual(
                        Path.GetFullPath(publicKey),
                        SystemImageBuilder.RequireReleasePublicKeyForImage(product));
                    Assert.AreEqual(
                        Path.Combine(root, "rootfs", "etc", "breakwater", "release.pub.pem"),
                        SystemImageBuilder.ReleaseTrustAnchorPath(Path.Combine(root, "rootfs"), product));
                    return Task.CompletedTask;
                },
                _ =>
                {
                    order.Add("release");
                    Assert.AreEqual(Path.GetFullPath(publicKey), ProductBuildEnvironment.Optional(product, "RELEASE_PUBLIC_KEY"));
                    Assert.AreEqual(Path.GetFullPath(privateKey), ProductBuildEnvironment.Optional(product, "RELEASE_PRIVATE_KEY"));
                    return Task.CompletedTask;
                });

            CollectionAssert.AreEqual(new[] { "image", "release" }, order);
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Release_Key_Resolver_Rejects_An_Incomplete_Explicit_Pair()
    {
        var root = Path.Combine(Path.GetTempPath(), "breakwater-incomplete-key-" + Guid.NewGuid().ToString("N"));
        var privateKey = Path.Combine(root, "release.pem");
        var privateName = "ARCH_AB_RELEASE_PRIVATE_KEY";
        var publicName = "ARCH_AB_RELEASE_PUBLIC_KEY";
        var previousPrivate = Environment.GetEnvironmentVariable(privateName);
        var previousPublic = Environment.GetEnvironmentVariable(publicName);
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(privateKey, "private-test-key");
            Environment.SetEnvironmentVariable(privateName, privateKey);
            Environment.SetEnvironmentVariable(publicName, null);

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseBuildSigningCoordinator.ResolveAsync(
                    root,
                    "1.2.3",
                    BreakwaterProduct().ToPlan(2),
                    ReleaseChannel.Dev,
                    new ProcessCommandRunner(),
                    CancellationToken.None));

            Assert.Contains("must be supplied as one pair", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(privateName, previousPrivate);
            Environment.SetEnvironmentVariable(publicName, previousPublic);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Productized_Initramfs_Source_Separates_Code_Path_And_Kernel_Prefixes()
    {
        var product = BreakwaterProduct().ToPlan(2) with
        {
            DisplayName = "Breakwater Appliance",
            PackagePrefix = "bw-package",
            KernelParameterPrefix = "bw-kernel",
            EnvironmentPrefix = "BW_ENV",
            EfiVariablePrefix = "BwCode",
            EfiVendorGuid = "12345678-9abc-def0-1234-56789abcdef0"
        };

        var result = BuildToolCommands.ProductizeText(
            "8be4df61-93ca-4d2c-bb7c-0f9d9aee5f3a HOMEHARBOR HomeHarbor homeharbor /run/homeharbor rd.homeharbor.verity homeharbor.super",
            product);

        Assert.AreEqual(
            "12345678-9abc-def0-1234-56789abcdef0 BW_ENV BwCode bw-package /run/bw-package rd.bw-kernel.verity bw-kernel.super",
            result);
    }

    [TestMethod]
    public void Relative_Source_Path_Allows_Systemd_Template_Names()
    {
        var result = SystemImageBuildDescriptor.ResolvePath(
            "/repo",
            "1.0.0",
            "os/systemd/wg-quick@.service.d/10-product.conf",
            "source");

        Assert.AreEqual(
            Path.GetFullPath("/repo/os/systemd/wg-quick@.service.d/10-product.conf"),
            result);
    }

    [TestMethod]
    public void Generic_Live_Installer_Startup_Uses_Product_Cli_Contract()
    {
        var profile = ReleaseArtifactBuilder.RenderGenericLiveInstallerBashProfile(
            BreakwaterProduct().ToPlan(2),
            "/opt/breakwater-installer/payloads");

        StringAssert.Contains(
            profile,
            "'/usr/lib/breakwater/installer/Breakwater.Installer' list-disks || true");
        StringAssert.Contains(profile, "'/opt/breakwater-installer/payloads'");
        StringAssert.Contains(profile, "export BREAKWATER_INSTALLER_STARTED=1");
        Assert.IsFalse(profile.Contains("--mode", StringComparison.Ordinal));
        Assert.IsFalse(profile.Contains("--payload-dir", StringComparison.Ordinal));
    }

    private static SystemImageProductDescriptor BreakwaterProduct()
        => new()
        {
            Id = "breakwater",
            DisplayName = "Breakwater",
            PackagePrefix = "breakwater",
            ServicePrefix = "breakwater",
            EfiDirectory = "EFI/Breakwater",
            BootStatePath = "EFI/Breakwater/boot_state.json",
            KernelParameterPrefix = "breakwater",
            LocalRepository = "breakwater-local",
            ControlPackage = "breakwater-control",
            RecoveryPackage = "breakwater-recovery",
            InstallerPackage = "breakwater-installer",
            InstallerEntryPoint = "/usr/lib/breakwater/installer/Breakwater.Installer"
        };
}
