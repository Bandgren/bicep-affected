# GitHub Actions rollout examples

These are secure, copyable patterns for consuming `bicep-affected`. They pin every action to an immutable full commit SHA, keep GitHub expressions out of shell source, pass values through environment variables, quote shell expansions, and never expose Azure credentials to pull-request validation.

The configuration example used by these workflows is validated by the root [configuration schema](../bicep-affected.schema.json). The analysis contract is JSON `schemaVersion: 2`; all graph and item kinds are strings, and the affected arrays are data—not commands to execute.

## Security rules used below

- PR jobs have only `contents: read`. They validate syntax with `az bicep build`; they do not run `azure/login`, use Azure credentials, or publish.
- A GitHub expression belongs in `env:` (or the workflow data model), not inside `run:`. Shell source consumes `"$VARIABLE"`.
- Any workflow that builds this repository restores with `dotnet restore --locked-mode` before `--no-restore` build/test/pack commands. The consumer-only examples below install a prebuilt tool and therefore do not restore this repository.
- `--allow-warnings` appears only in the named shadow job. Blocking jobs use the default fail-closed warning behavior.
- The publish job is separately triggered, ref-checked, concurrency-serialized, and assigned a protected `production` environment. Put Azure and registry secrets only on that protected environment.
- Diagnostics are written explicitly with `--output` and uploaded explicitly. They do not grant publication authority.

Action pins used in every example:

```yaml
# actions/checkout v5
uses: actions/checkout@93cb6efe18208431cddfb8368fd83d5badbf9bfd
# actions/setup-dotnet v5
uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1
# azure/login v3
uses: azure/login@532459ea530d8321f2fb9bb10d1e0bcf23869a43
# actions/upload-artifact v5
uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
```

## Pull-request detection and syntax validation

This workflow checks out full history, installs a pinned tool version, writes standard JSON to a file, and validates every affected entrypoint without Azure credentials. It parses the detector's JSON as data; it does not depend on a provider-specific step-output or matrix contract.

```yaml
name: Validate affected Bicep

on:
  pull_request:
    branches: [master]

permissions:
  contents: read

jobs:
  validate:
    runs-on: ubuntu-latest
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
          TOOL_VERSION: 0.1.0-beta.2
          TOOL_SOURCE: ${{ runner.temp }}/bicep-affected-tool
        shell: bash
        run: |
          mkdir -p "$TOOL_SOURCE"
          curl --fail --location --silent --show-error \
            "https://github.com/Bandgren/bicep-affected/releases/download/v${TOOL_VERSION}/BicepAffected.${TOOL_VERSION}.nupkg" \
            --output "$TOOL_SOURCE/BicepAffected.${TOOL_VERSION}.nupkg"
          dotnet tool install --global BicepAffected \
            --version "$TOOL_VERSION" \
            --add-source "$TOOL_SOURCE"

      - name: Detect and validate affected entrypoints
        env:
          BASE_REF: ${{ github.base_ref }}
          REPORT: ${{ runner.temp }}/bicep-affected.json
        shell: bash
        run: |
          bicep-affected affected \
            --repo "$GITHUB_WORKSPACE" \
            --from "origin/$BASE_REF" \
            --to "HEAD" \
            --format json \
            --output "$REPORT"

          jq -e '.schemaVersion == 2 and (.entrypoints | type == "array")' "$REPORT" > /dev/null
          while IFS= read -r bicep_path; do
            az bicep build --file "$bicep_path" --stdout > /dev/null
          done < <(jq -r '.entrypoints[] | .path' "$REPORT")

      - name: Upload detector diagnostic
        if: always()
        uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
        with:
          name: bicep-affected-diagnostic
          path: ${{ runner.temp }}/bicep-affected.json
          if-no-files-found: warn
```

The report is parsed only after checking its schema version and expected array type. Values are read as data, stored in shell variables, and quoted at their command boundary; no JSON value is interpolated into shell source.

## Deployment selection

The affected entrypoint array is also the deployment selection contract. Literal content-load dependencies are traversed in reverse: if `deployments/employees/main.bicep` uses `loadTextContent('../../apis/employees/policy.xml')`, changing `apis/employees/policy.xml` places `deployments/employees/main.bicep` in `.entrypoints` with a `reverseDependency` reason and the full dependency chain.

Keep deployment policy in a trusted repository script rather than accepting commands from detector output:

```yaml
- name: Deploy affected entrypoints
  env:
    REPORT: ${{ runner.temp }}/bicep-affected.json
  shell: bash
  run: |
    jq -e '.schemaVersion == 2 and (.entrypoints | type == "array")' "$REPORT" > /dev/null
    jq -r '.entrypoints[].path' "$REPORT" |
      while IFS= read -r bicep_path; do
        ./ci/deploy-bicep.sh "$bicep_path"
      done
```

`deploy-bicep.sh` must reject unknown paths and map approved entrypoints to their deployment scope, environment, parameter file, and credentials. Do not use `--allow-warnings` in this job: a warning may indicate an incomplete graph, so deployment selection must fail closed.

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
      --output "$SHADOW_REPORT" \
      --allow-warnings

- name: Upload shadow report
  if: always()
  uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
  with:
    name: bicep-affected-shadow
    path: ${{ runner.temp }}/bicep-affected-shadow.json
    if-no-files-found: warn
```

When detection fails or warns in a blocking rollout, run the existing full validation of every relevant Bicep file. Keep that scheduled or manually dispatchable fallback until affected results have been proven against it.

## Protected publishing

Publishing is a separate push/manual workflow, not a PR job. Manual dispatch requires an explicit trusted base revision to compare with the selected protected ref. Configure the `production` environment to require reviewers and restrict deployment branches. The Azure client ID, tenant ID, subscription ID, and registry name below must be protected environment secrets/variables. The publish loop parses only canonical JSON data and keeps every value quoted.

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
  publish:
    if: github.ref_protected
    runs-on: ubuntu-latest
    environment: production
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
          TOOL_VERSION: 0.1.0-beta.2
          TOOL_SOURCE: ${{ runner.temp }}/bicep-affected-tool
        shell: bash
        run: |
          mkdir -p "$TOOL_SOURCE"
          curl --fail --location --silent --show-error \
            "https://github.com/Bandgren/bicep-affected/releases/download/v${TOOL_VERSION}/BicepAffected.${TOOL_VERSION}.nupkg" \
            --output "$TOOL_SOURCE/BicepAffected.${TOOL_VERSION}.nupkg"
          dotnet tool install --global BicepAffected \
            --version "$TOOL_VERSION" \
            --add-source "$TOOL_SOURCE"
      - name: Detect modules to publish
        env:
          BEFORE_SHA: ${{ inputs.base_ref || github.event.before }}
          AFTER_SHA: ${{ github.sha }}
          REPORT: ${{ runner.temp }}/bicep-publish-detection.json
        shell: bash
        run: |
          bicep-affected affected \
            --repo "$GITHUB_WORKSPACE" \
            --from "$BEFORE_SHA" \
            --to "$AFTER_SHA" \
            --include modules \
            --format json \
            --output "$REPORT"

          jq -e '.schemaVersion == 2 and (.publishableModulesToPublish | type == "array")' "$REPORT" > /dev/null
      - name: Upload publishing diagnostic
        if: always()
        uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
        with:
          name: bicep-publish-detection
          path: ${{ runner.temp }}/bicep-publish-detection.json
          if-no-files-found: warn
      - name: Azure login
        uses: azure/login@532459ea530d8321f2fb9bb10d1e0bcf23869a43
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
      - name: Publish versioned modules
        env:
          REPORT: ${{ runner.temp }}/bicep-publish-detection.json
          REGISTRY: ${{ vars.BICEP_REGISTRY }}
        shell: bash
        run: |
          jq -c '.publishableModulesToPublish[]' "$REPORT" |
            while IFS= read -r module; do
              bicep_path="$(jq -r '.path' <<< "$module")"
              module_directory="$(jq -r '.directory' <<< "$module")"
              module_file_name="$(jq -r '.fileName' <<< "$module")"
              version_tag="$(jq -r '.versionTag' <<< "$module")"
              test -n "$version_tag"
              module_target="bicep/modules/$module_directory/$module_file_name:$version_tag"
              target="br:$REGISTRY/${module_target,,}"
              az bicep publish --file "$bicep_path" --target "$target"
            done
```

The sample does not contain a `buildCommand`, provider-specific matrix, or unquoted data interpolation. Consumers decide their own build/publish command from the canonical JSON fields and their trusted workflow policy.