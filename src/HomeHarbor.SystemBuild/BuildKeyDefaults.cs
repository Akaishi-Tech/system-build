using System.Security.Cryptography;

namespace HomeHarbor.Tooling;

public static class BuildKeyDefaults
{
    public static string Apply(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var keys = Path.Combine(fullRoot, "keys");

        _ = SetExisting("HOMEHARBOR_RELEASE_PRIVATE_KEY", keys, ["homeharbor-release-ed25519.pem", "release.pem"]);
        _ = SetExisting("HOMEHARBOR_RELEASE_PUBLIC_KEY", keys, ["homeharbor-release-ed25519.pub.pem", "release.pub.pem"]);
        var secureBootKey = SetExisting("HOMEHARBOR_SECURE_BOOT_KEY", keys, ["homeharbor-secure-boot.key", "secure-boot.key"]);
        _ = SetExisting("HOMEHARBOR_SECURE_BOOT_CERT", keys, ["homeharbor-secure-boot.crt", "secure-boot.crt"]);

        if (!string.IsNullOrWhiteSpace(secureBootKey))
        {
            SetAvbAlgorithmDefault(secureBootKey);
        }

        return fullRoot;
    }

    private static string? SetExisting(string name, string keys, IReadOnlyList<string> fileNames)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
        {
            return Environment.GetEnvironmentVariable(name);
        }

        var path = fileNames.Select(fileName => Path.Combine(keys, fileName)).FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(path))
        {
            Environment.SetEnvironmentVariable(name, path);
        }

        return path;
    }

    private static void SetAvbAlgorithmDefault(string keyPath)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HOMEHARBOR_AVB_ALGORITHM")))
        {
            return;
        }

        var algorithm = TryGetRsaKeySize(keyPath) switch
        {
            2048 => "SHA256_RSA2048",
            4096 => "SHA256_RSA4096",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(algorithm))
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_AVB_ALGORITHM", algorithm);
        }
    }

    private static int? TryGetRsaKeySize(string keyPath)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(keyPath));
            return rsa.KeySize;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string RequireSupportedAvbSigningAlgorithm(string keyPath, string? configuredAlgorithm)
    {
        var keySize = TryGetRsaKeySize(keyPath)
            ?? throw new InvalidOperationException("HOMEHARBOR_SECURE_BOOT_KEY is not a readable RSA PEM key");
        var expected = keySize switch
        {
            2048 => "SHA256_RSA2048",
            4096 => "SHA256_RSA4096",
            _ => throw new InvalidOperationException(
                $"HomeHarborBoot supports only RSA-2048 and RSA-4096 AVB keys; got RSA-{keySize}")
        };
        var actual = string.IsNullOrWhiteSpace(configuredAlgorithm) ? expected : configuredAlgorithm.Trim();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"HOMEHARBOR_AVB_ALGORITHM {actual} does not match the RSA-{keySize} AVB signing key; expected {expected}");
        }

        return expected;
    }
}
