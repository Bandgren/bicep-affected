# Public NuGet publishing and installation

`BicepAffected` is a public .NET global tool. The current beta is attached to its public GitHub release while NuGet.org Trusted Publishing is being activated.

## Install the current public beta

Download [`BicepAffected.0.1.0-beta.1.nupkg`](https://github.com/Bandgren/bicep-affected/releases/download/v0.1.0-beta.1/BicepAffected.0.1.0-beta.1.nupkg) into a local directory, then use that directory as an explicit package source:

```bash
dotnet tool install --global BicepAffected \
  --version 0.1.0-beta.1 \
  --add-source ./downloaded-packages
bicep-affected --help
```

For a repository-local pinned tool:

```bash
dotnet new tool-manifest
dotnet tool install BicepAffected \
  --version 0.1.0-beta.1 \
  --add-source ./downloaded-packages
dotnet tool run bicep-affected --help
```

The source directory must contain the downloaded `.nupkg`. Prerelease versions require an explicit `--version`.

After `BicepAffected` is published and indexed on NuGet.org, the additional source is no longer needed:

```bash
dotnet tool install --global BicepAffected --version 0.1.0-beta.1
```

## Trusted publishing setup

The `Publish Tool` workflow uses NuGet.org Trusted Publishing with GitHub OIDC. It does not store a long-lived NuGet API key.

Before the first publish:

1. Create or sign into the NuGet.org user `Bandgren`.
2. Open **Trusted Publishing** under that NuGet.org account.
3. Add a GitHub Actions policy with:
   - Repository owner: `Bandgren`
   - Repository: `bicep-affected`
   - Workflow file: `publish-tool.yml`
   - Environment: `nuget`
4. In GitHub, protect the `nuget` environment and restrict it to `master` and version tags.
5. Ensure the package ID `BicepAffected` is still available on NuGet.org.

The official setup details are in [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).

## Publish a version

The workflow accepts a NuGet SemVer 2.0 version without build metadata. It restores with committed lock files, builds, runs the full test suite, packs once, installs the exact local package, and requests a one-hour NuGet.org API key through OIDC immediately before the push.

For the initial beta:

1. Push tag `v0.1.0-beta.1` on the reviewed `master` commit.
2. Open **Actions → Publish Tool → Run workflow** on that tag.
3. Enter `0.1.0-beta.1` as the package version.
4. Approve the protected `nuget` environment deployment.

The workflow refuses a tag whose name does not exactly match `v<package-version>`. It also permits a protected manual publish from `master`, but a matching version tag is preferred for public releases.

## Release integrity

- The repository and package are MIT licensed.
- The SDK and third-party actions are pinned.
- NuGet dependencies are restored in locked mode.
- The exact `.nupkg` is smoke-installed before publication.
- Publication uses a short-lived OIDC credential, not a stored API key.
- Existing package versions are immutable; duplicate pushes fail.
- GitHub releases and NuGet package versions use the same SemVer tag.

## Troubleshooting

- **NuGet login fails:** confirm the NuGet.org `Bandgren` account and Trusted Publishing policy exist and match the repository, workflow filename, and `nuget` environment exactly.
- **Package ID rejected:** confirm `BicepAffected` remains available or is already owned by `Bandgren`.
- **Publish refused:** use `master` or tag `v<version>`, provide the matching version without build metadata, and satisfy environment approval.
- **Tool cannot be found immediately:** NuGet.org indexing can lag after a successful push; verify the package page, then retry with the explicit version.
- **Regression after upgrade:** pin the previous known-good version and re-enable the full-validation fallback described in the README.
