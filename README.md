# CheeseMods

Mods for [Cheese Rolling](https://store.steampowered.com/app/3809440/) (Steam). Install them with **[CheeseMM](../CheeseMM)**, the mod manager.

Mods only run in **Play Offline**. Press **F1** in game for the mod menu.

| Mod | What it does | Keys |
|---|---|---|
| `core` | Required. Mod menu, mouse camera, first person, zoom, speed FOV | F1 menu · F3 camera · V first person · scroll zoom |
| `bmw` | Drivable BMW M2: crash damage, dents, engine sounds, seated driver | E enter/exit · WASD · Space brake · Shift nitro · H horn · R flip · Backspace reset |
| `gore` | Real severed limbs, bleeding out, wounds, spatter, car and bullet gore | – |
| `guns` | Glock 17 and AK-47 with recoil, ADS, reloads | 1 / 2 draw · 3 holster · LMB fire · RMB aim · R reload |
| `toilet` | Rocket toilet you fly on a column of poop | T sit · Space/G thrust · Ctrl hover · WASD fly |
| `euphoria` | Active ragdolls: brace, shield, flinch, writhe | – |
| `gametweaks` | Endless round, freeze bots, VSync/FPS cap | F2 endless · F4 freeze |

## Manual install

1. Install [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) into the game folder.
   On macOS also set `executable_name="CheeseRolling.app"` and `arch -x86_64` in `run_bepinex.sh`, and `Type = Camera` under `[Preloader.Entrypoint]` in `BepInEx/config/BepInEx.cfg`. CheeseMM does all of this for you.
2. For each mod, copy `mods/<Mod>/dist/*` and `mods/<Mod>/assets/*` into `BepInEx/plugins/CheeseMods/<id>/`.

## Build

Needs the .NET SDK 8+ and the game with BepInEx installed.

```sh
dotnet run --project tools/Pack
```

This builds every mod into `mods/<Mod>/dist/` and writes `index.json`, the catalog CheeseMM reads.
If the game isn't in the default Steam folder, set `GameDir`:

```sh
GameDir="/path/to/Cheese Rolling" dotnet run --project tools/Pack
```

## Add or change a mod

```
mods/MyMod/
  mod.json        id, name, version, author, description, dependencies, credits
  MyMod.csproj    <Project Sdk="Microsoft.NET.Sdk" />
  src/*.cs
  assets/         optional; installed next to the DLL
```

```json
{ "id": "mymod", "name": "My Mod", "version": "1.0.0", "author": "you",
  "description": "One line.", "dependencies": ["core"], "credits": ["Model by X, CC-BY 4.0, link"] }
```

```csharp
[BepInPlugin("cheesemods.mymod", "My Mod", "1.0.0")]
[BepInDependency(CorePlugin.GUID)]
public class MyModPlugin : BaseUnityPlugin
{
    void Awake()
    {
        var enabled = Config.Bind("General", "Enabled", true, "One line shown in the mod menu.");
        Config.Bind("My Mod", "Power", 1f, ModCommon.Desc("Shown in the menu.", new AcceptableValueRange<float>(0f, 5f)));
        Config.Bind("My Mod", "Niche", 2f, ModCommon.Desc("Hidden behind Show advanced.", advanced: true));
        MenuRegistry.Action("cheesemods.mymod", "Do it", () => ModCommon.Toast("Done"));
    }
}
```

Every `Config.Bind` shows up in the F1 menu automatically. Useful Core APIs:

| API | Use |
|---|---|
| `ModCommon.Active`, `InRound`, `LocalRagdoll()`, `AllRagdolls()`, `BodyScale(r)` | Game state |
| `ModCommon.Toast`, `MenuRegistry.Action`, `MenuRegistry.QuickToggle` | Menu and messages |
| `CheeseApi.IsSeated`, `SetSeated`, `Driver`, `DriverVehicle`, `IsVehicle` | Vehicles and seating |
| `CheeseApi.BulletHit`, `IsDead`, `FirstPerson`, `MenuOpen` | Hooks between mods |
| `KinematicSeat`, `GlbLoader.Load`, `Wav.LoadFolder`, `ModCommon.Solid/UnlitMaterial` | Seats, .glb models, .wav sounds, materials |

Then bump `version` in `mod.json`, run Pack, and commit the source, `dist/` and `index.json`.

## Rules

- Offline only: check `ModCommon.Active` before doing anything.
- No game files, decompiled code or ripped assets.
- Credit every third-party asset with its license in `credits`.

## License

Code: MIT. Assets: see each mod's `credits` (the BMW model is CC-BY-NC-SA 4.0: non-commercial use only).
