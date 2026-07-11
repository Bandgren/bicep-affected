# Public NuGet publishing and installation

`BicepAffected` is a public .NET global tool. The current beta is **0.1.0-beta.3**. It is available as a public GitHub release package asset and is **not yet indexed on NuGet.org**.

## Install the current public beta

Download [`BicepAffected.0.1.0-beta.3.nupkg`](https://github.com/Bandgren/bicep-affected/releases/download/v0.1.0-beta.3/BicepAffected.0.1.0-beta.3.nupkg) into a local directory, then use that directory as an explicit source:

```bash
dotnet tool install --global BicepAffected \
  --version 0.1.0-beta.3 \
  --add-source ./downloaded-packages
bicep-affected --help
```

For a repository-local pinned tool:

```bash
dotnet new tool-manifest
dotnet tool install BicepAffected \
  --version 0.1.0-beta.3 \
  --add-source ./downloaded-packages
dotnet tool run bicep-affected --help
```

The source directory must contain the downloaded `.nupkg`; prereleases require an explicit version. After NuGet.org publishes **and indexes** this package, the explicit source is no longer needed:

```bash
dotnet tool install --global BicepAffected --version 0.1.0-beta.3
```

Do not use that shorter command yet: NuGet.org is not indexed for the current beta.

## beta.3 operational compatibility

beta.3 is a breaking migration from beta.2. Pin `0.1.0-beta.3` with consumers that validate affected JSON `schemaVersion: 3`, pass an explicit `--target`, and iterate `.targets`. The tool's affected text is one path per line; use `explain` for reasons and dependency chains. `--output` writes only its file, so consumers must read that file rather than expect rendered JSON on stdout. These are runtime contract changes, not NuGet publishing changes.

## Trusted publishing setup

The `Publish Tool` workflow uses NuGet.org Trusted Publishing with GitHub OIDC. It does not store a long-lived NuGet API key.

Before the first publish:

1. Create or sign into the NuGet.org user `Bandgren`.
2. Open **Trusted Publishing** under that NuGet.org account.
3. Add a GitHub Actions policy with repository owner `Bandgren`, repository `bicep-affected`, workflow file `publish-tool.yml`, and environment `nuget`.
4. In GitHub, protect the `nuget` environment and restrict it to `master` and version tags.
5. Ensure package ID `BicepAffected` remains available on NuGet.org.

See [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) for the official setup.

## Publish beta.3

The workflow accepts a NuGet SemVer 2.0 version without build metadata. It restores with committed lock files, builds, runs its validation, packs once, installs the exact local package, and requests a short-lived NuGet.org API key through OIDC immediately before push.

1. Push tag `v0.1.0-beta.3` on the reviewed `master` commit.
2. Open **Actions → Publish Tool → Run workflow** on that tag.
3. Enter `0.1.0-beta.3` as the package version.
4. Approve the protected `nuget` environment deployment.

The workflow refuses a tag whose name does not exactly match `v<package-version>`. A protected manual publish from `master` is allowed, but a matching version tag is preferred for public releases.

## Release integrity and troubleshooting

- The repository and package are MIT licensed.
- SDK and third-party actions are pinned; dependencies restore in locked mode.
- The exact `.nupkg` is smoke-installed before publication.
- Publication uses a short-lived OIDC credential, not a stored API key.
- Existing package versions are immutable; duplicate pushes fail.
- GitHub release and NuGet package versions use the same SemVer tag.
- **Tool is not found:** current beta.3 is not yet indexed on NuGet.org; use the downloaded package and explicit source. After publication, indexing can also lag—verify the package page and retry the explicit version.
- **Publish refused:** use `master` or tag `v<version>`, supply the matching version without build metadata, and satisfy environment approval.
- **Regression after upgrade:** pin the last known-good version and use the full-validation fallback described in the README while investigating.
