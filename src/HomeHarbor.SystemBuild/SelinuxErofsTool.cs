namespace HomeHarbor.Tooling;

public sealed class SelinuxErofsTool
{
    private static readonly string[] ToolPackages = ["libsepol", "libselinux", "erofs-utils-selinux"];

    private readonly string _executable;
    private readonly string _fsckExecutable;
    private readonly string _libraryPath;
    private readonly RootlessBuildExecutor _rootless;

    private SelinuxErofsTool(
        string executable,
        string fsckExecutable,
        string libraryPath,
        RootlessBuildExecutor rootless)
    {
        _executable = executable;
        _fsckExecutable = fsckExecutable;
        _libraryPath = libraryPath;
        _rootless = rootless;
    }

    public static async Task<SelinuxErofsTool> CreateAsync(
        string packageDirectory,
        string toolRoot,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        var rootless = new RootlessBuildExecutor(runner);
        if (Directory.Exists(toolRoot))
        {
            var removed = await rootless.RunMappedRootAsync(
                "rm",
                ["-rf", "--", Path.GetFullPath(toolRoot)],
                new CommandRunOptions(StreamError: true),
                cancellationToken);
            _ = removed.EnsureSuccess("could not reset the SELinux EROFS tool root");
        }

        _ = Directory.CreateDirectory(toolRoot);
        foreach (var packageName in ToolPackages)
        {
            var package = await FindPackageAsync(packageDirectory, packageName, runner, cancellationToken);
            var extracted = await rootless.RunMappedRootAsync(
                "bsdtar",
                ["-xpf", package, "-C", toolRoot],
                new CommandRunOptions(StreamError: true),
                cancellationToken);
            _ = extracted.EnsureSuccess("could not extract SELinux EROFS tool package " + packageName);
        }

        var executable = Path.Combine(toolRoot, "usr", "bin", "mkfs.erofs");
        var fsckExecutable = Path.Combine(toolRoot, "usr", "bin", "fsck.erofs");
        var libraryPath = Path.Combine(toolRoot, "usr", "lib");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("erofs-utils-selinux did not provide mkfs.erofs", executable);
        }
        if (!File.Exists(fsckExecutable))
        {
            throw new FileNotFoundException("erofs-utils-selinux did not provide fsck.erofs", fsckExecutable);
        }

        var probe = await runner.RunAsync(
            executable,
            ["--help"],
            new CommandRunOptions(
                EnvironmentOverride: new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = libraryPath },
                ThrowOnStartFailure: false),
            cancellationToken);
        if (probe.ExitCode != 0 || !probe.CombinedOutput.Contains("--file-contexts", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "the locally built erofs-utils-selinux does not support --file-contexts; refusing to create an unlabeled appliance image");
        }

        return new SelinuxErofsTool(executable, fsckExecutable, libraryPath, rootless);
    }

    internal async Task<string> CreatePathWrappersAsync(
        string wrapperDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(wrapperDirectory);
        _ = Directory.CreateDirectory(directory);
        await WriteWrapperAsync(Path.Combine(directory, "mkfs.erofs"), _executable, cancellationToken);
        await WriteWrapperAsync(Path.Combine(directory, "fsck.erofs"), _fsckExecutable, cancellationToken);
        return directory;
    }

    public async Task BuildAsync(
        string output,
        string source,
        string fileContexts,
        string mountPoint,
        IEnumerable<string> options,
        CancellationToken cancellationToken = default)
    {
        RequireFile(fileContexts, "SELinux file contexts are missing");
        if (string.IsNullOrWhiteSpace(mountPoint) || mountPoint[0] != '/')
        {
            throw new InvalidOperationException("EROFS SELinux mount point must be an absolute image path");
        }

        var result = await _rootless.RunMappedRootAsync(
            _executable,
            [
                .. options,
                "--file-contexts=" + Path.GetFullPath(fileContexts),
                "--mount-point=" + mountPoint,
                output,
                source
            ],
            new CommandRunOptions(
                StreamOutput: true,
                StreamError: true,
                EnvironmentOverride: new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = _libraryPath }),
            cancellationToken);
        _ = result.EnsureSuccess("SELinux-aware mkfs.erofs failed");
    }

    public static string RequireFileContexts(string rootfs)
    {
        var config = Path.Combine(rootfs, "etc", "selinux", "config");
        RequireFile(config, "SELinux configuration is missing from image root");
        var policyName = File.ReadAllLines(config)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("SELINUXTYPE=", StringComparison.Ordinal))
            .Select(line => line["SELINUXTYPE=".Length..].Trim())
            .SingleOrDefault();
        if (string.IsNullOrWhiteSpace(policyName) || policyName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("SELinux configuration must declare one safe SELINUXTYPE");
        }

        var fileContexts = Path.Combine(rootfs, "etc", "selinux", policyName, "contexts", "files", "file_contexts");
        RequireFile(fileContexts, "compiled SELinux file contexts are missing from image root");
        return fileContexts;
    }

    private static async Task<string> FindPackageAsync(
        string packageDirectory,
        string expectedPackageName,
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        var matches = new List<string>();
        foreach (var package in Directory.GetFiles(packageDirectory, expectedPackageName + "-*.pkg.tar.*")
                     .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var inspected = await runner.RunAsync("bsdtar", ["-xOf", package, ".PKGINFO"], cancellationToken: cancellationToken);
            _ = inspected.EnsureSuccess("could not inspect local package " + package);
            if (inspected.Stdout.Split('\n').Any(line => line.Trim() == "pkgname = " + expectedPackageName))
            {
                matches.Add(package);
            }
        }

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"expected exactly one {expectedPackageName} package in {packageDirectory}, found {matches.Count}");
    }

    private async Task WriteWrapperAsync(
        string path,
        string executable,
        CancellationToken cancellationToken)
    {
        var script = $"""
            #!/usr/bin/env bash
            set -euo pipefail
            export LD_LIBRARY_PATH={BashSingleQuote(_libraryPath)}
            exec {BashSingleQuote(executable)} "$@"
            """;
        await FileWrites.AtomicWriteTextAsync(path, script, 0755, cancellationToken);
    }

    internal static string BashSingleQuote(string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new InvalidOperationException("shell wrapper paths must not contain control characters");
        }

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new FileNotFoundException(message + ": " + path, path);
        }
    }
}
