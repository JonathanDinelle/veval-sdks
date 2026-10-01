# Veval SDKs

[![npm](https://img.shields.io/npm/v/@veval/sdk?label=npm)](https://www.npmjs.com/package/@veval/sdk)
[![PyPI](https://img.shields.io/pypi/v/veval-sdk?label=pypi)](https://pypi.org/project/veval-sdk/)
[![NuGet](https://img.shields.io/nuget/v/Veval.Sdk?label=nuget)](https://www.nuget.org/packages/Veval.Sdk)
[![Publish](https://github.com/JonathanDinelle/veval-sdks/actions/workflows/publish.yml/badge.svg)](https://github.com/JonathanDinelle/veval-sdks/actions/workflows/publish.yml)

SDKs for [Veval](https://veval.dev) — trace, evaluate, and test AI agents with a few lines of code. Wrap your agent to record every step it takes, then replay recorded runs, assert on them, and catch drift with snapshots. The three SDKs share the same concepts and wire format.

## Install

| Language | Package | Install |
|---|---|---|
| Node | [`@veval/sdk`](node) | `npm install @veval/sdk` |
| Python | [`veval-sdk`](python) | `pip install veval-sdk` |
| C# | [`Veval.Sdk`](dotnet/Veval.Sdk) | `dotnet add package Veval.Sdk` |

Each package's README has a quick start. Guides and the full reference are at [docs.veval.dev](https://docs.veval.dev).

## Releasing

See [publish-how-to.md](publish-how-to.md): bump the version in all three packages and push a `v*` tag.

## License

MIT. See [LICENSE](LICENSE).
