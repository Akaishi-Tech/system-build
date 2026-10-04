# system-build

Build Arch Linux appliance images, signed OTA bundles, and installer ISOs from YAML manifests.

Requires a .NET 10 SDK compatible with [global.json](global.json) (10.0.109, with `latestFeature` roll-forward) and a recursive checkout:

```sh
git clone --recurse-submodules https://github.com/Akaishi-Tech/system-build.git
cd system-build
```

## Build and test

```sh
dotnet restore system-build.slnx
dotnet build system-build.slnx --no-restore -p:TreatWarningsAsErrors=true
dotnet test tests/HomeHarbor.SystemBuild.Tests/HomeHarbor.SystemBuild.Tests.csproj --no-build
```

These commands compile the tooling and run unit tests. Destructive, installer, and full-system validation must run in disposable VMs.

## Plan a system image

The CLI reads manifests from a consumer repository. This HomeHarbor example prints a build plan; replace `/path/to/home-harbor` with the consumer checkout:

```sh
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- \
  system-plan /path/to/home-harbor/system/x86_64/system/manifest.yml 0.1.0-dev /path/to/home-harbor
```

The consumer is expected to pin this repository at `tools/system-build` recursively. `ARCH_AB_SYSTEM_UTILS_ROOT` can override system-utils asset discovery for nonstandard layouts.

## Scope

- Plan and build system images, kernel/module/firmware payloads, and recovery images.
- Produce signed system and kernel OTA bundles with anti-rollback release sequences, plus full and network installer ISOs.
- Build and validate Arch and SELinux package sets in rootless build environments.

Product policy and manifests belong in consumer repositories. A/B boot, OTA, and runtime utilities come from the pinned `external/system-utils` submodule.

Extracted from HomeHarbor, this repository retains the `HomeHarbor.ImageBuilder` CLI project and `HomeHarbor.Tooling` build namespaces for source compatibility. The packed .NET tool command is `arch-system-build`.

See [AGENTS.md](AGENTS.md) for contribution, verification, and writing conventions.

## License

GPL-3.0-only. See `LICENSE`.
