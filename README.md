# bicep-affected

`bicep-affected` turns a changed-file set into a deterministic, actionable Bicep selection. It never emits deployment, build, or publish commands: the consuming repository maps the returned paths to its own trusted policy.

## Install beta.3

The current release is [0.1.0-beta.3](https://github.com/Bandgren/bicep-affected/releases/tag/v0.1.0-beta.3). It is distributed as a GitHub release package asset and is **not yet indexed on NuGet.org**.

1. Download `BicepAffected.0.1.0-beta.3.nupkg` into `./downloaded-packages`.
2. Install the pinned prerelease from that explicit source:

```bash
dotnet tool install --global BicepAffected \
  --version 0.1.0-beta.3 \
  --add-source ./downloaded-packages
bicep-affected --help
```

For a repository-local installation, use `dotnet new tool-manifest` and omit `--global`. The shorter NuGet.org installation command is unavailable until NuGet.org has indexed the package; see [public publishing](docs/public-publishing.md).

## Actionable output

```text
bicep-affected affected [options]  # one selected path per line, or JSON
bicep-affected explain [options]   # selected targets with reasons and chains
bicep-affected graph [options]     # repository dependency topology
```

`affected` and `explain` require exactly one changed-file mode: repeat `--changed-file <path>`, pipe line-oriented input to `--changed-files-stdin`, or pass both `--from <git-ref>` and `--to <git-ref>`. Paths are repository-relative. `graph` does not take changed-file or target-selection options.

Use `--target build|deploy|publish` to select an action class; the default is `deploy`:

| Target | `targets` contains |
| --- | --- |
| `deploy` (default) | affected deployment entrypoints |
| `build` | affected deployment entrypoints and publishable modules |
| `publish` | affected publishable modules with changed, readable configured version metadata |

### APIM policy change: affected paths and causality

If `deployments/employees/main.bicep` loads `../../apis/employees/policy.xml`, an APIM policy edit selects that deployment entrypoint:

```bash
bicep-affected affected \
  --repo . \
  --changed-file apis/employees/policy.xml
```

The default text output is deliberately machine-friendly—one path per line:

```text
deployments/employees/main.bicep
```

Use `explain` when a reviewer needs causality rather than a selection list:

```bash
bicep-affected explain \
  --repo . \
  --changed-file apis/employees/policy.xml \
  --target deploy
```

```text
Changed files:
  apis/employees/policy.xml

Selected deploy targets:
  deployments/employees/main.bicep
    reason: Affected through content-load dependency. caused by apis/employees/policy.xml
    chain: apis/employees/policy.xml -> deployments/employees/main.bicep
```

The exact reason wording depends on the discovered dependency; the selected path, reason, and complete reverse-dependency chain identify why the trusted deploy script received that path.

### JSON deployment, build, and publish selection

Use JSON when a trusted program needs selection metadata. The affected payload has `schemaVersion: 3` and one canonical `targets` array.

```bash
# Deployment selection (the default target is deploy)
bicep-affected affected --repo . \
  --changed-file apis/employees/policy.xml \
  --format json --target deploy --output deploy.json

jq -e '.schemaVersion == 3 and .target == "deploy" and (.targets | type == "array")' deploy.json >/dev/null
jq -r '.targets[].path' deploy.json
```

```bash
# Build selection: entrypoints plus affected publishable modules
bicep-affected affected --repo . --from origin/master --to HEAD \
  --format json --target build --output build.json
jq -r '.targets[].path' build.json
```

```bash
# Publish selection: only version-gated affected publishable modules
bicep-affected affected --repo . --from origin/master --to HEAD \
  --target publish --format json --output publish.json
jq -r '.targets[] | select(.versionTag != null) | .path' publish.json
```

A selected item includes `path`, `kind`, `directory`, `fileName`, `artifactName`, `versionFile`, `version`, `versionTag`, `hasVersionChange`, and `reasons`. Publish consumers must apply their own allowlisted registry, module-name, environment, and credential mapping; output data is not executable authority.

The payload fields are:

```text
schemaVersion, target, hasTargets, targetCount,
changedFiles, targets, warnings
```

For an empty selection, text output is empty (no lines). JSON still provides an explicit result, for example `"hasTargets": false`, `"targetCount": 0`, and `"targets": []`. `--fail-if-none` can make that selection exit with code 3.

### Output files and warnings

`--output <path>` writes the rendered payload **only to that file**; it does not duplicate normal rendered output to stdout. Warnings always go to stderr. For `affected` and `explain`, warnings fail closed before any payload is rendered or written unless `--allow-warnings` is explicitly supplied. Reserve `--allow-warnings` for a visibly non-blocking shadow comparison, not deployment or publishing.

```bash
bicep-affected affected --repo . --from origin/master --to HEAD \
  --target deploy --format json --output affected.json
# Read the file; stdout carries no rendered payload when --output is set.
jq -r '.targets[].path' affected.json
```

## Options and exit policy

```text
--repo <path>                         Repository root (default: current directory)
--config <path>                       Optional bicep-affected.json path
--format text|json                    Affected/graph format (default: text)
--target build|deploy|publish          Action target (default: deploy)
--output <path>                       Write rendered output only to a file
--publish-version-file <file>         Repeatable adjacent filename for publish gating
--fail-if-affected                    Exit 2 when selected targets exist
--fail-if-none                        Exit 3 when no selected targets exist
--allow-warnings                      Explicitly permit warnings
```

`--publish-version-file` is a nonempty basename only. Exit 1 covers invalid input, analysis errors, and warnings not explicitly allowed; exit 2 is `--fail-if-affected` with selected targets; exit 3 is `--fail-if-none` with no selected targets. Warnings take precedence over exits 2 and 3.

## JSON graph contract

`graph --format json` remains independent from action selection and uses `schemaVersion: 1`, with sorted `nodes`, `edges`, `warnings`, and `parseDiagnosticFiles`. Graph node and edge kinds are strings. Use it to inspect topology; do not treat it as an affected-action payload.

## Configuration

Configuration is optional. The strict [configuration schema](bicep-affected.schema.json) documents `entrypoints`, `helpers`, `publishableModules`, and `globalImpactFiles`. Do not add `$schema` to the runtime config: unknown properties are rejected. Omitted collections select defaults; explicit empty arrays select no values for that collection.

```json
{
  "entrypoints": ["deployments/**/*.bicep"],
  "helpers": ["infra/shared/**/*.bicep"],
  "globalImpactFiles": ["bicepconfig.json"],
  "publishableModules": [{ "path": "modules/**/*.bicep", "metadata": "metadata.json" }]
}
```

Repository, config, changed, and dependency paths are constrained to the repository; traversal, NUL-containing paths, and symlink escape are rejected.

## beta.2 → beta.3 migration

beta.3 is a clean breaking migration. Update every consumer at once; do not preserve compatibility parsing for beta.2 output.

| beta.2 (superseded) | beta.3 |
| --- | --- |
| `--include all|entrypoints|modules|helpers` | `--target build|deploy|publish` (default `deploy`) |
| Default affected text was explanatory | `affected` emits one selected path per line; use `explain` for reasons and chains |
| Affected JSON `schemaVersion: 2` | Affected JSON `schemaVersion: 3` |
| Category arrays (`entrypoints`, `publishableModulesToPublish`, and similar) were the action contract | `.targets` is the sole action-selection array; validate `target` and schema version |
| `--output` also duplicated rendered output on stdout | `--output` writes only the specified file; warnings remain stderr |

Historical beta.2 references in review material are explicitly superseded and are not operational guidance.

## Safe rollout and workflows

Start with a non-blocking shadow comparison, retain a scheduled or manual full-validation fallback, and make affected selection blocking only after it agrees with full validation. A blocking job must fail closed on warnings. See [GitHub Actions examples](docs/github-actions-poc.md) for protected environments, trusted path-to-deployment mapping, quoted JSON parsing, and full-validation fallback.

See [CLI review](docs/cli-review.md), [implementation review](docs/review.md), and [public publishing](docs/public-publishing.md) for the corresponding current policy.

## License

Licensed under the [MIT License](LICENSE).
