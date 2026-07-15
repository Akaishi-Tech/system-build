namespace HomeHarbor.Tooling;

public sealed class RootlessBuildExecutor(ICommandRunner runner)
{
    internal const string SystemdResolvedTarget = "/run/systemd/resolve/stub-resolv.conf";

    private static readonly string[] MappedRootPrefix =
    [
        "--fork",
        "--pid",
        "--mount",
        "--propagation",
        "unchanged",
        "--setgroups",
        "allow",
        "--map-auto",
        "--map-root-user",
        "--setuid",
        "0",
        "--setgid",
        "0"
    ];

    private readonly ICommandRunner _runner = runner;

    public async Task RequireReadyAsync(CancellationToken cancellationToken = default)
    {
        await RequireNonRootAsync(cancellationToken);
        foreach (var tool in new[] { "fakeroot", "unshare", "pacstrap", "arch-chroot" })
        {
            await NeedAsync(tool, cancellationToken);
        }

        await RequireHelpOptionAsync("pacstrap", "-N", cancellationToken);
        await RequireHelpOptionAsync("pacstrap", "-C", cancellationToken);
        await RequireHelpOptionAsync("arch-chroot", "-N", cancellationToken);
        await RequireMappedRootAsync(cancellationToken);
    }

    public async Task RequireNonRootAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync("id", ["-u"], cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("could not determine current uid");
        if (string.Equals(result.Stdout.Trim(), "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("image builders must run as a normal user, not as real root");
        }
    }

    public async Task<CommandResult> RunPacstrapAsync(
        string rootfs,
        IEnumerable<string> packages,
        CommandRunOptions? options = null,
        string? pacmanConfig = null,
        CancellationToken cancellationToken = default)
    {
        RequireEmptyPacstrapRoot(rootfs);
        var result = await _runner.RunAsync(
            "pacstrap",
            PacstrapArguments(rootfs, packages, pacmanConfig),
            IsolatedOptions(options),
            cancellationToken);
        _ = ValidatePacstrapResult(rootfs, result);
        ClearPacstrapPackageCache(rootfs);
        return result;
    }

    public Task<CommandResult> RunPacstrapAsync(
        string rootfs,
        IEnumerable<string> packages,
        CommandRunOptions? options,
        CancellationToken cancellationToken)
        => RunPacstrapAsync(rootfs, packages, options, pacmanConfig: null, cancellationToken);

    internal static void RequireEmptyPacstrapRoot(string rootfs)
    {
        var localDatabase = PacmanLocalDatabasePath(rootfs);
        if (Directory.Exists(localDatabase) && Directory.EnumerateDirectories(localDatabase).Any())
        {
            throw new InvalidOperationException(
                "pacstrap requires a disposable root without an existing local package database: " + rootfs);
        }
    }

    internal static CommandResult ValidatePacstrapResult(string rootfs, CommandResult result)
    {
        _ = result.EnsureSuccess("could not install packages into the disposable root");

        // pacstrap's unshare cleanup can mask the nested pacman exit status.
        // Treat its explicit failure marker as authoritative even when the
        // wrapper itself reports success.
        if (result.CombinedOutput.Contains(
                "==> ERROR: Failed to install packages to new root",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "pacstrap reported that package installation failed despite returning success" +
                (string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? string.Empty
                    : Environment.NewLine + result.CombinedOutput.Trim()));
        }

        var localDatabase = PacmanLocalDatabasePath(rootfs);
        if (!Directory.Exists(localDatabase) || !Directory.EnumerateDirectories(localDatabase).Any())
        {
            throw new InvalidOperationException(
                "pacstrap returned success without creating a populated local package database in " + rootfs);
        }

        return result;
    }

    internal static void ClearPacstrapPackageCache(string rootfs)
    {
        var packageCache = Path.Combine(
            Path.GetFullPath(rootfs),
            "var",
            "cache",
            "pacman",
            "pkg");
        if (Directory.Exists(packageCache))
        {
            Directory.Delete(packageCache, recursive: true);
        }
        else if (File.Exists(packageCache) || new FileInfo(packageCache).LinkTarget is not null)
        {
            File.Delete(packageCache);
        }

        _ = Directory.CreateDirectory(packageCache);
    }

    private static string PacmanLocalDatabasePath(string rootfs)
        => Path.Combine(Path.GetFullPath(rootfs), "var", "lib", "pacman", "local");

    public static IReadOnlyList<string> PacstrapArguments(
        string rootfs,
        IEnumerable<string> packages,
        string? pacmanConfig = null)
    {
        // Keep pacstrap's package cache inside the disposable target root. The
        // host cache can contain an older, byte-different build of a locally
        // maintained package with the same pkgver/pkgrel, which then fails the
        // checksum recorded in the freshly generated local repository.
        var arguments = new List<string> { "-N" };
        if (!string.IsNullOrWhiteSpace(pacmanConfig))
        {
            arguments.Add("-C");
            arguments.Add(Path.GetFullPath(pacmanConfig));
        }

        arguments.Add(rootfs);
        arguments.AddRange(packages);
        return arguments;
    }

    public Task<CommandResult> RunMappedRootAsync(
        string fileName,
        IEnumerable<string> arguments,
        CommandRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => _runner.RunAsync(
            "unshare",
            MappedRootArguments(fileName, arguments),
            IsolatedOptions(options),
            cancellationToken);

    public async Task<CommandResult> RunMappedChrootAsync(
        string rootfs,
        string fileName,
        IEnumerable<string> arguments,
        CommandRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var resolver = StageResolverConfiguration(rootfs);
        return await _runner.RunAsync(
            "arch-chroot",
            MappedChrootArguments(rootfs, fileName, arguments),
            IsolatedOptions(options),
            cancellationToken);
    }

    internal static IReadOnlyList<string> MappedChrootArguments(
        string rootfs,
        string fileName,
        IEnumerable<string> arguments)
        => ["-N", "-r", rootfs, fileName, .. arguments];

    public static IReadOnlyList<string> MappedRootArguments(string fileName, IEnumerable<string> arguments)
        => [.. MappedRootPrefix, fileName, .. arguments];

    internal static CommandRunOptions IsolatedOptions(CommandRunOptions? options)
    {
        options ??= CommandRunOptions.Default;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = "/tmp",
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["LOGNAME"] = "root",
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/bin:/bin",
            ["SHELL"] = "/bin/bash",
            ["TERM"] = "dumb",
            ["USER"] = "root"
        };
        foreach (var (key, value) in options.Environment)
        {
            environment[key] = value;
        }

        return options with
        {
            ClearEnvironment = true,
            EnvironmentOverride = environment
        };
    }

    internal static IDisposable StageResolverConfiguration(
        string rootfs,
        string source = "/etc/resolv.conf")
    {
        if (!File.Exists(source))
        {
            return ResolverRestore.Empty;
        }

        var destination = Path.Combine(Path.GetFullPath(rootfs), "etc", "resolv.conf");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var backup = FileEntryExists(destination)
            ? destination + ".homeharbor-" + Guid.NewGuid().ToString("N")
            : null;
        try
        {
            if (backup is not null)
            {
                File.Move(destination, backup);
            }

            File.Copy(source, destination);
            return new ResolverRestore(destination, backup);
        }
        catch
        {
            RestoreResolverConfiguration(destination, backup);
            throw;
        }
    }

    internal static void ConfigureSystemdResolved(string rootfs)
    {
        var destination = Path.Combine(Path.GetFullPath(rootfs), "etc", "resolv.conf");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".homeharbor-" + Guid.NewGuid().ToString("N");
        try
        {
            _ = File.CreateSymbolicLink(temporary, SystemdResolvedTarget);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (FileEntryExists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool FileEntryExists(string path)
        => File.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static void RestoreResolverConfiguration(string destination, string? backup)
    {
        if (FileEntryExists(destination))
        {
            File.Delete(destination);
        }

        if (backup is not null && FileEntryExists(backup))
        {
            File.Move(backup, destination);
        }
    }

    private sealed class ResolverRestore(string? destination = null, string? backup = null) : IDisposable
    {
        internal static ResolverRestore Empty { get; } = new();

        public void Dispose()
        {
            if (destination is not null)
            {
                RestoreResolverConfiguration(destination, backup);
            }
        }
    }

    private async Task NeedAsync(string command, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "sh",
            ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", command],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("missing required rootless build tool: " + command);
        }
    }

    private async Task RequireHelpOptionAsync(string command, string option, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            command,
            ["-h"],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        if (result.ExitCode == 127 || !result.CombinedOutput.Contains(option, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{command} must support {option} rootless unshare mode");
        }
    }

    private async Task RequireMappedRootAsync(CancellationToken cancellationToken)
    {
        var result = await RunMappedRootAsync(
            "id",
            ["-u"],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        if (result.ExitCode != 0 || !string.Equals(result.Stdout.Trim(), "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "rootless image builds require working unprivileged user namespaces with subuid/subgid mappings. " +
                "Enable user namespaces and configure /etc/subuid and /etc/subgid for the build user." +
                (string.IsNullOrWhiteSpace(result.CombinedOutput) ? string.Empty : Environment.NewLine + result.CombinedOutput.Trim()));
        }
    }
}
