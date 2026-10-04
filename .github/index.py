import hashlib, html, json, pathlib, re, sys

root = pathlib.Path(__file__).resolve().parent.parent
mods, errors = [], []

for manifest in sorted(root.glob("mods/*/mod.json")):
    folder = manifest.parent
    try:
        mod = json.loads(manifest.read_text())
    except ValueError as e:
        errors.append(f"{manifest}: {e}")
        continue
    for key in ("id", "name", "version", "author", "description"):
        if not isinstance(mod.get(key), str) or not mod[key]:
            errors.append(f"{folder.name}/mod.json: missing \"{key}\"")
    if mod.get("id") != folder.name:
        errors.append(f"{folder.name}/mod.json: id must be \"{folder.name}\"")
    if not re.fullmatch(r"\d+\.\d+\.\d+", mod.get("version", "")):
        errors.append(f"{folder.name}/mod.json: version must look like 1.2.3")
    files = sorted(p for p in (folder / "files").rglob("*") if p.is_file() and p.name != ".DS_Store")
    if not any(p.suffix == ".dll" for p in files):
        errors.append(f"{folder.name}: no .dll in files/")
    mod.setdefault("dependencies", [])
    mod["files"] = [{
        "path": p.relative_to(root).as_posix(),
        "target": p.relative_to(folder / "files").as_posix(),
        "size": p.stat().st_size,
        "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
    } for p in files]
    mods.append(mod)

ids = {m["id"] for m in mods}
for m in mods:
    for dep in m["dependencies"]:
        if dep not in ids:
            errors.append(f"{m['id']}: unknown dependency \"{dep}\"")

if errors:
    sys.exit("\n".join(errors))
index = {"game": "Cheese Rolling", "steamAppId": 3809440, "mods": mods}
(root / "index.json").write_text(json.dumps(index, indent=2, ensure_ascii=False) + "\n")
print(f"index.json: {len(mods)} mods")

cards = "".join(
    f"<li><b>{html.escape(m['name'])}</b> <span>v{html.escape(m['version'])}</span><br>{html.escape(m['description'])}</li>"
    for m in mods)
(root / "index.html").write_text(f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Modzarella</title>
<style>
body{{margin:0;font:16px/1.5 system-ui,sans-serif;background:#16161a;color:#ececf0}}
main{{max-width:720px;margin:0 auto;padding:48px 16px}}
h1{{color:#f5b830;margin:0}} a{{color:#f5b830}} span{{color:#9a9aa6}}
.btn{{display:inline-block;margin:24px 0;padding:12px 28px;background:#f5b830;color:#1a1300;border-radius:8px;font-weight:700;text-decoration:none}}
ul{{list-style:none;padding:0}} li{{background:#202026;border:1px solid #2e2e36;border-radius:10px;padding:12px 16px;margin:8px 0}}
</style></head><body><main>
<h1>Modzarella</h1>
<p>Mods for <a href="https://store.steampowered.com/app/3809440/">Cheese Rolling</a>. One app installs and runs them on macOS, Windows and Linux.</p>
<a class="btn" href="https://github.com/ModzarellaHQ/Modzarella/releases/latest">Download Modzarella</a>
<h2>Mods</h2><ul>{cards}</ul>
<p><a href="https://github.com/ModzarellaHQ/Modz">Make or share a mod</a> · <a href="https://github.com/ModzarellaHQ/Modzarella">Source</a></p>
</main></body></html>
""")
