# Modz

Mods for [Cheese Rolling](https://store.steampowered.com/app/3809440/), installed through [Modzarella](https://github.com/ModzarellaHQ/Modzarella). If you just want to play, that's the one you want.

## Adding a mod

Each mod is a folder in `mods/`. The easiest start is to copy an existing one.

| File | Contents |
|---|---|
| `mod.json` | Name, version, description, dependencies, credits |
| `<Name>.csproj` | `<Project Sdk="Microsoft.NET.Sdk" />` |
| `*.cs` | A BepInEx 5 plugin that depends on `core` |
| `files/` | What gets installed: the built DLL and any assets |

```sh
dotnet build mods/<id> -c Release     # build into files/
python3 .github/index.py              # make a local index.json
Modzarella source .                   # point Modzarella at this folder to test
```

Bump `version` in `mod.json` when you're done, then open a pull request.

**Please:** keep mods offline-only (check `ModCommon.Active`), don't include game files, and credit any third-party assets.

Code is MIT. Assets are under the licenses listed in each `mod.json`.
