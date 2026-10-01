# Publishing the Veval SDKs

All three SDKs share one version number. Bump it in `node/package.json`,
`python/pyproject.toml` and `dotnet/Veval.Sdk/Veval.Sdk.csproj` before publishing.

## Node SDK

Normally published by `.github/workflows/publish.yml`: bump the version in `node/package.json`,
merge, and push a tag like `v1.2.0`. The workflow stages the version on npm; approve it at
https://www.npmjs.com/package/@veval/sdk (or `npm stage approve`) to make it live. The manual
steps below are the fallback.

The package is `@veval/sdk`, so the npm account publishing it must own or belong to the
`veval` organization (create it once at https://www.npmjs.com/org/create, free for public
packages).

```bash
cd sdk/node

# Verify you are logged in
npm whoami

# Login if needed
npm login

# Run the tests
npm test

# Publish (builds first via prepublishOnly; access is public via publishConfig)
npm publish
```

## C# SDK

```bash
cd sdk/dotnet

dotnet test Veval.Sdk.slnx -c Release
dotnet pack Veval.Sdk/Veval.Sdk.csproj -c Release -o out

# API key from https://www.nuget.org/account/apikeys
dotnet nuget push out/Veval.Sdk.<version>.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json
```

## Python SDK

Normally published by `.github/workflows/publish.yml` on the same `v*` tag: bump the version in
`python/pyproject.toml` first. The manual steps below are the fallback.

```bash
cd sdk/python

# Install build tools if needed
pip install build twine

# Build the distribution
python -m build

# Upload to PyPI
twine upload dist/*
```

For PyPI authentication, create a token at https://pypi.org/manage/account/token/ and configure `~/.pypirc`:

```ini
[pypi]
  username = __token__
  password = pypi-...your-token-here...
```

### Test publish (dry run)

To verify packaging before publishing to production, upload to TestPyPI first:

```bash
twine upload --repository testpypi dist/*
```

Then install from TestPyPI to verify:

```bash
pip install --index-url https://test.pypi.org/simple/ veval-sdk
```
