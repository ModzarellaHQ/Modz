# Modz

The mod catalog for [Cheese Rolling](https://store.steampowered.com/app/3809440/), used by **[Modzarella](https://github.com/ModzarellaHQ/Modzarella)**.

> Just want to play? Get **[Modzarella](https://github.com/ModzarellaHQ/Modzarella)**. You don't need anything here.

## Making a mod

```
mods/<id>/
  mod.json       name, version, description, dependencies, credits
  <Name>.csproj  <Project Sdk="Microsoft.NET.Sdk" />
  *.cs           a BepInEx 5 plugin that depends on core
  files/         what gets installed: the built DLL plus assets
```

1. Copy a mod folder and rename it.
2. Build it: `dotnet build mods/<id> -c Release`
3. Test it: run `python3 .github/index.py`, then `Modzarella source <this folder>`
4. Bump `version` in `mod.json` and open a pull request.

## Rules

- Offline only: check `ModCommon.Active`.
- No game files or ripped assets.
- Credit third-party assets in `credits`.

## License

Code is MIT. Assets are under the licenses listed in each `mod.json`.
