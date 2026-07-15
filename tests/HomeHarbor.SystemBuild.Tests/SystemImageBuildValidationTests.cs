using HomeHarbor.Tooling;

namespace HomeHarbor.SystemBuild.Tests;

[TestClass]
public sealed class SystemImageBuildValidationTests
{
    [TestMethod]
    public void Root_Plan_Rejects_Systemd_With_Traditional_Arch_Ab_Verity_Hook()
    {
        var descriptor = RootWithHooks("base", "systemd", "arch-ab-verity", "filesystems");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => descriptor.ToPlan("/repo", "1.0.0", "rootfs"));

        StringAssert.Contains(exception.Message, "systemd");
        StringAssert.Contains(exception.Message, "arch-ab-verity");
    }

    [TestMethod]
    public void Root_Plan_Allows_Udev_With_Traditional_Arch_Ab_Verity_Hook()
    {
        var descriptor = RootWithHooks("base", "udev", "arch-ab-verity", "filesystems");

        var plan = descriptor.ToPlan("/repo", "1.0.0", "rootfs");

        CollectionAssert.AreEqual(
            new[] { "base", "udev", "arch-ab-verity", "filesystems" },
            plan.MkinitcpioHooks.ToArray());
    }

    private static SystemImageRootDescriptor RootWithHooks(params string[] hooks)
        => new()
        {
            Hostname = "breakwater",
            MkinitcpioHooks = [.. hooks]
        };
}
