namespace HomeHarbor.Tooling;

internal sealed record ReleaseSigningKeys(string PrivateKey, string PublicKey, string KeyId);

internal static class ReleaseBuildSigningCoordinator
{
    internal static async Task RunAsync(
        string root,
        string version,
        SystemImageBuildPlan plan,
        Func<CancellationToken, Task> buildImage,
        Func<CancellationToken, Task> buildRelease,
        ICommandRunner? runner = null,
        CancellationToken cancellationToken = default)
        => await RunAsync(
            root,
            version,
            plan.Product,
            buildImage,
            buildRelease,
            runner,
            cancellationToken);

    internal static async Task RunAsync(
        string root,
        string version,
        SystemImageProductPlan product,
        Func<CancellationToken, Task> buildImage,
        Func<CancellationToken, Task> buildRelease,
        ICommandRunner? runner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(buildImage);
        ArgumentNullException.ThrowIfNull(buildRelease);
        var channel = ReleaseChannel.Require(
            ProductBuildEnvironment.Optional(product, "RELEASE_CHANNEL")
                ?? ProductBuildEnvironment.Optional(product, "ISO_CHANNEL")
                ?? ProductBuildEnvironment.Optional(product, "CHANNEL")
                ?? ReleaseChannel.Dev,
            product.EnvironmentPrefix + " release channel");
        var keys = await ResolveAsync(
            root,
            version,
            product,
            channel,
            runner ?? new ProcessCommandRunner(),
            cancellationToken);

        using var environment = ApplyEnvironment(keys);
        await buildImage(cancellationToken);
        await buildRelease(cancellationToken);
    }

    internal static async Task<ReleaseSigningKeys> ResolveAsync(
        string root,
        string version,
        SystemImageProductPlan product,
        string channel,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(runner);
        channel = ReleaseChannel.Require(channel, product.EnvironmentPrefix + " release channel");
        var privateKey = ProductBuildEnvironment.Optional(product, "RELEASE_PRIVATE_KEY");
        var publicKey = ProductBuildEnvironment.Optional(product, "RELEASE_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(privateKey) || !string.IsNullOrWhiteSpace(publicKey))
        {
            if (string.IsNullOrWhiteSpace(privateKey) || string.IsNullOrWhiteSpace(publicKey))
            {
                throw new InvalidOperationException(
                    "product RELEASE_PRIVATE_KEY and RELEASE_PUBLIC_KEY must be supplied as one pair");
            }

            RequireKeyFile(privateKey, "release private key");
            RequireKeyFile(publicKey, "release public key");
            return new ReleaseSigningKeys(
                Path.GetFullPath(privateKey),
                Path.GetFullPath(publicKey),
                ProductBuildEnvironment.String(product, "RELEASE_KEY_ID", channel + "-local"));
        }

        if (channel != ReleaseChannel.Dev)
        {
            throw new InvalidOperationException(
                "product RELEASE_PRIVATE_KEY and RELEASE_PUBLIC_KEY are required for " + channel + " releases");
        }

        if (!SecurityGuards.IsSafeVersion(version))
        {
            throw new InvalidOperationException("release version contains unsafe characters: " + version);
        }

        var keyDir = RootPathGuard.CreateDirectory(
            Path.Combine(Path.GetFullPath(root), ".work", "release-keys", version, product.Id),
            "development release key directory");
        privateKey = Path.Combine(keyDir, "release.pem");
        publicKey = Path.Combine(keyDir, "release.pub.pem");
        if (File.Exists(privateKey) || File.Exists(publicKey))
        {
            if (!File.Exists(privateKey) || !File.Exists(publicKey))
            {
                throw new InvalidOperationException(
                    "development release key directory contains an incomplete key pair: " + keyDir);
            }

            RequireKeyFile(privateKey, "development release private key");
            RequireKeyFile(publicKey, "development release public key");
            return new ReleaseSigningKeys(privateKey, publicKey, "dev-local");
        }

        _ = (await runner.RunAsync(
            "openssl",
            ["genpkey", "-algorithm", "Ed25519", "-out", privateKey],
            cancellationToken: cancellationToken)).EnsureSuccess("failed to generate development release private key");
        _ = (await runner.RunAsync(
            "openssl",
            ["pkey", "-in", privateKey, "-pubout", "-out", publicKey],
            cancellationToken: cancellationToken)).EnsureSuccess("failed to derive development release public key");
        _ = (await runner.RunAsync(
            "chmod",
            ["0600", privateKey],
            cancellationToken: cancellationToken)).EnsureSuccess("failed to protect development release private key");
        _ = (await runner.RunAsync(
            "chmod",
            ["0644", publicKey],
            cancellationToken: cancellationToken)).EnsureSuccess("failed to set development release public key mode");
        RequireKeyFile(privateKey, "development release private key");
        RequireKeyFile(publicKey, "development release public key");
        return new ReleaseSigningKeys(privateKey, publicKey, "dev-local");
    }

    private static IDisposable ApplyEnvironment(ReleaseSigningKeys keys)
        => new ReleaseKeyEnvironmentScope(keys);

    private static void RequireKeyFile(string path, string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.LinkTarget is not null ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(label + " is missing, empty, or not a regular non-link file: " + path);
        }
    }

    private sealed class ReleaseKeyEnvironmentScope : IDisposable
    {
        private static readonly string[] Names =
        [
            "ARCH_AB_RELEASE_PRIVATE_KEY",
            "ARCH_AB_RELEASE_PUBLIC_KEY",
            "ARCH_AB_RELEASE_KEY_ID"
        ];

        private readonly IReadOnlyDictionary<string, string?> _previous;
        private bool _disposed;

        internal ReleaseKeyEnvironmentScope(ReleaseSigningKeys keys)
        {
            _previous = Names.ToDictionary(
                name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            Environment.SetEnvironmentVariable(Names[0], keys.PrivateKey);
            Environment.SetEnvironmentVariable(Names[1], keys.PublicKey);
            Environment.SetEnvironmentVariable(Names[2], keys.KeyId);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            _disposed = true;
        }
    }
}
