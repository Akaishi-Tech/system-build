using HomeHarbor.Tooling;

namespace HomeHarbor.SystemBuild.Tests;

[TestClass]
public sealed class SystemImageRecoveryConfigurationTests
{
    [TestMethod]
    public void RewriteMkinitcpioHooks_Replaces_The_Installed_Recovery_Hook_List()
    {
        var root = Path.Combine(Path.GetTempPath(), "recovery-mkinitcpio-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "etc", "mkinitcpio.conf");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(
                config,
                "MODULES=()\nHOOKS=(base udev autodetect modconf block filesystems fsck)\nCOMPRESSION=\"zstd\"\n");

            SystemImageBuilder.RewriteMkinitcpioHooks(
                config,
                ["base", "systemd", "autodetect", "arch-ab-verity", "filesystems"]);

            Assert.AreEqual(
                "MODULES=()\n" +
                "HOOKS=(base systemd autodetect arch-ab-verity filesystems)\n" +
                "COMPRESSION=\"zstd\"\n",
                File.ReadAllText(config));
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
    public void Recovery_Rootfs_Build_Uses_Recovery_Mkinitcpio_Hooks()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src",
            "HomeHarbor.SystemBuild",
            "SystemImageBuilder.cs"));
        var methodStart = source.IndexOf(
            "private async Task BuildRecoveryRootfsBaseAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task InstallAvbTrustAnchorsAsync(",
            methodStart,
            StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, methodStart);
        Assert.IsGreaterThan(methodStart, methodEnd);
        var method = source[methodStart..methodEnd];
        StringAssert.Contains(
            method,
            "RewriteMkinitcpioHooks(\n" +
            "            Path.Combine(recoveryRootfs, \"etc\", \"mkinitcpio.conf\"),\n" +
            "            plan.Recovery.MkinitcpioHooks);");
        Assert.IsFalse(method.Contains("plan.Rootfs.MkinitcpioHooks", StringComparison.Ordinal));
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "could not locate repository file",
            Path.Combine(segments));
    }
}
