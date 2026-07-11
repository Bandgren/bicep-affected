# bicep-affected

`bicep-affected` determines which Bicep deployment entrypoints, publishable modules, and helpers are affected by a changed-file set. By default it emits a human-readable text report; it can also emit a deterministic JSON contract.

## Install

The beta is currently distributed as a public package asset on the [0.1.0-beta.2 GitHub release](https://github.com/Bandgren/bicep-affected/releases/tag/v0.1.0-beta.2); it is **not yet indexed on NuGet.org**.

1. Download `BicepAffected.0.1.0-beta.2.nupkg` into a local directory such as `./downloaded-packages`.
2. Install it from that directory:

```bash
dotnet tool install --global BicepAffected \
  --version 0.1.0-beta.2 \
  --add-source ./downloaded-packages
bicep-affected --help
```

The shorter `dotnet tool install --global BicepAffected --version 0.1.0-beta.2` command will work only after the package is published and indexed on NuGet.org. For repository-local installation and publishing status, see [public publishing](docs/public-publishing.md).

To build from source, use the SDK pinned by `global.json` and the committed dependency locks:

```bash
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore
dotnet pack src/BicepAffected.Cli/BicepAffected.Cli.csproj --configuration Release --no-build --no-restore
```

## Commands and changed-file input

```text
bicep-affected affected [options]
bicep-affected graph [options]
```

`affected` requires **exactly one** changed-file input mode:

1. One or more `--changed-file <path>` options. This option is repeatable.
2. `--changed-files-stdin`, with one path per input line. Empty lines are ignored.
3. Both `--from <git-ref>` and `--to <git-ref>`, which obtains paths from Git.

The modes cannot be combined; supplying only one of `--from` and `--to` is an error. Explicit and stdin inputs are de-duplicated and sorted without rewriting the supplied Git path spelling. Use paths relative to the repository root. For example:

```bash
bicep-affected affected --repo . --changed-file 'infra/shared/types.bicep'
printf '%s\n' 'infra/shared/types.bicep' | bicep-affected affected --repo . --changed-files-stdin
bicep-affected affected --repo . --from origin/master --to HEAD
```

`graph` has no changed-file input. It accepts `--repo`, `--config`, `--format text|json`, and `--allow-warnings`; affected-only options (including `--include`, `--publish-version-file`, and fail policies) are rejected.

Common `affected` options are:

```text
--repo <path>                         Repository root (default: current directory)
--config <path>                       Config file (default lookup: bicep-affected.json)
--format text|json                    Output format (default: text)
--include all|entrypoints|modules|helpers
--publish-version-file <file>         Repeatable simple adjacent filename
--output <path>                       Write rendered output to a file
--fail-if-affected                    Exit 2 if an affected item exists
--fail-if-none                        Exit 3 if no affected item exists
--allow-warnings                      Opt out of the default warning failure
```

`--publish-version-file` must be a nonempty basename: no directory separator, absolute path, `.` or `..`. Use `--output` to write the rendered payload to a file:

```bash
bicep-affected affected --repo . --from origin/master --to HEAD --format json --output affected.json
```

### Default text output

Without `--format`, the report is human-readable text. For the monorepo fixture, changing `apis/employees/openapi.yaml` affects `deployments/employees/main.bicep` through a content-load reverse dependency:

```text
Changed files:
  apis/employees/openapi.yaml

Affected entrypoints:
  deployments/employees/main.bicep
    reason: Affected through content-load dependency. caused by apis/employees/openapi.yaml
    chain: apis/employees/openapi.yaml -> deployments/employees/main.bicep
```

Use `--format json` only when another trusted program needs to consume the result.

## Exit and warning policy

| Exit | Meaning |
| --- | --- |
| `0` | Successful analysis with no warnings, or warnings explicitly permitted by `--allow-warnings` |
| `1` | Invalid command/options, runtime/analysis error, or any warning without `--allow-warnings` |
| `2` | `--fail-if-affected` and at least one affected item |
| `3` | `--fail-if-none` and no affected items |

Warnings are printed to standard error and are **fail-closed by default**. They take precedence over exits 2 and 3. `--allow-warnings` does not hide warnings or convert errors to success; reserve it for a deliberate, non-blocking shadow comparison during rollout.

## Configuration

Configuration is optional. With no configuration file, the defaults are empty `entrypoints` and `helpers`, one publishable-module rule (`**/*.bicep` with adjacent `metadata.json`), and `**/bicepconfig.json` as a global-impact pattern.

Use the strict [configuration schema](bicep-affected.schema.json) in external editor/CI validation. Do not add a `$schema` member to the runtime config: the loader rejects unknown properties. It accepts JSON comments and trailing commas, property names are case-insensitive, and each configured collection must be an array with no `null` entries.

```json
{
  "entrypoints": [
    "deployments/**/*.bicep",
    "components/*/infrastructure/*.bicep"
  ],
  "helpers": ["infra/shared/**/*.bicep"],
  "globalImpactFiles": ["bicepconfig.json"],
  "publishableModules": [
    {
      "path": "modules/**/*.bicep",
      "metadata": "metadata.json"
    }
  ]
}
```

All four properties are optional. **Omitting** a property selects its default; an **explicit empty array** selects no values for that property. In particular, omit `publishableModules` to retain the default publishable-module rule, or set `"publishableModules": []` to disable module classification. A rule may omit `path` or `metadata`, which respectively default to `**/*.bicep` and `metadata.json`.

Repository paths are constrained defensively. The repository root must exist; a config path must resolve inside it; changed and dependency paths cannot be empty, NUL-containing, traversing outside the root, or escape through a symlink. Git paths are retained as supplied rather than normalized into a different spelling in output.

## JSON contracts

Both JSON formats use `schemaVersion: 2`. JSON is indented, camel-cased, deterministic, and data-only: it contains no shell command such as `buildCommand`.

`affected --format json` contains `hasAffected`, `hasPublishableModulesToPublish`, `counts`, sorted `changedFiles`, the canonical affected arrays (`entrypoints`, `publishableModules`, `publishableModulesToPublish`, `publishableModulesWithoutVersionChange`, and `helpers`), and sorted `warnings`. An affected item has the data fields:

```text
path, kind, directory, fileName, artifactName,
versionFile, version, versionTag, hasVersionChange, reasons
```

Kinds are strings, not numeric enums. Affected-item kinds are `entrypoint`, `publishableModule`, or `helper`; graph node kinds additionally include `unknownBicepFile`, `contentFile`, and `configFile`; edge kinds are `localModule`, `compileTimeImport`, `contentLoad`, `directoryContent`, `parameterFile`, `globalConfig`, or `externalModule`. `artifactName` is stable and filesystem-oriented: a lower-case path stem with non-alphanumeric characters changed to `-`, followed by `-`, the first 12 lower-case hex characters of the SHA-256 of the normalized path, and `-bicep`. Do not reconstruct or use an artifact name as a command.

`graph --format json` has `schemaVersion: 2`, sorted `nodes`, sorted `edges`, sorted `warnings`, and sorted `parseDiagnosticFiles`. Node and edge kinds are likewise strings; nullable graph fields remain JSON `null` when absent.

## CI rollout, fallback, and recovery

Adopt affected validation in stages:

1. Run the new detector beside the existing full validation and use `--allow-warnings` only in that explicitly named shadow job.
2. Compare the detector’s JSON with the full-validation result and fix configuration or graph gaps.
3. Make the affected job blocking only after the comparison is reliable; keep warnings fail-closed.
4. Retain a manual or scheduled **full-validation fallback** that validates every relevant Bicep file, and use it immediately when the detector fails, reports warnings, or its result is questionable.

To upgrade, pin a tested tool version in each workflow, run shadow validation, then promote that version. To roll back, replace the pinned tool version with the last known-good version and enable the full-validation fallback; do not suppress detector warnings to keep a broken rollout green.

### Troubleshooting

- **Exit 1 with `warning:` output:** investigate the warning or run the documented full-validation fallback. Do not add `--allow-warnings` to a blocking job.
- **No changes detected:** ensure exactly one input mode is used, fetch the compared refs, and use a repository-relative path.
- **Config rejected:** validate against [bicep-affected.schema.json](bicep-affected.schema.json), remove unknown keys/nulls, and ensure the config path stays inside the repository.
- **Path escape error:** remove `..`, absolute paths, or a symlink that resolves outside the repository.
- **Unexpected publish skip:** inspect `publishableModulesWithoutVersionChange`; only modules with a changed adjacent configured version file appear in `publishableModulesToPublish`.

## Workflow and publishing examples

The secure GitHub Actions examples—including SHA-pinned actions, environment/quoted shell boundaries, diagnostic uploads, unprivileged PR validation, and protected publishing—are in [docs/github-actions-poc.md](docs/github-actions-poc.md). Public NuGet installation and OIDC trusted publishing are in [docs/public-publishing.md](docs/public-publishing.md).

The implementation and CLI review records are maintained in [docs/cli-review.md](docs/cli-review.md) and [docs/review.md](docs/review.md). Their historical findings are labeled by current resolution status.

## License

Licensed under the [MIT License](LICENSE).