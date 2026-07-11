# GitHub Actions rollout examples

These examples consume the beta.3 actionable-output contract. They use JSON `schemaVersion: 3`, an explicit action `target`, and the canonical `.targets` array. The detector returns data only: each consuming repository retains the trusted mapping from an approved path to deployment scope, environment, credentials, registry, and command.

Every action is pinned to an immutable commit SHA. GitHub expressions are passed through `env:` rather than embedded in shell source; every shell expansion is quoted. Analysis warnings fail closed by default, so a blocking job writes no payload when analysis reports a warning.

## Rules for a safe rollout

- PR jobs use only `contents: read`; they must not use Azure credentials or publish.
- Pin the tool version and install it from an explicit source until NuGet.org indexes it.
- Validate `schemaVersion`, `target`, and the `.targets` type before reading any values.
- Keep path-to-command mapping in a reviewed repository script. Do not construct shell source from detector output.
- Blocking build, deployment, and publishing jobs must not pass `--allow-warnings`.
- Retain a scheduled or manually dispatchable **full-validation fallback**. Run it when detection fails or emits warnings.
- Keep publishing separately triggered, ref-checked, serialized, and protected by an environment. Put Azure and registry credentials only on that environment.

The immutable action pins in the examples are:

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

## Pull-request detection and build selection

An APIM policy edit can affect a deployment entrypoint through `loadTextContent`. This PR job asks for the `build` target, which includes affected entrypoints and affected publishable modules, then passes each path as data to a trusted validation script. The script must explicitly allow the received paths and choose its own command.

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
          TOOL_VERSION: 0.1.0-beta.3
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

      - name: Detect build targets and validate
        env:
          BASE_REF: ${{ github.base_ref }}
          REPORT: ${{ runner.temp }}/bicep-affected.json
        shell: bash
        run: |
          bicep-affected affected \
            --repo "$GITHUB_WORKSPACE" \
            --from "origin/$BASE_REF" \
            --to HEAD \
            --target build \
            --format json \
            --output "$REPORT"

          jq -e \
            '.schemaVersion == 3 and .target == "build" and (.targets | type == "array")' \
            "$REPORT" > /dev/null
          jq -r '.targets[].path' "$REPORT" |
            while IFS= read -r bicep_path; do
              ./ci/validate-bicep-target.sh "$bicep_path"
            done

      - name: Upload detector diagnostic
        if: always()
        uses: actions/upload-artifact@330a01c490aca151604b8cf639adc76d48f6c5d4
        with:
          name: bicep-affected-diagnostic
          path: ${{ runner.temp }}/bicep-affected.json
          if-no-files-found: warn
```

`--output` writes the report only to `REPORT`; it does not duplicate rendered JSON to stdout. If warnings occur, the default failure happens before a report is written. The upload step remains diagnostic-only and cannot grant deployment authority.

## Deployment selection and causality review

Use `deploy` for entrypoint-only deployment selection. `explain` is useful during review: it prints selected targets plus each reason and reverse-dependency chain, while `affected` remains one path per line in text mode.

```yaml
- name: Detect deploy targets
  env:
    REPORT: ${{ runner.temp }}/bicep-deploy.json
  shell: bash
  run: |
    bicep-affected affected \
      --repo "$GITHUB_WORKSPACE" \
      --from "$BEFORE_SHA" \
      --to "$AFTER_SHA" \
      --target deploy \
      --format json \
      --output "$REPORT"
    jq -e \
      '.schemaVersion == 3 and .target == "deploy" and (.targets | type == "array")' \
      "$REPORT" > /dev/null

- name: Deploy approved targets
  env:
    REPORT: ${{ runner.temp }}/bicep-deploy.json
  shell: bash
  run: |
    jq -r '.targets[].path' "$REPORT" |
      while IFS= read -r bicep_path; do
        ./ci/deploy-bicep.sh "$bicep_path"
      done
```

`deploy-bicep.sh` must reject unknown paths and map known entrypoints to the appropriate scope, parameter file, and protected credentials. For a policy change such as `apis/employees/policy.xml`, `explain --target deploy` identifies the selected entrypoint and the content-load dependency chain that selected it.

## Deliberate shadow comparison and fallback

A shadow job may explicitly permit warnings only while an established full-validation job is authoritative:

```yaml
- name: Shadow compare build selection
  continue-on-error: true
  env:
    BASE_REF: ${{ github.base_ref }}
    SHADOW_REPORT: ${{ runner.temp }}/bicep-affected-shadow.json
  shell: bash
  run: |
    bicep-affected affected \
      --repo "$GITHUB_WORKSPACE" \
      --from "origin/$BASE_REF" \
      --to HEAD \
      --target build \
      --format json \
      --output "$SHADOW_REPORT" \
      --allow-warnings
```

Keep the existing complete Bicep validation runnable on schedule and by manual dispatch. A warning, detector failure, or questionable selection is a signal to use that full-validation fallback—not a reason to weaken a blocking job.

## Protected publish selection

Only the `publish` target selects affected publishable modules that have changed, readable configured version metadata. The protected workflow below preserves the detector's role as a data source and applies the registry mapping in the trusted workflow.

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
          TOOL_VERSION: 0.1.0-beta.3
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
      - name: Detect publish targets
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
            --target publish \
            --format json \
            --output "$REPORT"
          jq -e \
            '.schemaVersion == 3 and .target == "publish" and (.targets | type == "array")' \
            "$REPORT" > /dev/null
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
          jq -c '.targets[]' "$REPORT" |
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

Configure `production` to require reviewers and restrict deployment branches. Keep Azure identity and registry configuration in that protected environment. The workflow validates the payload before parsing, quotes every data boundary, and never treats a returned value as shell code.
