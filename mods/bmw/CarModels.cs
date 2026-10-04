using System.IO;
using BepInEx;
using UnityEngine;

namespace Modz
{
    public static class CarModels
    {
        private static GlbLoader.CarModel cached;
        private static string cachedKey;
        private static Transform holder;

        public static string ResolvePath()
        {
            return Path.Combine(Path.GetDirectoryName(typeof(CarModels).Assembly.Location), "car.glb");
        }

        public static GlbLoader.CarModel Get()
        {
            string path = ResolvePath();
            if (path == null || !File.Exists(path)) return null;
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (cached != null && key == cachedKey && cached.Body) return cached;

            if (holder) Object.Destroy(holder.gameObject);
            var go = new GameObject("CarModelTemplates");
            go.SetActive(false);
            Object.DontDestroyOnLoad(go);
            holder = go.transform;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                cached = GlbLoader.Load(path, 1f, 0f, holder);
                cachedKey = key;
                CarPlugin.Log.LogInfo($"Car model {Path.GetFileName(path)}: {cached.Triangles} tris, {cached.DrawCalls} draw calls, " +
                    $"{(cached.Wheels != null ? "4 wheels split" : "wheels not found (static)")}, {sw.ElapsedMilliseconds} ms");
            }
            catch (System.Exception e)
            {
                CarPlugin.Log.LogError("Car model failed to load, using built-in car: " + e);
                CarPlugin.Instance.Toast("Car model failed — see BepInEx log");
                cached = null;
                cachedKey = key; // don't retry every spawn
            }
            return cached;
        }
    }
}
