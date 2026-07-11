# GitHub Actions rollout examples

These are secure, copyable patterns for consuming `bicep-affected`. They pin every action to an immutable full commit SHA, keep GitHub expressions out of shell source, pass repository and matrix data through environment variables, quote shell expansions, and never expose Azure credentials to pull-request validation.

The configuration example used by these workflows is validated by the root [configuration schema](../bicep-affected.schema.json). The analysis contract is JSON `schemaVersion: 1`; all graph and item kinds are strings, and its matrix is data—not a command to execute.

## Security rules used below

- PR jobs have only `contents: read`. They validate syntax with `az bicep build`; they do not run `azure/login`, use Azure credentials, or publish.
- A GitHub expression belongs in `env:` (or the workflow data model), not inside `run:`. Shell source consumes `"$VARIABLE"`.
- Any workflow that builds this repository restores with `dotnet restore --locked-mode` before `--no-restore` build/test/pack commands. The consumer-only examples below install a prebuilt tool and therefore do not restore this repository.
- `--allow-warnings` appears only in the named shadow job. Blocking jobs use the default fail-closed warning behavior.
- The publish job is separately triggered, ref-checked, concurrency-serialized, and assigned a protected `production` environment. Put Azure and registry secrets only on that protected environment.
- Diagnostics are written explicitly and uploaded explicitly. The artifacts do not grant publication authority.

Action pins used in every example:

```yaml
# actions/checkout v5
uses: actions/checkout@93cb6efe18208431cddfb8368fd83d5badbf9bfd
# actions/setup-dotnet v5
uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1
# azure/login v3
uses: azure/login@532459ea530d8321f2fb9bb10d1e0bcf23869a43
# actions/github-script v8
uses: actions/github-script@ed597411d8f924073f98dfc5c65a23a2325f34cd
# actions/upload-artifact v5
uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
```

## Pull-request detection and syntax validation

This workflow checks out full history, installs a pinned tool version, detects an affected matrix, and validates matrix paths without Azure credentials. It intentionally captures the GitHub-output diagnostic even when detection fails.

```yaml
name: Validate affected Bicep

on:
  pull_request:
    branches: [master]

permissions:
  contents: read

jobs:
  detect:
    runs-on: ubuntu-latest
    outputs:
      has_affected: ${{ steps.affected.outputs.has_affected }}
      matrix: ${{ steps.affected.outputs.matrix }}
      modules_without_version_change_json: ${{ steps.affected.outputs.modules_without_version_change_json }}
    steps:
      - name: Checkout
        uses: actions/checkout@93cb6efe18208431cddfb8368fd83d5badbf9bfd
        with:
          fetch-depth: 0
          persist-credentials: false

      - name: Set up .NET
        uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1
        with:
          dotnet-version: 10.0.301

      - name: Install detector
        env:
          TOOL_VERSION: 0.1.0-beta.1
        shell: bash
        run: dotnet tool install --global BicepAffected --version "$TOOL_VERSION"

      - name: Detect affected files
        id: affected
        env:
          BASE_REF: ${{ github.base_ref }}
          DIAGNOSTIC_PATH: ${{ runner.temp }}/bicep-affected.github-output
        shell: bash
        run: |
          bicep-affected affected \
            --repo "$GITHUB_WORKSPACE" \
            --from "origin/$BASE_REF" \
            --to "HEAD" \
            --format github > "$DIAGNOSTIC_PATH"

      - name: Upload detector diagnostic
        if: always()
        uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
        with:
          name: bicep-affected-diagnostic
          path: ${{ runner.temp }}/bicep-affected.github-output
          if-no-files-found: warn

  validate:
    needs: detect
    if: needs.detect.outputs.has_affected == 'true'
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix: ${{ fromJSON(needs.detect.outputs.matrix) }}
    steps:
      - name: Checkout
        uses: actions/checkout@93cb6efe18208431cddfb8368fd83d5badbf9bfd
        with:
          persist-credentials: false

      - name: Validate Bicep syntax
        env:
          BICEP_PATH: ${{ matrix.path }}
        shell: bash
        run: az bicep build --file "$BICEP_PATH" --stdout > /dev/null
```

`github` format appends outputs to `GITHUB_OUTPUT` and writes the same key/value output to standard output. Redirecting it above deliberately captures a diagnostic artifact; it does not make the matrix content shell code.

## Deliberate shadow rollout

A shadow job may keep reporting when the detector emits warnings, while the established full-validation job remains authoritative. It must be visibly non-blocking and should have a finite removal plan. This is the only appropriate use of `--allow-warnings`:

```yaml
- name: Shadow compare affected analysis
  continue-on-error: true
  env:
    BASE_REF: ${{ github.base_ref }}
    SHADOW_REPORT: ${{ runner.temp }}/bicep-affected-shadow.json
  shell: bash
  run: |
    bicep-affected affected \
      --repo "$GITHUB_WORKSPACE" \
      --from "origin/$BASE_REF" \
      --to "HEAD" \
      --format json \
      --allow-warnings > "$SHADOW_REPORT"

- name: Upload shadow report
  if: always()
  uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
  with:
    name: bicep-affected-shadow
    path: ${{ runner.temp }}/bicep-affected-shadow.json
    if-no-files-found: warn
```

When detection fails or warns in a blocking rollout, run the existing full validation of every relevant Bicep file. Keep that scheduled or manually dispatchable fallback until affected results have been proven against it.

## Version reminder on trusted pull requests

If a repository chooses to comment on a PR, grant `pull-requests: write` only to the comment job. Do not run this job for untrusted fork code. The workflow passes the detector output through `env`; `github-script` parses data rather than injecting it into JavaScript source.

```yaml
comment-version-reminder:
  if: github.event.pull_request.head.repo.full_name == github.repository
  needs: detect
  runs-on: ubuntu-latest
  permissions:
    contents: read
    pull-requests: write
  steps:
    - name: Comment on version omissions
      uses: actions/github-script@ed597411d8f924073f98dfc5c65a23a2325f34cd
      env:
        MODULES_WITHOUT_VERSION_CHANGE_JSON: ${{ needs.detect.outputs.modules_without_version_change_json }}
      with:
        script: |
          const modules = JSON.parse(process.env.MODULES_WITHOUT_VERSION_CHANGE_JSON || '[]');
          if (modules.length === 0) return;
          const body = modules.map(({ path, versionFile }) => `- ${path}: ${versionFile ?? 'no version file'}`).join('\n');
          await github.rest.issues.createComment({
            owner: context.repo.owner,
            repo: context.repo.repo,
            issue_number: context.issue.number,
            body: `Affected publishable modules without a version-file change:\n${body}`,
          });
```

## Protected publishing

Publishing is a separate push/manual workflow, not a PR job. Manual dispatch requires an explicit trusted base revision to compare with the selected protected ref. Configure the `production` environment to require reviewers and restrict deployment branches. The Azure client ID, tenant ID, subscription ID, and registry name below must be protected environment secrets/variables. The matrix fields are all carried through `env` and quoted before use.

```yaml
name: Publish affected Bicep modules

on:
  workflow_dispatch:
    inputs:
      base_ref:
        description: Trusted base commit or ref for affected analysis
        required: true
        type: string
  push:
    branches: [master]

permissions:
  contents: read
  id-token: write

concurrency:
  group: publish-bicep-modules
  cancel-in-progress: false

jobs:
  detect:
    if: github.ref_protected
    runs-on: ubuntu-latest
    outputs:
      has_publish_modules: ${{ steps.affected.outputs.has_publish_modules }}
      publish_matrix: ${{ steps.affected.outputs.publish_matrix }}
    steps:
      - name: Checkout
        uses: actions/checkout@93cb6efe18208431cddfb8368fd83d5badbf9bfd
        with:
          fetch-depth: 0
          persist-credentials: false
      - name: Set up .NET
        uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1
        with:
          dotnet-version: 10.0.301
      - name: Install detector
        env:
          TOOL_VERSION: 0.1.0-beta.1
        shell: bash
        run: dotnet tool install --global BicepAffected --version "$TOOL_VERSION"
      - name: Detect modules to publish
        id: affected
        env:
          BEFORE_SHA: ${{ inputs.base_ref || github.event.before }}
          AFTER_SHA: ${{ github.sha }}
          DIAGNOSTIC_PATH: ${{ runner.temp }}/publish-detection.github-output
        shell: bash
        run: |
          bicep-affected affected \
            --repo "$GITHUB_WORKSPACE" \
            --from "$BEFORE_SHA" \
            --to "$AFTER_SHA" \
            --format github \
            --include modules > "$DIAGNOSTIC_PATH"
      - name: Upload publishing diagnostic
        if: always()
        uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
        with:
          name: bicep-publish-detection
          path: ${{ runner.temp }}/publish-detection.github-output
          if-no-files-found: warn

  publish:
    needs: detect
    if: needs.detect.outputs.has_publish_modules == 'true'
    runs-on: ubuntu-latest
    environment: production
    strategy:
      fail-fast: false
      matrix: ${{ fromJSON(needs.detect.outputs.publish_matrix) }}
    steps:
      - name: Checkout
        uses: actions/checkout@93cb6efe18208431cddfb8368fd83d5badbf9bfd
        with:
          persist-credentials: false
      - name: Azure login
        uses: azure/login@532459ea530d8321f2fb9bb10d1e0bcf23869a43
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
      - name: Publish module
        env:
          BICEP_PATH: ${{ matrix.path }}
          VERSION_TAG: ${{ matrix.versionTag }}
          MODULE_DIRECTORY: ${{ matrix.directory }}
          MODULE_FILE_NAME: ${{ matrix.fileName }}
          REGISTRY: ${{ vars.BICEP_REGISTRY }}
        shell: bash
        run: |
          test -n "$VERSION_TAG"
          module="bicep/modules/$MODULE_DIRECTORY/$MODULE_FILE_NAME:$VERSION_TAG"
          target="br:$REGISTRY/${module,,}"
          az bicep publish --file "$BICEP_PATH" --target "$target"
```

The sample does not contain a `buildCommand`, Azure DevOps matrix, or unquoted matrix interpolation. Consumers decide their own build/publish command from the data fields and their trusted workflow policy.