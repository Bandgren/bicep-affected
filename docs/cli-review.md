# CLI and implementation review

This is a historical review record updated after the production-hardening work. It distinguishes conclusions that are now resolved from current operational boundaries; it is not a backlog or a proposal for unimplemented work.

## Current conclusion

The supported contract is intentionally narrow and strict:

- `affected` accepts exactly one changed-file mode: repeatable explicit paths, line-oriented stdin, or a complete `--from`/`--to` Git range.
- `graph` does not accept affected-only input, include, publish, or fail-policy flags; it does accept `--allow-warnings` so graph-extraction warnings can be handled with the same explicit policy.
- Unknown options, invalid formats, invalid includes, incomplete ranges, and invalid adjacent version-file names are errors.
- Analysis warnings fail closed with exit 1 unless a caller deliberately supplies `--allow-warnings`; warnings remain visible.
- JSON and graph JSON use `schemaVersion: 2`, string kinds, deterministic ordering, and data-only fields. There is no `buildCommand` in the current payload.
- Configuration is strict: unknown members, non-array collections, and null collection entries are rejected; omitted versus explicitly empty arrays retain their documented distinct meanings.
- Repository/config/dependency paths are constrained to the repository and checked against traversal and symlink escape.

The [README](../README.md) is the user-facing specification; the root [JSON Schema](../bicep-affected.schema.json) is the strict configuration contract.

## Resolved historical findings

| Historical concern | Current status |
| --- | --- |
| Changed-file modes could drift or be combined accidentally | **Resolved.** Exactly-one input-mode validation is enforced. |
| Git paths were rewritten or unsafe repository reads were possible | **Resolved.** Raw Git path spelling is preserved and repository containment/symlink checks are enforced. |
| Warnings could silently permit a CI pass | **Resolved.** Warnings fail closed by default; `--allow-warnings` is explicit. |
| CLI/output behavior lacked a stable machine contract | **Resolved.** Schema version 2, string kinds, deterministic fields, and regression coverage define the JSON contract. |
| Generic payloads contained executable build instructions | **Resolved.** Output is data-only; consumers apply their own trusted workflow policy. |
| YAML/Azure DevOps output was implied | **Resolved.** The supported formats are text and JSON. No provider-specific matrix, Azure matrix, or YAML output contract is claimed. |

## Current operational boundaries

- Local registry/template-spec dependencies are external references; they are not resolved back to source files in a different repository.
- Analysis models the supported local Bicep syntax and literal content-load paths. A warning is a safety signal, not a result to ignore in a blocking workflow.
- A changed publishable module appears in `publishableModulesToPublish` only when an adjacent configured publish-version file changed and valid version metadata can be read.
- `--output` writes the rendered text or JSON payload to a file for both `affected` and `graph`.

## Review outcome

Use affected analysis as a production gate only with the documented warning policy and a retained full-validation fallback. Shadow comparisons may use `--allow-warnings` temporarily; normal validation and publishing must remain fail closed.