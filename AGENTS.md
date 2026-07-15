# Repository Guidelines

This repository owns the reusable Arch appliance build engine extracted from HomeHarbor.

- Keep product policy and manifests in consumer repositories; keep reusable image, release, signing, archive, and build orchestration here.
- Prefer C# for manifests, cryptography, path safety, release guards, and build decisions. Shell is only thin external-tool glue.
- `external/system-utils` is the pinned source of A/B boot, OTA, and runtime utility primitives. Update it deliberately and recursively.
- Add MSTest unit coverage for build-engine behavior changes. Destructive, installer, or full-system validation must run only in disposable VMs.
- Do not weaken Secure Boot, AVB, OTA anti-rollback, source provenance, archive, or path checks.
