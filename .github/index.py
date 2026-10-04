import hashlib, json, pathlib, re, sys

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
    if not (folder / "main.lua").is_file():
        errors.append(f"{folder.name}: no main.lua")
    scripts = sorted(folder.glob("*.lua"))
    assets = sorted(p for p in (folder / "files").rglob("*") if p.is_file() and p.name != ".DS_Store")
    mod.setdefault("dependencies", [])
    mod["files"] = [{
        "path": p.relative_to(root).as_posix(),
        "target": p.relative_to(folder / "files" if p in assets else folder).as_posix(),
        "size": p.stat().st_size,
        "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
    } for p in scripts + assets]
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

