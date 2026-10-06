Self-hosted resume toolkit. One download per platform, no .NET or Node needed on your machine.

Download the zip for your platform, unzip it, run the binary. It opens your browser at
http://127.0.0.1:5177 and your data stays in the folder.

| Platform | Zip |
|---|---|
| Windows x64 | `resu-clean-win-x64.zip` |
| macOS x64 (Intel, or Apple silicon under Rosetta) | `resu-clean-osx-x64.zip` |
| Linux x64 | `resu-clean-linux-x64.zip` |

ARM64 is not here yet. See "What's left for v2" in the README if you want to help add it.

## What you get

- Resume intake from paste, `.txt`, `.docx` or `.pdf`
- Deterministic cleaner, ATS scoring and keyword matching, with no model required
- Profile facts you approve, with fuzzy dedupe
- Job search over RSS, JSON, HTML and plain links, with robots handling, rate limits and caching
- Application kits as DOCX, PDF, EML, ZIP, mailto and tracker
- Model routing you point at your own keys, with fallbacks in order

Every part of the deterministic path works with no API key at all. A model only adds rewording,
prioritised fixes, tailoring and written emails.

## Two binaries per zip

- `resu-clean` (or `resu-clean.exe`) runs the app on one port.
- `resu-clean-dev` starts the backend and the Vite dev server with hot reload. That one needs the
  repo, so it is only useful if you cloned the source.

## Your keys

Keys are encrypted before they hit the database and no endpoint ever returns one, including the ones
that list providers. Prefer an environment variable in `.env` if you would rather store nothing at
all. Windows uses DPAPI per user account; macOS and Linux use AES-256 with `Data/machine.key`.

Your database and keys live in the `Data/` folder inside the bundle, so copying the folder to another
machine copies everything.

## Notes

- The builds are x64 only. See the README section on ARM64.
- Memory sits around 60 MB idle. The UI bundle is under 80 KB gzipped and there is no external font,
  script or CDN request.
- Licence: MIT.
