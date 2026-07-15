using System.IO.Compression;
using System.Text;

namespace HomeHarbor.Tooling;

internal static class KernelConfigValidator
{
    private static readonly byte[] ConfigMarker = [.. "IKCFG_ST"u8, 0x1f, 0x8b, 0x08];
    private static readonly byte[] ZstdMarker = [0x28, 0xb5, 0x2f, 0xfd];

    private static readonly string[] RequiredOptions =
    [
        "CONFIG_SECURITY",
        "CONFIG_SECURITYFS",
        "CONFIG_SECURITY_NETWORK",
        "CONFIG_SECURITY_SELINUX",
        "CONFIG_SECURITY_SELINUX_BOOTPARAM",
        "CONFIG_AUDIT",
        "CONFIG_AUDITSYSCALL",
        "CONFIG_EROFS_FS_XATTR",
        "CONFIG_EROFS_FS_SECURITY",
        "CONFIG_EXT4_FS_SECURITY",
        "CONFIG_TMPFS",
        "CONFIG_TMPFS_XATTR"
    ];

    internal static async Task ValidateAsync(
        string kernelImage,
        string workDirectory,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(kernelImage) || new FileInfo(kernelImage).Length == 0)
        {
            throw new InvalidOperationException("missing nonempty kernel image for SELinux config validation: " + kernelImage);
        }

        var image = await File.ReadAllBytesAsync(kernelImage, cancellationToken);
        var config = ExtractEmbeddedConfig(image);
        config ??= await ExtractFromZstdPayloadAsync(
                image,
                workDirectory,
                runner,
                cancellationToken);

        if (config is null)
        {
            throw new InvalidOperationException(
                $"kernel image {kernelImage} does not contain an extractable CONFIG_IKCONFIG payload");
        }

        ValidateConfig(config, kernelImage);
    }

    internal static void ValidateConfig(string config, string label)
    {
        var enabled = config.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.EndsWith("=y", StringComparison.Ordinal))
            .Select(line => line[..^2])
            .ToHashSet(StringComparer.Ordinal);
        var missing = RequiredOptions.Where(option => !enabled.Contains(option)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"kernel config {label} is missing required SELinux filesystem security options: {string.Join(", ", missing)}");
        }
    }

    internal static string? ExtractEmbeddedConfig(ReadOnlySpan<byte> image)
    {
        var markerOffset = image.IndexOf(ConfigMarker);
        if (markerOffset < 0)
        {
            return null;
        }

        try
        {
            using var source = new MemoryStream(image[(markerOffset + "IKCFG_ST"u8.Length)..].ToArray());
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return reader.ReadToEnd();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<string?> ExtractFromZstdPayloadAsync(
        byte[] image,
        string workDirectory,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        _ = Directory.CreateDirectory(workDirectory);
        try
        {
            var searchOffset = 0;
            while (searchOffset < image.Length)
            {
                var relativeOffset = image.AsSpan(searchOffset).IndexOf(ZstdMarker);
                if (relativeOffset < 0)
                {
                    return null;
                }

                var payloadOffset = searchOffset + relativeOffset;
                var payload = Path.Combine(workDirectory, "kernel.zst");
                var unpacked = Path.Combine(workDirectory, "kernel.unpacked");
                await using (var output = File.Create(payload))
                {
                    await output.WriteAsync(image.AsMemory(payloadOffset), cancellationToken);
                }

                _ = await runner.RunAsync(
                    "sh",
                    ["-c", "zstd --decompress --stdout -- \"$1\" > \"$2\"", "sh", payload, unpacked],
                    new CommandRunOptions(ThrowOnStartFailure: false),
                    cancellationToken);
                // A bzImage has trailing boot metadata after the compressed
                // vmlinux frame. zstd reports the trailing bytes as an error,
                // but stdout already contains the complete decompressed ELF.
                if (File.Exists(unpacked) && new FileInfo(unpacked).Length > 0)
                {
                    var config = ExtractEmbeddedConfig(await File.ReadAllBytesAsync(unpacked, cancellationToken));
                    if (config is not null)
                    {
                        return config;
                    }
                }

                File.Delete(payload);
                File.Delete(unpacked);
                searchOffset = payloadOffset + ZstdMarker.Length;
            }

            return null;
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }
}
