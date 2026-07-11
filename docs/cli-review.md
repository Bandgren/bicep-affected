# CLI and implementation review

This review records the current beta.3 operational contract. Earlier beta.2 conclusions described below as superseded are historical only and must not guide consumers.

## Current supported contract

- `affected` selects actionable paths; text output is exactly one selected path per line.
- `explain` uses the same selection and reports changed files, selected targets, reasons, and complete dependency chains.
- `graph` reports topology and has no changed-file or action-target selection input.
- `affected` and `explain` accept exactly one changed-file mode: repeatable `--changed-file`, line-oriented `--changed-files-stdin`, or a complete `--from`/`--to` Git range.
- `--target build|deploy|publish` selects action class, defaulting to `deploy`. Build selects entrypoints and affected modules; deploy selects entrypoints; publish selects only version-gated affected modules.
- Affected JSON is data-only `schemaVersion: 3`: `target`, `hasTargets`, `targetCount`, `changedFiles`, `targets`, and `warnings`. Consumers select from `.targets`, after validating schema version and target.
- Graph JSON remains `schemaVersion: 1`, with deterministic nodes, edges, warnings, and parse-diagnostic files.
- Warnings fail closed with exit 1 before affected/explain payload emission unless a deliberately non-blocking caller supplies `--allow-warnings`. Warnings remain visible on stderr.
- `--output` writes rendered output only to the requested file; it does not duplicate a rendered payload to stdout.
- Unknown options, incomplete ranges, invalid target names, and invalid adjacent version-file names are errors. Configuration and repository paths remain strict and contained.

## Output and safety boundaries

The tool returns paths and metadata, never deployment/build/publish commands. A consuming workflow owns the trusted allowlist and mapping from a selected path to scope, parameters, credentials, registry, and executable command. A warning signals that graph completeness is uncertain; blocking validation, deployment, and publishing must use the default fail-closed behavior.

An empty textual `affected` result contains no lines. The JSON result remains explicit: `hasTargets` is `false`, `targetCount` is `0`, and `targets` is `[]`. `--fail-if-affected` exits 2 when selected targets exist; `--fail-if-none` exits 3 when none exist, except that unallowed warnings take precedence.

## Superseded beta.2 assumptions

The following are explicitly superseded by beta.3: category-specific output arrays as an automation contract, affected JSON schema version 2, explanatory default `affected` text, the old selector option, and stdout duplication when using `--output`. Current consumers must use beta.3 `--target`, `explain`, and `.targets` instead.

## Review outcome

Use affected selection as a production gate only with a pinned tool version, schema/target validation, an allowlisted consumer-side mapping, and a retained full-validation fallback. A shadow comparison may opt into `--allow-warnings`; normal validation and publishing must not.
