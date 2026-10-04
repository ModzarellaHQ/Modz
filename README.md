# Modz

The mod catalog for [Cheese Rolling](https://store.steampowered.com/app/3809440/).

> **Just want to play with mods?** You don't need anything here. Get **[Modzarella](https://github.com/ModzarellaHQ/Modzarella)**, the app that installs and runs these mods for you.

## Mods

| Mod | What it does |
|---|---|
| `core` | Required by every mod. In-game mod menu (F1) and camera features |
| `bmw` | Drivable BMW M2 with crash damage |
| `gore` | Limbs come off, bleeding out |
| `guns` | Glock 17 and AK-47 |
| `toilet` | Rocket toilet |
| `euphoria` | Ragdolls that react and struggle |
| `tweaks` | Endless round, freeze bots, slow motion |

## Making a mod

Each mod is one folder in `mods/`:

```
mods/<id>/
  mod.json       name, version, description, dependencies, credits
  <Name>.csproj  <Project Sdk="Microsoft.NET.Sdk" />
  *.cs           source (a BepInEx 5 plugin depending on core)
  files/         what gets installed: the built DLL plus assets
```

1. Copy an existing mod folder and rename it.
2. Build with `dotnet build mods/<id> -c Release`. You need the game, BepInEx (Modzarella installs it) and the .NET SDK 8+.
3. Test it: run `python3 .github/index.py`, then point Modzarella at this folder with `Modzarella source <path>`.
4. Bump `version` in `mod.json` and open a pull request. CI checks every `mod.json`.

## Rules

- Mods must check `ModCommon.Active`, so they only run in Play Offline.
- No game files or ripped assets.
- Credit third-party assets and their licenses in `credits`.

## License

Code is MIT. Assets carry their own licenses, listed in each `mod.json`. The BMW model is CC-BY-NC-SA 4.0 (non-commercial use only).
