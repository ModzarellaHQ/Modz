# Contributing a mod

Mods are Lua scripts run by Modzarella inside Cheese Rolling. Read the [Lua API](https://github.com/ModzarellaHQ/Modzarella/blob/main/docs/lua-api.md) first; this page covers what a mod in this repository must look like.

## Layout

```
mods/<id>/
  mod.json      manifest (required)
  main.lua      runs when the game starts (required)
  *.lua         more scripts, loaded with require("name")
  files/        models and sounds, installed next to main.lua
```

- `<id>` is lowercase letters, digits and dashes, and matches `id` in `mod.json`.
- Lua files sit at the root of the mod folder. Everything else goes in `files/`.

## mod.json

```json
{
  "id": "superjump",
  "name": "Super Jump",
  "version": "1.0.0",
  "author": "your name",
  "description": "One sentence about what the mod does.",
  "dependencies": [],
  "credits": ["Jump sound: \"Boing\" by someone, CC0, https://example.com"]
}
```

| Field | Rule |
|---|---|
| `id` | Same as the folder name. Never changes once published. |
| `name` | Shown in the app and the F1 menu. |
| `version` | `major.minor.patch`. Bump it with every change, or players won't get the update. |
| `author` | You, or your team. |
| `description` | One sentence, 120 characters at most. Describe your mod on its own: don't mention other mods or key bindings. |
| `dependencies` | Ids of mods that must be installed too. Keep it empty whenever you can. |
| `credits` | Required when you use anything you didn't make: title, author, licence and link. |

## Files

| Kind | Format |
|---|---|
| Models | `.glb` (binary glTF), textures embedded. Keep them light: decimate and compress before adding. |
| Sounds | `.wav`, 16-bit PCM. Mono is enough for most effects. |

A mod is 20 MB at most, scripts and files together. No other file types, no executables and no DLLs.

## Code

- **Reuse Core.** Seats, vehicles, holding objects, muscles, models, camera and effects are already built: see [Building blocks](https://github.com/ModzarellaHQ/Modzarella/blob/main/docs/lua-api.md#building-blocks). Don't rebuild them in your mod.
- **Lua only.** Mods run in a sandbox without `os`, `io` or file access outside the mod folder.
- **Stand alone.** A mod must work with no other mod installed. To react to other mods, use `events` (`bullet_hit`, `wheels_bloody`, or your own) and `game.vehicles()`, never names like `find("BMW")`.
- **Settings.** Declare them with `setting.*`, give each a short `desc`, and keep the list short. Rarely used ones get `advanced = true`.
- **Keys.** Pick defaults that don't clash with the game or the mods here, and check `input.allowed()` before acting on raw input.
- **Clean up.** Destroy what you spawn in `on_unload`, so Reload leaves nothing behind.
- **Performance.** Nothing heavy every frame on every ragdoll. Use `hold` on muscles and spread work across steps, as Euphoria does.
- **Offline only.** No online features, nothing that sends data anywhere, nothing meant to cheat against other players.

## Assets and licences

Scripts are MIT, like the rest of this repository. Models and sounds keep their own licence, which must allow redistribution, and must be listed in `credits`. Don't add anything ripped from other games.

## Submitting

1. Run `python3 .github/index.py`. It checks the rules above and must print `mods/index.json: N mods` without errors.
2. Point Modzarella at your clone with `Modzarella source mods`, install your mod and play a round with it in Play Offline.
3. Open a pull request with a short description and a screenshot.

To update a mod, change what you need, bump `version` and open a pull request the same way.
