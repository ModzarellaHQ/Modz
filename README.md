# Modz

Mods for [Cheese Rolling](https://store.steampowered.com/app/3809440/). To play them, get [Modzarella](https://github.com/ModzarellaHQ/Modzarella).

## Adding a mod

Copy a folder in `mods/` and rename it:

| File | Contents |
|---|---|
| `mod.json` | Name, version, description, credits |
| `*.cs`, `<Name>.csproj` | A BepInEx 5 plugin |
| `files/` | What gets installed |

```sh
dotnet build mods/<id> -c Release   # needs the game, set up once by Modzarella
python3 .github/index.py && Modzarella source .
```

Then bump `version` and open a pull request. Keep mods offline-only, leave out game files, and credit any assets. Code is MIT.
