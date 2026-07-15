using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class RecoveryKernelTreePrunerTests
{
    [TestMethod]
    public void Prune_Copies_Allowlisted_Modules_With_Dep_And_Softdep()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var sourceModules = Path.Combine(tempDir, "modules-source");
            var sourceFirmware = Path.Combine(tempDir, "firmware-source");
            var destinationModules = Path.Combine(tempDir, "modules-destination");
            var destinationFirmware = Path.Combine(tempDir, "firmware-destination");
            var releaseRoot = Path.Combine(sourceModules, "1.2.3-homeharbor");
            _ = Directory.CreateDirectory(releaseRoot);
            WriteRequiredBuiltIns(releaseRoot);
            WriteModule(releaseRoot, "kernel/drivers/net/ethernet/intel/e1000e/e1000e.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/ptp/ptp.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/pps/pps_core.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/net/ethernet/realtek/r8169.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/net/phy/realtek.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/gpu/drm/amd/amdgpu/amdgpu.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/net/wireless/intel/iwlwifi/iwlwifi.ko.zst");
            File.WriteAllText(Path.Combine(releaseRoot, "modules.dep"), """
                kernel/drivers/net/ethernet/intel/e1000e/e1000e.ko.zst: kernel/drivers/ptp/ptp.ko.zst kernel/drivers/pps/pps_core.ko.zst
                kernel/drivers/ptp/ptp.ko.zst:
                kernel/drivers/pps/pps_core.ko.zst:
                kernel/drivers/net/ethernet/realtek/r8169.ko.zst:
                kernel/drivers/net/phy/realtek.ko.zst:
                kernel/drivers/gpu/drm/amd/amdgpu/amdgpu.ko.zst:
                kernel/drivers/net/wireless/intel/iwlwifi/iwlwifi.ko.zst:
                """);
            File.WriteAllText(Path.Combine(releaseRoot, "modules.softdep"), "softdep r8169 pre: realtek\n");

            WriteFirmware(sourceFirmware, "rtl_nic/rtl8168h-2.fw.zst");
            WriteFirmware(sourceFirmware, "bnx2/bnx2-mips-09-6.2.1b.fw.zst");
            WriteFirmware(sourceFirmware, "bnx2x/bnx2x-e2-7.13.21.0.fw.zst");
            WriteFirmware(sourceFirmware, "tigon/tg3.bin.zst");
            WriteFirmware(sourceFirmware, "intel/ice/ddp/ice.pkg.zst");
            WriteFirmware(sourceFirmware, "amdgpu/fake.bin.zst");
            WriteFirmware(sourceFirmware, "intel/iwlwifi/fake.ucode.zst");
            WriteFirmware(sourceFirmware, "nvidia/ga102/fake.bin.zst");

            var result = RecoveryKernelTreePruner.Prune(
                sourceModules,
                sourceFirmware,
                destinationModules,
                destinationFirmware);

            Assert.AreEqual("1.2.3-homeharbor", result.KernelRelease);
            Assert.IsTrue(File.Exists(ModulePath(destinationModules, "kernel/drivers/net/ethernet/intel/e1000e/e1000e.ko.zst")));
            Assert.IsTrue(File.Exists(ModulePath(destinationModules, "kernel/drivers/ptp/ptp.ko.zst")));
            Assert.IsTrue(File.Exists(ModulePath(destinationModules, "kernel/drivers/pps/pps_core.ko.zst")));
            Assert.IsTrue(File.Exists(ModulePath(destinationModules, "kernel/drivers/net/ethernet/realtek/r8169.ko.zst")));
            Assert.IsTrue(File.Exists(ModulePath(destinationModules, "kernel/drivers/net/phy/realtek.ko.zst")));
            Assert.IsFalse(File.Exists(ModulePath(destinationModules, "kernel/drivers/gpu/drm/amd/amdgpu/amdgpu.ko.zst")));
            Assert.IsFalse(File.Exists(ModulePath(destinationModules, "kernel/drivers/net/wireless/intel/iwlwifi/iwlwifi.ko.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationFirmware, "rtl_nic", "rtl8168h-2.fw.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationFirmware, "bnx2", "bnx2-mips-09-6.2.1b.fw.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationFirmware, "bnx2x", "bnx2x-e2-7.13.21.0.fw.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationFirmware, "tigon", "tg3.bin.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationFirmware, "intel", "ice", "ddp", "ice.pkg.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(destinationFirmware, "amdgpu", "fake.bin.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(destinationFirmware, "intel", "iwlwifi", "fake.ucode.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(destinationFirmware, "nvidia", "ga102", "fake.bin.zst")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Prune_Fails_When_Required_Core_Module_Is_Not_File_Or_Builtin()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var sourceModules = Path.Combine(tempDir, "modules-source");
            var sourceFirmware = Path.Combine(tempDir, "firmware-source");
            var releaseRoot = Path.Combine(sourceModules, "1.2.3-homeharbor");
            _ = Directory.CreateDirectory(releaseRoot);
            _ = Directory.CreateDirectory(sourceFirmware);
            WriteRequiredBuiltIns(releaseRoot, "dm-mod");

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                RecoveryKernelTreePruner.Prune(
                    sourceModules,
                    sourceFirmware,
                    Path.Combine(tempDir, "modules-destination"),
                    Path.Combine(tempDir, "firmware-destination")));

            Assert.Contains("dm-mod", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteRequiredBuiltIns(string releaseRoot, params string[] excludedModules)
    {
        var excluded = excludedModules
            .Select(module => module.Replace('_', '-'))
            .ToHashSet(StringComparer.Ordinal);
        File.WriteAllLines(
            Path.Combine(releaseRoot, "modules.builtin"),
            RecoveryKernelTreePruner.RequiredCoreModules
                .Where(module => !excluded.Contains(module))
                .Select(module => "kernel/builtin/" + module.Replace('-', '_') + ".ko"));
    }

    private static void WriteModule(string releaseRoot, string relativePath)
    {
        var path = Path.Combine(releaseRoot, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "module\n");
    }

    private static void WriteFirmware(string firmwareRoot, string relativePath)
    {
        var path = Path.Combine(firmwareRoot, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "firmware\n");
    }

    private static string ModulePath(string destinationModules, string relativePath)
        => Path.Combine(destinationModules, "1.2.3-homeharbor", relativePath);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-recovery-prune-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
