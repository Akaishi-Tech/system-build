# Repository Guidelines

This repository owns the reusable Arch appliance build engine extracted from HomeHarbor.

- Keep product policy and manifests in consumer repositories; keep reusable image, release, signing, archive, and build orchestration here.
- Prefer C# for manifests, cryptography, path safety, release guards, and build decisions. Shell is only thin external-tool glue.
- `external/system-utils` is the pinned source of A/B boot, OTA, and runtime utility primitives. Update it deliberately and recursively.
- Add MSTest unit coverage for build-engine behavior changes. Destructive, installer, or full-system validation must run only in disposable VMs.
- Do not weaken Secure Boot, AVB, OTA anti-rollback, source provenance, archive, or path checks.

## Working on a change

- Read the relevant code, tests, and local instructions before editing. Define completion from the user's request and keep unrelated work out of the patch.
- Carry authorized work through implementation and verification. Make reasonable assumptions for routine, reversible decisions and state assumptions that affect the result. Ask when missing information changes correctness or scope; continue independent work while waiting.
- Before requesting approval for an action that needs it, prepare the parts already authorized so the user can review a concrete result. Preserve existing user changes.
- Interpret optional skill guidance in light of the user's request and existing authorization. If an instruction blocks progress, link the exact file, quote the relevant rule, and explain the conflict. The security and disposable-VM requirements above still apply.
- Delegate independent investigation, implementation, or review when parallel work saves time or improves quality. Give each agent a clear scope, avoid overlapping edits, and review its findings before integrating them.
- Share concise updates when a finding, decision, or blocker affects the work. Finish with the change, verification results, and any remaining limitation; distinguish observed results from expectations.

## Verification

- For behavior changes, add MSTest coverage of the changed behavior and relevant failure cases. Use existing test patterns under `tests/HomeHarbor.SystemBuild.Tests`.
- Run the relevant tests and required checks. The build and test commands in [README.md](README.md#build-and-test) follow [CI](.github/workflows/ci.yml), including treating build warnings as errors.
- For documentation-only changes, check accuracy, referenced paths, command syntax, and `git diff --check`. Add tests when behavior changes, not to assert prose or mirror implementation details.
- Inspect the final diff. Expand or repeat verification only when new edits, failures, or unresolved concerns justify it. Report any check that was not run and why.
- When updating `external/system-utils`, inspect the pinned revision and recursive submodules, review upstream changes, and verify affected consumers. Do not advance the pin as incidental cleanup.

## Technical writing

Apply these rules to documentation, comments, change descriptions, and user-facing updates.

- Start with the concrete result or instruction. Give the file, command, behavior, or evidence that makes a claim useful.
- Prefer short paragraphs and direct verbs. Use lists for steps or parallel items and tables for comparisons; let the content determine their size.
- Remove praise, filler openings, repeated conclusions, and stock contrasts such as "not just X, but Y". Replace promotional claims such as "seamless" or "robust" with a supported description.
- State uncertainty precisely. Never invent measurements, test results, citations, or personal experience to make text sound convincing.
- Keep exact technical terms, identifiers, commands, and security qualifications. Writing guidance is an editing aid, not a mechanical word blacklist.
- Put prerequisites and a usable command near the start of a README. Explain reasons and constraints in comments rather than restating the code.

These conventions adapt [OpenAI's GPT-6 Astra prompting guidance](https://developers.openai.com/api/docs/guides/latest-model#gpt-6-astra-prompting-best-practices) and [NousResearch's ANTI-SLOP writing reference](https://github.com/NousResearch/autonovel/blob/master/ANTI-SLOP.md) for this build engine. They do not require a particular model or writing detector.
