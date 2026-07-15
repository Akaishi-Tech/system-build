using System.Globalization;
using System.Text;

namespace HomeHarbor.Tooling;

public sealed class BuildToolCommands(string root, ICommandRunner? runner = null)
{
    private readonly string _root = BuildKeyDefaults.Apply(root);
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();
    private readonly RootlessBuildExecutor _rootless = new(runner ?? new ProcessCommandRunner());

    public async Task BuildEfiLoaderAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["avbtool", "clang", "lld-link", "make", "openssl"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        var buildDirectory = Path.Combine(_root, ".work", "efi-loader");
        var publicKeyHeader = Path.Combine(buildDirectory, "homeharbor-avb-public-key.h");
        await GenerateEfiAvbPublicKeyHeaderAsync(publicKeyHeader, cancellationToken);
        var makefile = SystemUtilityAssets.RequireAsset(_root, "boot", "bootloader", "Makefile");
        await RunRequiredAsync(
            "make",
            [
                "-C",
                Path.GetDirectoryName(makefile)!,
                "OUTPUT=" + fullOutput,
                "BUILD_DIR=" + buildDirectory,
                "AVB_PUBLIC_KEY_HEADER=" + publicKeyHeader,
                "all"
            ],
            cancellationToken);
    }

    public async Task BuildHomeHarborAvbAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            [
                "-O2",
                "-Wall",
                "-Wextra",
                "-o",
                fullOutput,
                SystemUtilityAssets.RequireAsset(_root, "boot", "avb", "homeharbor-avb.c"),
                "-lcrypto"
            ],
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    public async Task BuildHomeHarborInitAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            HomeHarborInitHelperBuild.CompileArguments(
                fullOutput,
                SystemUtilityAssets.RequireAsset(_root, "boot", "init", "homeharbor-verity.c")),
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    public string GetSelinuxDependencyInputSha256()
        => SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(_root);

    public async Task BuildSelinuxDependencyPackagesAsync(
        string output,
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        RequireNormalPackageBuilder("selinux-dependency-build");
        await RequireToolsAsync(
            ["arch-chroot", "bsdtar", "fakeroot", "git", "makepkg", "pacman", "pacstrap", "repo-add", "unshare"],
            cancellationToken);
        await _rootless.RequireReadyAsync(cancellationToken);

        var workRoot = Path.Combine(_root, ".work");
        var artifactsRoot = Path.Combine(_root, "artifacts");
        var work = RequireManagedBuildPath(
            workDirectory,
            "SELinux dependency package work directory",
            workRoot);
        var packageOutput = RequireManagedBuildPath(
            output,
            "SELinux dependency package output",
            workRoot,
            artifactsRoot);
        RequireSeparateDirectories(work, packageOutput, "SELinux dependency package work and output");

        await DeleteMappedBuildPathAsync(work, cancellationToken);
        await DeleteMappedBuildPathAsync(packageOutput, cancellationToken);
        _ = Directory.CreateDirectory(work);
        _ = Directory.CreateDirectory(packageOutput);

        var inputSha256 = GetSelinuxDependencyInputSha256();
        var plan = SelinuxPackageBuildDescriptor.LoadDefaultPlan(_root);
        await new SelinuxPackageBuilder(_root, _runner).BuildAsync(
            plan,
            work,
            packageOutput,
            cancellationToken);
        await SelinuxDependencyPackageSetProvenance.WriteAsync(
            _root,
            packageOutput,
            inputSha256,
            _runner,
            cancellationToken);
        await SelinuxDependencyPackageSetProvenance.VerifyAsync(
            _root,
            packageOutput,
            _runner,
            cancellationToken);
        Console.WriteLine("Built verified SELinux dependency packages: " + packageOutput);
    }

    public async Task VerifySelinuxDependencyPackagesAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        await RequireToolsAsync(["bsdtar"], cancellationToken);
        await SelinuxDependencyPackageSetProvenance.VerifyAsync(
            _root,
            Path.GetFullPath(input),
            _runner,
            cancellationToken);
        Console.WriteLine("Verified SELinux dependency packages: " + Path.GetFullPath(input));
    }

    public async Task<ArchLocalPackageRepository> ArchPackageAsync(string version, CancellationToken cancellationToken = default)
    {
        RequireSafeVersion(version);
        RequireNormalPackageBuilder("arch-package");

        await RequireToolsAsync(
            ["arch-chroot", "bsdtar", "dotnet", "fakeroot", "git", "makepkg", "pacman", "pacstrap", "pnpm", "repo-add", "tar", "unshare"],
            cancellationToken);
        await _rootless.RequireReadyAsync(cancellationToken);

        var workRoot = Path.Combine(_root, ".work");
        var artifactsRoot = Path.Combine(_root, "artifacts");
        var packageWork = RequireManagedBuildPath(
            Env.String("HOMEHARBOR_PACKAGE_WORK", Path.Combine(workRoot, "arch-package", version)),
            "HOMEHARBOR_PACKAGE_WORK",
            workRoot);
        var packageOutput = RequireManagedBuildPath(
            Env.String("HOMEHARBOR_PACKAGE_OUTPUT", Path.Combine(artifactsRoot, "packages", version)),
            "HOMEHARBOR_PACKAGE_OUTPUT",
            workRoot,
            artifactsRoot);
        RequireSeparateDirectories(packageWork, packageOutput, "package work and output");
        var configuredDependencyCache = Env.Optional("HOMEHARBOR_SELINUX_DEPENDENCY_CACHE");
        var dependencyCache = string.IsNullOrWhiteSpace(configuredDependencyCache)
            ? null
            : Path.GetFullPath(configuredDependencyCache);
        if (dependencyCache is not null)
        {
            RequireSeparateDirectories(
                dependencyCache,
                packageWork,
                "SELinux dependency cache and package work");
            RequireSeparateDirectories(
                dependencyCache,
                packageOutput,
                "SELinux dependency cache and package output");
        }

        var sourceDir = Path.Combine(packageWork, "source");
        var buildDir = Path.Combine(packageWork, "makepkg");
        var sourceTarball = Path.Combine(sourceDir, $"homeharbor-{version}.tar.gz");

        await DeleteMappedBuildPathAsync(packageWork, cancellationToken);
        await DeleteMappedBuildPathAsync(packageOutput, cancellationToken);
        _ = Directory.CreateDirectory(sourceDir);
        _ = Directory.CreateDirectory(buildDir);
        _ = Directory.CreateDirectory(packageOutput);

        var selinuxSourceSha256 = ArchPackageSetProvenance.ComputeSelinuxSourceSha256(_root);
        if (dependencyCache is null)
        {
            var dependencyOutput = Path.Combine(packageWork, "selinux-packages");
            _ = Directory.CreateDirectory(dependencyOutput);
            var dependencyInputSha256 = GetSelinuxDependencyInputSha256();
            await new SelinuxPackageBuilder(_root, _runner).BuildAsync(
                SelinuxPackageBuildDescriptor.LoadDefaultPlan(_root),
                Path.Combine(packageWork, "selinux"),
                dependencyOutput,
                cancellationToken);
            await SelinuxDependencyPackageSetProvenance.WriteAsync(
                _root,
                dependencyOutput,
                dependencyInputSha256,
                _runner,
                cancellationToken);
            dependencyCache = dependencyOutput;
        }

        await SelinuxDependencyPackageSetProvenance.ImportVerifiedAsync(
            _root,
            dependencyCache,
            packageOutput,
            _runner,
            cancellationToken);

        await CreateCleanSourceArchiveAsync(sourceDir, sourceTarball, version, cancellationToken);

        var packagingTarball = Path.Combine(_root, "packaging", "arch", $"homeharbor-{version}.tar.gz");
        if (File.Exists(packagingTarball))
        {
            File.Delete(packagingTarball);
        }

        var environment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(packageWork, "home", ".dotnet"),
            ["HOME"] = Path.Combine(packageWork, "home"),
            ["HOMEHARBOR_VERSION"] = version,
            ["HOMEHARBOR_CHANNEL"] = Env.String("HOMEHARBOR_CHANNEL", ReleaseChannel.Dev),
            ["HOMEHARBOR_SOURCE_TARBALL"] = sourceTarball,
            ["LOGNAME"] = Environment.UserName,
            ["NUGET_PACKAGES"] = Path.Combine(packageWork, "nuget-packages"),
            ["PKGDEST"] = packageOutput,
            ["BUILDDIR"] = buildDir,
            ["USER"] = Environment.UserName,
            ["XDG_CACHE_HOME"] = Path.Combine(packageWork, "home", ".cache")
        };
        _ = Directory.CreateDirectory(environment["HOME"]);
        await RunRequiredAsync(
            "makepkg",
            ["--force", "--cleanbuild", "--clean", "--nodeps"],
            cancellationToken,
            workingDirectory: Path.Combine(_root, "packaging", "arch"),
            environment: environment);

        await ArchPackageArchiveValidator.ValidateHomeHarborPackagesAsync(
            packageOutput,
            version,
            _runner,
            cancellationToken);

        foreach (var package in Directory.GetFiles(packageOutput, "*.pkg.tar.*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            Console.WriteLine(package);
        }

        await ArchPackageSetProvenance.WriteAsync(
            _root,
            version,
            packageOutput,
            selinuxSourceSha256,
            cancellationToken);

        return await ArchLocalPackageRepositoryBuilder.CreateAsync(
            packageOutput,
            Path.Combine(packageWork, "repository"),
            _runner,
            cancellationToken);
    }

    public async Task GenerateEfiAvbPublicKeyHeaderAsync(string output, CancellationToken cancellationToken = default)
    {
        await RequireToolsAsync(["avbtool", "openssl"], cancellationToken);
        var fullOutput = Path.GetFullPath(output);
        var work = Path.Combine(Path.GetTempPath(), "homeharbor-avb-public-key-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var encodedKey = Path.Combine(work, "homeharbor-avb-public-key.avbpub");
            var publicKey = Env.Optional("HOMEHARBOR_AVB_PUBLIC_KEY");
            if (!string.IsNullOrWhiteSpace(publicKey))
            {
                RequireFile(publicKey, "HOMEHARBOR_AVB_PUBLIC_KEY does not point to a readable file");
                if (IsEncodedAvbPublicKey(publicKey))
                {
                    File.Copy(publicKey, encodedKey, overwrite: true);
                }
                else
                {
                    await RunRequiredAsync("avbtool", ["extract_public_key", "--key", publicKey, "--output", encodedKey], cancellationToken);
                }
            }
            else
            {
                var secureBootKey = Env.Optional("HOMEHARBOR_SECURE_BOOT_KEY");
                if (!string.IsNullOrWhiteSpace(secureBootKey))
                {
                    RequireFile(secureBootKey, "HOMEHARBOR_SECURE_BOOT_KEY does not point to a readable file");
                    await RunRequiredAsync("avbtool", ["extract_public_key", "--key", secureBootKey, "--output", encodedKey], cancellationToken);
                }
                else
                {
                    var cert = Env.String("HOMEHARBOR_SECURE_BOOT_CERT", Path.Combine(_root, "certs", "homeharbor-secure-boot.crt"));
                    RequireFile(cert, "no AVB public key source found; set HOMEHARBOR_AVB_PUBLIC_KEY or HOMEHARBOR_SECURE_BOOT_KEY");
                    var pem = Path.Combine(work, "secure-boot-public.pem");
                    var result = await _runner.RunAsync(
                        "openssl",
                        ["x509", "-in", cert, "-pubkey", "-noout"],
                        new CommandRunOptions(StreamError: true),
                        cancellationToken);
                    _ = result.EnsureSuccess("openssl x509 failed");
                    await File.WriteAllTextAsync(pem, result.Stdout, cancellationToken);
                    await RunRequiredAsync("avbtool", ["extract_public_key", "--key", pem, "--output", encodedKey], cancellationToken);
                }
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            var encoded = await File.ReadAllBytesAsync(encodedKey, cancellationToken);
            _ = RequireSelectorSupportedAvbPublicKey(encoded);
            await File.WriteAllTextAsync(fullOutput, RenderAvbPublicKeyHeader(encoded), Encoding.ASCII, cancellationToken);
        }
        finally
        {
            DeleteIfExists(work);
        }
    }

    private static string RenderAvbPublicKeyHeader(byte[] data)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("#include <stdint.h>");
        _ = builder.AppendLine(CultureInfo.InvariantCulture, $"#define HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE {data.Length}U");
        _ = builder.AppendLine("static const uint8_t HomeHarborTrustedAvbPublicKey[HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE] = {");
        for (var i = 0; i < data.Length; i += 12)
        {
            var chunk = data.Skip(i).Take(12).Select(value => "0x" + value.ToString("x2", CultureInfo.InvariantCulture));
            _ = builder.Append("    ");
            _ = builder.Append(string.Join(", ", chunk));
            if (i + 12 < data.Length)
            {
                _ = builder.Append(',');
            }

            _ = builder.AppendLine();
        }

        _ = builder.AppendLine("};");
        return builder.ToString();
    }

    private static bool IsEncodedAvbPublicKey(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8)
        {
            return false;
        }

        var bits = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        return bits is 2048 or 4096 && data.Length == 8 + bits / 8 * 2;
    }

    internal static int RequireSelectorSupportedAvbPublicKey(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8)
        {
            throw new InvalidOperationException("encoded AVB public key is truncated");
        }

        var bits = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        if (bits is not (2048 or 4096))
        {
            throw new InvalidOperationException(
                $"HomeHarborBoot supports only RSA-2048 and RSA-4096 AVB keys; got RSA-{bits}");
        }

        var expectedBytes = checked(8 + bits / 8 * 2);
        if (data.Length != expectedBytes)
        {
            throw new InvalidOperationException(
                $"encoded RSA-{bits} AVB public key has an unexpected size: {data.Length} != {expectedBytes}");
        }

        return bits;
    }

    private static void RequireSafeVersion(string version)
    {
        if (!SecurityGuards.IsSafeVersion(version))
        {
            throw new InvalidOperationException("version must contain only letters, numbers, dot, underscore, and dash: " + version);
        }
    }

    private static void RequireNormalPackageBuilder(string command)
    {
        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            throw new InvalidOperationException(
                command + " must run as a normal user; makepkg and fakeroot must not be driven from a rootful builder.");
        }
    }

    internal static void RequireSeparateDirectories(string first, string second, string label)
    {
        if (SecurityGuards.IsInsideDirectory(first, second) ||
            SecurityGuards.IsInsideDirectory(second, first))
        {
            throw new InvalidOperationException(label + " must be separate directories");
        }
    }

    internal static string RequireManagedBuildPath(string path, string label, params string[] allowedRoots)
    {
        var fullPath = Path.GetFullPath(path);
        var matchingRoot = allowedRoots
            .Select(Path.GetFullPath)
            .FirstOrDefault(root => SecurityGuards.IsInsideDirectory(fullPath, root) &&
                !string.Equals(fullPath, root, StringComparison.Ordinal)) ?? throw new InvalidOperationException(label + " must be a child of a managed .work or artifacts directory: " + fullPath);
        var current = fullPath;
        while (SecurityGuards.IsInsideDirectory(current, matchingRoot))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(label + " must not traverse a symbolic link: " + current);
                }
            }

            if (string.Equals(current, matchingRoot, StringComparison.Ordinal))
            {
                break;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(label + " has no managed parent directory");
        }

        return fullPath;
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(message + ": " + path, path);
        }
    }

    private async Task CreateCleanSourceArchiveAsync(
        string sourceDirectory,
        string sourceTarball,
        string version,
        CancellationToken cancellationToken)
    {
        var listed = await _runner.RunAsync(
            "git",
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
            new CommandRunOptions(WorkingDirectory: _root, StreamError: true),
            cancellationToken);
        _ = listed.EnsureSuccess("could not enumerate clean HomeHarbor source inputs");

        var sourcePaths = SelectCleanSourcePaths(
            _root,
            listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        if (sourcePaths.Count == 0)
        {
            throw new InvalidOperationException("clean HomeHarbor source input set is empty");
        }

        var stageName = "homeharbor-" + version;
        var stage = Path.Combine(sourceDirectory, stageName);
        CopyTrackedTree(_root, stage, sourcePaths);
        await CopyInitializedSubmodulesAsync(stage, cancellationToken);
        await RunRequiredAsync(
            "tar",
            ["-C", sourceDirectory, "-czf", sourceTarball, stageName],
            cancellationToken);
    }

    private static void CopyTrackedTree(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyCollection<string> sourcePaths)
    {
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourcePath in sourcePaths)
        {
            var current = sourcePath;
            while (!string.Equals(current, sourceRoot, StringComparison.Ordinal))
            {
                _ = included.Add(current);
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException("source input has no repository parent: " + sourcePath);
            }
        }

        FileTreeCopier.CopyDirectory(
            sourceRoot,
            destinationRoot,
            path => included.Contains(Path.GetFullPath(path)));
    }

    private async Task CopyInitializedSubmodulesAsync(string stage, CancellationToken cancellationToken)
    {
        var status = await _runner.RunAsync(
            "git",
            ["submodule", "status", "--recursive"],
            new CommandRunOptions(WorkingDirectory: _root, StreamError: true),
            cancellationToken);
        _ = status.EnsureSuccess("could not inspect source submodules");
        var invalidStatus = status.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Length == 0 || line[0] != ' ');
        if (invalidStatus is not null)
        {
            throw new InvalidOperationException(
                "all recursive source submodules must be initialized at their pinned revisions: " +
                invalidStatus.Trim());
        }

        var listedSubmodules = await _runner.RunAsync(
            "git",
            ["submodule", "foreach", "--recursive", "--quiet", "printf '%s\\0' \"$displaypath\""],
            new CommandRunOptions(WorkingDirectory: _root, StreamError: true),
            cancellationToken);
        _ = listedSubmodules.EnsureSuccess("could not enumerate source submodules");
        foreach (var relativePath in listedSubmodules.Stdout
                     .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .Order(StringComparer.Ordinal))
        {
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("git returned an unsafe submodule path: " + relativePath);
            }

            var submoduleRoot = Path.GetFullPath(Path.Combine(_root, relativePath));
            if (!SecurityGuards.IsInsideDirectory(submoduleRoot, _root) || !Directory.Exists(submoduleRoot))
            {
                throw new DirectoryNotFoundException("source submodule is unavailable: " + submoduleRoot);
            }

            var listed = await _runner.RunAsync(
                "git",
                ["ls-files", "--cached", "-z"],
                new CommandRunOptions(WorkingDirectory: submoduleRoot, StreamError: true),
                cancellationToken);
            _ = listed.EnsureSuccess("could not enumerate source submodule " + relativePath);
            var sourcePaths = SelectCleanSourcePaths(
                submoduleRoot,
                listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            if (sourcePaths.Count == 0)
            {
                throw new InvalidOperationException("source submodule input set is empty: " + relativePath);
            }

            CopyTrackedTree(
                submoduleRoot,
                Path.Combine(stage, relativePath),
                sourcePaths);
        }
    }

    internal static IReadOnlyList<string> SelectCleanSourcePaths(string root, IEnumerable<string> relativePaths)
    {
        var repositoryRoot = Path.GetFullPath(root);
        var selinuxRoot = Path.Combine(repositoryRoot, "packaging", "arch", "selinux");
        var result = new List<string>();
        foreach (var relativePath in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("git returned an unsafe source input path: " + relativePath);
            }

            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
            if (!SecurityGuards.IsInsideDirectory(fullPath, repositoryRoot))
            {
                throw new InvalidOperationException("source input escapes the repository: " + relativePath);
            }

            if (SecurityGuards.IsInsideDirectory(fullPath, selinuxRoot) &&
                !ArchPackageSetProvenance.IsMaintainedSource(selinuxRoot, fullPath))
            {
                continue;
            }

            if (!File.Exists(fullPath) &&
                !Directory.Exists(fullPath) &&
                FileTreeCopier.ReadSymbolicLink(fullPath) is null)
            {
                throw new FileNotFoundException("git source input does not exist", fullPath);
            }

            result.Add(fullPath);
        }

        return result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task RequireToolsAsync(IEnumerable<string> tools, CancellationToken cancellationToken)
    {
        foreach (var tool in tools)
        {
            var result = await _runner.RunAsync(
                "sh",
                ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", tool],
                new CommandRunOptions(ThrowOnStartFailure: false),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("missing required tool: " + tool);
            }
        }
    }

    private async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var options = new CommandRunOptions(
            WorkingDirectory: workingDirectory,
            StreamOutput: true,
            StreamError: true,
            EnvironmentOverride: environment);
        if (environment is not null)
        {
            options = RootlessBuildExecutor.IsolatedOptions(options);
        }

        var result = await _runner.RunAsync(
            fileName,
            arguments,
            options,
            cancellationToken);
        _ = result.EnsureSuccess();
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task DeleteMappedBuildPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        var result = await _rootless.RunMappedRootAsync(
            "rm",
            ["-rf", "--", Path.GetFullPath(path)],
            new CommandRunOptions(StreamError: true, Timeout: TimeSpan.FromMinutes(5)),
            cancellationToken);
        _ = result.EnsureSuccess("could not remove mapped package build path " + path);
    }
}
