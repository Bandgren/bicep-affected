# Implementation review record

This document states the shipped beta.3 contract and the operating response to its intentional boundaries. References to beta.2 behavior are explicitly marked superseded rather than retained as current guidance.

## Resolved conclusions

| Review area | Current status |
| --- | --- |
| Changed-file input | Exactly one supported input mode is validated; raw Git path spelling is preserved and paths are constrained to the repository. |
| Parameter and content dependencies | Supported Bicep parameter relationships and literal content-load reverse dependencies participate in affected analysis. |
| Actionable selection | `--target build|deploy|publish` selects a deterministic `.targets` list; default target is `deploy`. |
| Causality | `explain` shows changed files, selected targets, reason messages, and complete dependency chains. |
| Machine contract | Affected JSON uses data-only `schemaVersion: 3` with `target`, `hasTargets`, `targetCount`, `changedFiles`, `targets`, and `warnings`. |
| Graph contract | Graph JSON remains data-only `schemaVersion: 1`; it is topology, not action selection. |
| Warning policy | Warnings are visible on stderr and fail closed before affected/explain payload emission unless explicitly allowed for a non-blocking shadow comparison. |
| Output-file policy | `--output` writes only the rendered output file; it does not duplicate rendered output to stdout. |
| Workflow hardening | Consumer examples pin tools/actions, quote data boundaries, retain trusted mappings, keep PRs unprivileged, and protect publishing environments. |

## Current boundaries and required response

| Boundary | Required response |
| --- | --- |
| Registry/template-spec dependency points outside the repository | Treat it as external and validate the consuming repository when its reference changes. |
| Detector warns or fails | Run the retained full-validation fallback; do not weaken blocking work with `--allow-warnings`. |
| A selected path must be built, deployed, or published | Validate schema/target first, then map the path using a reviewed repository-owned allowlist and command policy. |
| Publish target is empty | Do not publish; version metadata must change and be readable for a module to be selected. |
| Tool upgrade | Pin it, compare in a non-blocking shadow run, then promote only after comparison with full validation. |
| Regression after promotion | Roll back to the last known-good pin and re-enable full validation while investigating. |

## Superseded beta.2 record

Beta.2 affected JSON schema version 2, category arrays as an action contract, explanatory default affected text, the old include selector, and stdout duplication with `--output` are superseded. They must not be used in production automation. Current workflows validate beta.3 and iterate `.targets`.

## Review outcome

Affected analysis is a production gate only when warnings remain fail closed, output is interpreted as data rather than executable policy, and a complete validation fallback remains available.
