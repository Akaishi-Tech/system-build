# system-build

Manifest-driven build tooling for Arch Linux appliances with seamless A/B system updates. This repository was extracted from HomeHarbor so the image, kernel, OTA, verified-boot, and installer pipeline can evolve as an independently reusable tool.

The first extracted release preserves the existing `HomeHarbor.ImageBuilder` CLI and `HomeHarbor.Tooling` build namespaces for source compatibility. The packed .NET tool command is `arch-system-build`.

## Capabilities

- Plan and build deterministic system images from YAML manifests.
- Build kernel/module/firmware payloads and recovery images.
- Produce signed system and kernel OTA bundles with anti-rollback release sequences.
- Assemble full and network installer ISO artifacts.
- Build and validate Arch and SELinux package sets in rootless build environments.
- Consume A/B boot and OTA primitives from the pinned `external/system-utils` submodule.

## Checkout

Clone recursively:

```sh
git clone --recurse-submodules https://github.com/Akaishi-Tech/system-build.git
cd system-build
```

## Build and test

```sh
dotnet restore system-build.slnx
dotnet build system-build.slnx --no-restore
dotnet test tests/HomeHarbor.SystemBuild.Tests/HomeHarbor.SystemBuild.Tests.csproj --no-build
```

The CLI operates on a consumer repository. For HomeHarbor:

```sh
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- \
  system-plan /path/to/home-harbor/system/x86_64/system/manifest.yml 0.1.0-dev /path/to/home-harbor
```

The consumer is expected to pin this repository at `tools/system-build` recursively. `ARCH_AB_SYSTEM_UTILS_ROOT` can override system-utils asset discovery for nonstandard layouts.

## License

GPL-3.0-only. See `LICENSE`.
