namespace HomeHarbor.Tooling;

public static class SecureBootAssets
{
    public const string ConsoleArgs = "console=tty0 console=ttyS0,115200n8";
    public const string SelinuxArgs = "lsm=landlock,lockdown,yama,integrity,selinux,bpf selinux=1 enforcing=1 audit=1 audit_backlog_limit=8192";

    private static string BaseArgs => ConsoleArgs + " " + SelinuxArgs;

    public static bool IsEnabled()
        => Env.Flag("HOMEHARBOR_SECURE_BOOT");

    public static string EnrollMode()
        => Env.String("HOMEHARBOR_SECURE_BOOT_ENROLL", "manual");

    public static string BootMode()
        => IsEnabled() ? "secure-boot-raw-uki" : "raw-uki";

    public static (string Key, string Certificate) RequireSigningAssets()
    {
        var key = Env.Optional("HOMEHARBOR_SECURE_BOOT_KEY");
        var cert = Env.Optional("HOMEHARBOR_SECURE_BOOT_CERT");
        return string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(cert) || !File.Exists(key) || !File.Exists(cert)
            ? throw new InvalidOperationException("HOMEHARBOR_SECURE_BOOT_KEY and HOMEHARBOR_SECURE_BOOT_CERT are required when HOMEHARBOR_SECURE_BOOT=1")
            : ((string Key, string Certificate))(Path.GetFullPath(key), Path.GetFullPath(cert));
    }

    public static string GenericBootCmdline(
        string kernelRelease,
        string version,
        long releaseSequence,
        string? vbmetaADigest = null,
        string? vbmetaBDigest = null,
        string? extraArgs = null)
    {
        var vbmetaArgs = string.IsNullOrEmpty(vbmetaADigest) && string.IsNullOrEmpty(vbmetaBDigest)
            ? string.Empty
            : $" homeharbor.vbmeta_a_digest={vbmetaADigest} homeharbor.vbmeta_b_digest={vbmetaBDigest}";
        var appendedArgs = string.IsNullOrWhiteSpace(extraArgs) ? string.Empty : " " + extraArgs.Trim();
        _ = ReleaseSequence.RequirePositive(releaseSequence, "release sequence");
        return BaseArgs + " ro rd.homeharbor.verity=1 root=/dev/mapper/homeharbor-root rootfstype=erofs " +
            $"homeharbor.boot_mode={BootMode()} homeharbor.boot_generic=1 homeharbor.super=/dev/disk/by-partlabel/super " +
            $"homeharbor.kernel_release={kernelRelease}{vbmetaArgs}{appendedArgs} " +
            $"{ReleaseSequence.KernelArgument}={releaseSequence} homeharbor.version={version}";
    }

    public static string SlotCmdline(HomeHarborBootEnvironment env)
        => BaseArgs + " ro rd.homeharbor.verity=1 root=/dev/mapper/homeharbor-root rootfstype=erofs " +
            $"homeharbor.boot_mode={BootMode()} homeharbor.super=/dev/disk/by-partlabel/super " +
            $"homeharbor.slot={env.RootSlot} " +
            $"homeharbor.root_logical={env.RootLogical} homeharbor.kernel_release={env.KernelRelease} " +
            $"homeharbor.modules_logical={env.ModulesLogical} homeharbor.firmware_logical={env.FirmwareLogical} " +
            $"homeharbor.vbmeta_partition={env.VbmetaPartition} homeharbor.vbmeta_digest={env.VbmetaDigest} " +
            $"homeharbor.version={env.Version}";

    public static string RecoveryCmdline()
        => $"{BaseArgs} ro rd.homeharbor.verity=1 homeharbor.boot_mode={BootMode()} " +
            "homeharbor.recovery=1 root=/dev/mapper/homeharbor-recovery-root rootfstype=erofs";

    public static void WriteLoaderEntries(string esp, string mode, string? recoveryHash = null)
    {
        _ = recoveryHash;
        var entries = Path.Combine(esp, "loader", "entries");
        _ = Directory.CreateDirectory(entries);
        FileWrites.AtomicWriteText(Path.Combine(esp, "loader", "loader.conf"), """
            default homeharbor_a.conf
            timeout 3
            console-mode max
            editor no
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0644);

        if (string.Equals(mode, "secure-boot-uki", StringComparison.Ordinal))
        {
            FileWrites.AtomicWriteText(Path.Combine(entries, "homeharbor_a.conf"), """
                title HomeHarbor A
                uki /EFI/HomeHarbor/A/homeharbor.efi
                """.Replace("                ", string.Empty, StringComparison.Ordinal), 0644);
            FileWrites.AtomicWriteText(Path.Combine(entries, "homeharbor_b.conf"), """
                title HomeHarbor B
                uki /EFI/HomeHarbor/B/homeharbor.efi
                """.Replace("                ", string.Empty, StringComparison.Ordinal), 0644);
            FileWrites.AtomicWriteText(Path.Combine(entries, "homeharbor_recovery.conf"), """
                title HomeHarbor Recovery
                uki /EFI/HomeHarbor/Recovery/homeharbor.efi
                """.Replace("                ", string.Empty, StringComparison.Ordinal), 0644);
            return;
        }

        FileWrites.AtomicWriteText(Path.Combine(entries, "homeharbor_a.conf"), """
            title HomeHarbor A
            linux /EFI/HomeHarbor/A/vmlinuz-linux
            initrd /EFI/HomeHarbor/A/initramfs-linux.img
            options console=tty0 console=ttyS0,115200n8 lsm=landlock,lockdown,yama,integrity,selinux,bpf selinux=1 enforcing=1 audit=1 audit_backlog_limit=8192 ro rd.homeharbor.verity=1 root=/dev/mapper/homeharbor-root rootfstype=erofs homeharbor.boot_generic=1 homeharbor.super=/dev/disk/by-partlabel/super
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0644);
        FileWrites.AtomicWriteText(Path.Combine(entries, "homeharbor_b.conf"), """
            title HomeHarbor B
            linux /EFI/HomeHarbor/B/vmlinuz-linux
            initrd /EFI/HomeHarbor/B/initramfs-linux.img
            options console=tty0 console=ttyS0,115200n8 lsm=landlock,lockdown,yama,integrity,selinux,bpf selinux=1 enforcing=1 audit=1 audit_backlog_limit=8192 ro rd.homeharbor.verity=1 root=/dev/mapper/homeharbor-root rootfstype=erofs homeharbor.boot_generic=1 homeharbor.super=/dev/disk/by-partlabel/super
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0644);
        FileWrites.AtomicWriteText(Path.Combine(entries, "homeharbor_recovery.conf"), """
            title HomeHarbor Recovery
            linux /EFI/HomeHarbor/Recovery/vmlinuz-linux
            initrd /EFI/HomeHarbor/Recovery/initramfs-linux.img
            options console=tty0 console=ttyS0,115200n8 lsm=landlock,lockdown,yama,integrity,selinux,bpf selinux=1 enforcing=1 audit=1 audit_backlog_limit=8192 ro rd.homeharbor.verity=1 homeharbor.recovery=1 root=/dev/mapper/homeharbor-recovery-root rootfstype=erofs
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0644);
    }
}
