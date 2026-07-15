using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class MainFirmwareTreePrunerTests
{
    [TestMethod]
    public async Task Prune_Keeps_Wired_Storage_Firmware_Forced_Directories_And_Symlink_Targets()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var sourceModules = Path.Combine(tempDir, "modules-source");
            var sourceFirmware = Path.Combine(tempDir, "firmware-source");
            var releaseRoot = Path.Combine(sourceModules, "1.2.3-homeharbor");
            _ = Directory.CreateDirectory(releaseRoot);

            WriteModule(releaseRoot, "kernel/drivers/net/ethernet/intel/e1000/e1000.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/scsi/fake-storage.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/net/wireless/intel/iwlwifi/iwlwifi.ko.zst");
            WriteModule(releaseRoot, "kernel/drivers/gpu/drm/amd/amdgpu/amdgpu.ko.zst");
            WriteModule(releaseRoot, "kernel/sound/pci/fake-audio.ko.zst");

            WriteFirmware(sourceFirmware, "cxgb4/t4fw.bin.zst");
            WriteFirmware(sourceFirmware, "storage/adapter.bin.zst");
            WriteFirmware(sourceFirmware, "targets/fw-real.bin.zst");
            WriteSymlink(sourceFirmware, "aliases/fw.bin.zst", "../targets/fw-real.bin.zst");
            WriteFirmware(sourceFirmware, "rtl_nic/rtl8168h-2.fw.zst");
            WriteFirmware(sourceFirmware, "bnx2/bnx2-mips-09-6.2.1b.fw.zst");
            WriteFirmware(sourceFirmware, "bnx2x/bnx2x-e2-7.13.21.0.fw.zst");
            WriteFirmware(sourceFirmware, "tigon/tg3.bin.zst");
            WriteFirmware(sourceFirmware, "intel/ice/ddp/ice.pkg.zst");
            WriteFirmware(sourceFirmware, "WHENCE.zst");
            WriteFirmware(sourceFirmware, "iwlwifi-bad.ucode.zst");
            WriteFirmware(sourceFirmware, "amdgpu/fake.bin.zst");
            WriteFirmware(sourceFirmware, "rtl_bt/rtl8761bu_fw.bin.zst");
            WriteFirmware(sourceFirmware, "rtw89/rtw8852b_fw.bin.zst");
            WriteFirmware(sourceFirmware, "audio/dsp.bin.zst");

            var runner = new FakeModinfoRunner(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kernel/drivers/net/ethernet/intel/e1000/e1000.ko.zst"] = ["cxgb4/t4fw.bin", "aliases/fw.bin"],
                ["kernel/drivers/scsi/fake-storage.ko.zst"] = ["storage/adapter.bin"],
                ["kernel/drivers/net/wireless/intel/iwlwifi/iwlwifi.ko.zst"] = ["iwlwifi-bad.ucode"],
                ["kernel/drivers/gpu/drm/amd/amdgpu/amdgpu.ko.zst"] = ["amdgpu/fake.bin"],
                ["kernel/sound/pci/fake-audio.ko.zst"] = ["audio/dsp.bin"]
            });

            var result = await MainFirmwareTreePruner.PruneAsync(sourceModules, sourceFirmware, runner);

            Assert.AreEqual("1.2.3-homeharbor", result.KernelRelease);
            Assert.AreEqual(2, result.ModulesScanned);
            Assert.AreEqual(2, result.ModulesWithFirmware);
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "cxgb4", "t4fw.bin.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "storage", "adapter.bin.zst")));
            Assert.AreEqual("../targets/fw-real.bin.zst", new FileInfo(Path.Combine(sourceFirmware, "aliases", "fw.bin.zst")).LinkTarget);
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "targets", "fw-real.bin.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "rtl_nic", "rtl8168h-2.fw.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "bnx2", "bnx2-mips-09-6.2.1b.fw.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "bnx2x", "bnx2x-e2-7.13.21.0.fw.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "tigon", "tg3.bin.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "intel", "ice", "ddp", "ice.pkg.zst")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceFirmware, "WHENCE.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(sourceFirmware, "iwlwifi-bad.ucode.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(sourceFirmware, "amdgpu", "fake.bin.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(sourceFirmware, "rtl_bt", "rtl8761bu_fw.bin.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(sourceFirmware, "rtw89", "rtw8852b_fw.bin.zst")));
            Assert.IsFalse(File.Exists(Path.Combine(sourceFirmware, "audio", "dsp.bin.zst")));
            Assert.IsGreaterThan(0, result.RemovedEntries);
            Assert.IsLessThan(result.OriginalBytes, result.KeptBytes);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Prune_Does_Not_Fail_When_Optional_Firmware_Is_Missing()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var sourceModules = Path.Combine(tempDir, "modules-source");
            var sourceFirmware = Path.Combine(tempDir, "firmware-source");
            var releaseRoot = Path.Combine(sourceModules, "1.2.3-homeharbor");
            _ = Directory.CreateDirectory(releaseRoot);
            _ = Directory.CreateDirectory(sourceFirmware);
            WriteModule(releaseRoot, "kernel/drivers/net/ethernet/intel/e1000/e1000.ko.zst");

            var result = await MainFirmwareTreePruner.PruneAsync(
                sourceModules,
                sourceFirmware,
                new FakeModinfoRunner(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["kernel/drivers/net/ethernet/intel/e1000/e1000.ko.zst"] = ["missing/firmware.bin"]
                }));

            Assert.AreEqual(1, result.ModulesScanned);
            Assert.AreEqual(1, result.MissingFirmwareReferences);
            Assert.AreEqual(0, result.KeptEntries);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class FakeModinfoRunner(IReadOnlyDictionary<string, string[]> firmwareByModule) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("modinfo", fileName);
            var args = arguments.ToArray();
            Assert.AreEqual("-F", args[0]);
            Assert.AreEqual("firmware", args[1]);
            var modulePath = args[2].Replace('\\', '/');
            var firmware = firmwareByModule
                .FirstOrDefault(pair => modulePath.EndsWith(pair.Key, StringComparison.Ordinal))
                .Value ?? [];
            return Task.FromResult(new CommandResult(0, string.Join('\n', firmware) + (firmware.Length == 0 ? string.Empty : "\n"), string.Empty, "modinfo"));
        }
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

    private static void WriteSymlink(string firmwareRoot, string relativePath, string target)
    {
        var path = Path.Combine(firmwareRoot, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ = File.CreateSymbolicLink(path, target);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-main-firmware-prune-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
