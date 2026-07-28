# Publishing the Veval SDKs

## Node SDK

```bash
cd sdk/node

# Build
npm run build

# Verify you are logged in
npm whoami

# Login if needed
npm login

# Publish
npm publish --access public
```

## Python SDK

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
