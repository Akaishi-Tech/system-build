using System.Security.Cryptography;
using HomeHarbor.Tooling;

namespace HomeHarbor.SystemBuild.Tests;

[TestClass]
public sealed class ReleaseArtifactSidecarTests
{
    [TestMethod]
    public async Task System_Ota_Sidecar_Verifies_The_Extracted_Rootfs_Name()
    {
        var root = Path.Combine(Path.GetTempPath(), "system-ota-sidecar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "image-work", "root_a.logical");
            var extracted = Path.Combine(root, "extracted", "breakwater-system-ota-1.0.0");
            var destination = Path.Combine(extracted, "rootfs.img");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, [0x10, 0x20, 0x30, 0x40]);

            await ReleaseArtifactBuilder.CopyReleaseFileWithShaAsync(
                source,
                destination,
                CancellationToken.None);

            await AssertSha256SumCheckSucceedsAsync(extracted, "rootfs.img");
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
    public async Task Kernel_Ota_Sidecar_Verifies_The_Extracted_Modules_Name()
    {
        var root = Path.Combine(Path.GetTempPath(), "kernel-ota-sidecar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "image-work", "modules_a.logical");
            var extracted = Path.Combine(root, "extracted", "breakwater-kernel-lts-ota-1.0.0");
            var destination = Path.Combine(extracted, "modules.img");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, [0x50, 0x60, 0x70, 0x80]);

            await ReleaseArtifactBuilder.CopyReleaseFileWithShaAsync(
                source,
                destination,
                CancellationToken.None);

            await AssertSha256SumCheckSucceedsAsync(extracted, "modules.img");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AssertSha256SumCheckSucceedsAsync(string directory, string payloadFileName)
    {
        var sidecar = (await File.ReadAllTextAsync(Path.Combine(directory, payloadFileName + ".sha256")))
            .TrimEnd('\r', '\n');
        var separator = sidecar.IndexOf("  ", StringComparison.Ordinal);
        Assert.AreEqual(64, separator);
        Assert.AreEqual(payloadFileName, sidecar[(separator + 2)..]);

        var payload = Path.Combine(directory, sidecar[(separator + 2)..]);
        await using var input = File.OpenRead(payload);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
        Assert.AreEqual(sidecar[..separator], actualHash);
    }
}
