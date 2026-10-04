using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var mods = new JsonArray();

foreach (var dir in Directory.GetDirectories(Path.Combine(root, "mods")).OrderBy(d => d))
{
    var manifest = Path.Combine(dir, "mod.json");
    if (!File.Exists(manifest)) continue;
    var folder = Path.GetFileName(dir);
    var mod = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();

    var build = Process.Start("dotnet", ["build", Path.Combine(dir, folder + ".csproj"), "-c", "Release", "-nologo", "-v", "q"])!;
    build.WaitForExit();
    if (build.ExitCode != 0) { Console.Error.WriteLine($"build failed: {folder}"); return 1; }

    var dist = Path.Combine(dir, "dist");
    if (Directory.Exists(dist)) Directory.Delete(dist, true);
    Directory.CreateDirectory(dist);
    var dll = $"CheeseMods.{folder}.dll";
    File.Copy(Path.Combine(root, "build", folder, dll), Path.Combine(dist, dll));

    var files = new JsonArray();
    foreach (var baseDir in new[] { dist, Path.Combine(dir, "assets") }.Where(Directory.Exists))
        foreach (var f in Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories).OrderBy(f => f))
            files.Add(new JsonObject
            {
                ["path"] = Path.GetRelativePath(root, f).Replace('\\', '/'),
                ["target"] = Path.GetRelativePath(baseDir, f).Replace('\\', '/'),
                ["size"] = new FileInfo(f).Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))),
            });
    mod["files"] = files;
    mods.Add(mod);
    Console.WriteLine($"{mod["id"]} {mod["version"]}: {files.Count} files");
}

var index = new JsonObject { ["game"] = "Cheese Rolling", ["steamAppId"] = 3809440, ["mods"] = mods };
File.WriteAllText(Path.Combine(root, "index.json"), index.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
Console.WriteLine($"index.json: {mods.Count} mods");
return 0;
