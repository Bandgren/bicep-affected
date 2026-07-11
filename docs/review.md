# Implementation review record

This document preserves the review conclusions that informed the hardened implementation. Status labels are current: **resolved** means the shipped contract addresses the finding; **current boundary** means an intentional limitation with documented operational handling. None of the entries below is a promise of future work.

## Resolved conclusions

| Review area | Current status |
| --- | --- |
| Deleted/renamed and explicit changed-path handling | **Resolved contract.** Changed-file inputs are validated strictly, preserve raw Git path spelling, and are analyzed conservatively within repository containment rules. |
| Parameter-file impact | **Resolved contract.** `.bicepparam` and conventional parameter-file relationships are part of affected analysis. |
| CI-native output | **Resolved contract.** Text, JSON, and GitHub Actions output are supported. JSON is schema version 1, deterministic, and data-only. |
| Path casing and containment | **Resolved contract.** Path comparison follows the supported platform behavior, while traversal, empty/NUL input, and physical symlink escape are rejected. |
| Metadata failures | **Resolved contract.** Metadata/version problems surface as warnings; warnings fail closed unless explicitly allowed for a shadow rollout. |
| CLI/output regression coverage | **Resolved contract.** The command, exit, configuration, output, and automation contracts are regression-tested. |
| Artifact-name safety | **Resolved contract.** Artifact names are deterministic path-derived names with a SHA-256 suffix, not executable commands. |
| Workflow hardening | **Resolved contract.** Examples use immutable action SHAs, quoted environment boundaries, explicit diagnostics, unprivileged PR validation, and protected publishing. |

## Current boundaries and required response

| Boundary | Required response |
| --- | --- |
| Registry and template-spec references point outside the repository | Treat them as external; validate the consuming repository when its reference changes. |
| The analyzer emits a warning or fails | Use the full-validation fallback. Do not weaken a blocking job with `--allow-warnings`. |
| A new tool version is introduced | Pin it, run a non-blocking shadow comparison, then promote only after comparison with full validation. |
| A regression is discovered after promotion | Roll back to the last known-good pinned tool version and re-enable full validation while investigating. |
| A module is affected without an adjacent configured version-file change | It remains out of `publishMatrix`; decide and make the version change through the normal release process. |

## Output contract conclusions

`affected --format json` and `graph --format json` both declare `schemaVersion: 1`. Node, item, and dependency kinds are strings. CI fields are sorted and data-only, so consumers must not look for or execute a `buildCommand`; no YAML or Azure DevOps matrix contract exists.

The configuration file is an object with only `entrypoints`, `helpers`, `publishableModules`, and `globalImpactFiles`. See the root [strict schema](../bicep-affected.schema.json) and the [README configuration example](../README.md#configuration). Omitted collections select runtime defaults; explicit empty arrays deliberately select no values for that collection.

## Historical note

Earlier review text described missing output features, a future Azure matrix, an executable `buildCommand`, relaxed warning behavior, and clear-text package-token storage. Those conclusions are superseded and must not be used as operating guidance. The current README and workflow/publishing documents are authoritative.