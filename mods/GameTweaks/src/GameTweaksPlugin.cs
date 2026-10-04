using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CheeseMods
{
    [BepInPlugin(GUID, "Game Tweaks", "2.0.0")]
    [BepInDependency(CorePlugin.GUID)]
    public class GameTweaksPlugin : BaseUnityPlugin
    {
        public const string GUID = "cheesemods.gametweaks";
        public static GameTweaksPlugin Instance;

        public ConfigEntry<bool> Enabled, Endless, FreezeBots, VSyncOff;
        public ConfigEntry<int> FpsCap;
        public ConfigEntry<KeyboardShortcut> EndlessKey, FreezeKey;

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Endless rounds, frozen bots and frame-rate settings.");
            Endless = Config.Bind("Game", "Endless round", false, "Nobody can catch the cheese and the timer stops before the final countdown.");
            FreezeBots = Config.Bind("Game", "Freeze bots", false, "Bots stand still and do nothing.");
            VSyncOff = Config.Bind("Performance", "Disable VSync", true, "VSync drops to 30 fps whenever the game misses 60. Off is smoother.");
            FpsCap = Config.Bind("Performance", "FPS cap (0 means none)", 0, ModCommon.Desc("Frame-rate cap when VSync is off.", new AcceptableValueRange<int>(0, 240), true));
            EndlessKey = Config.Bind("Keys", "Endless round", new KeyboardShortcut(KeyCode.F2), "Toggle endless round.");
            FreezeKey = Config.Bind("Keys", "Freeze bots", new KeyboardShortcut(KeyCode.F4), "Toggle frozen bots.");
            Endless.SettingChanged += (_, __) => ModCommon.Toast(Endless.Value ? "Endless round on" : "Endless round off — 6 s left");
            FreezeBots.SettingChanged += (_, __) => ModCommon.Toast(FreezeBots.Value ? "Bots frozen" : "Bots unfrozen");
            VSyncOff.SettingChanged += (_, __) => ApplyPerf();
            FpsCap.SettingChanged += (_, __) => ApplyPerf();
            ApplyPerf();
            MenuRegistry.QuickToggle($"Endless round ({EndlessKey.Value})", Endless);
            MenuRegistry.QuickToggle($"Freeze bots ({FreezeKey.Value})", FreezeBots);
            new Harmony(GUID).PatchAll(typeof(GameTweaksPatches));
        }

        internal bool On => Enabled.Value && ModCommon.Active;

        private void Update()
        {
            if (VSyncOff.Value && QualitySettings.vSyncCount != 0) ApplyPerf();
            if (!ModCommon.Active || CheeseApi.MenuOpen) return;
            if (EndlessKey.Value.IsDown()) Endless.Value = !Endless.Value;
            if (FreezeKey.Value.IsDown()) FreezeBots.Value = !FreezeBots.Value;
        }

        private void ApplyPerf()
        {
            QualitySettings.vSyncCount = VSyncOff.Value ? 0 : 1;
            Application.targetFrameRate = VSyncOff.Value && FpsCap.Value > 0 ? FpsCap.Value : -1;
        }

        private void OnGUI()
        {
            if (!Endless.Value || !ModCommon.InRound) return;
            GUI.Label(new Rect(20, 16, 300, 30), $"∞ ENDLESS ROUND ({EndlessKey.Value})",
                new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.85f, 0.2f) } });
        }
    }

    internal static class GameTweaksPatches
    {
        private static GameTweaksPlugin P => GameTweaksPlugin.Instance;
        private static bool EndlessOn => P && P.On && P.Endless.Value;

        [HarmonyPrefix, HarmonyPatch(typeof(Cheese), nameof(Cheese.Catch))]
        private static bool NoCatch(ref bool __result)
        {
            if (!EndlessOn) return true;
            __result = false;
            return false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), "Update")]
        private static void HoldClock(GameManager __instance)
        {
            if (!EndlessOn || __instance.round == 0) return;
            float hold = __instance.GetRoundLength() - 6f;
            if (__instance.roundTime <= hold) return;
            __instance.roundStartTime = Time.time - hold;
            __instance.roundTime = hold;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.RestartGame))]
        private static bool NoRestart() => !EndlessOn;

        [HarmonyPrefix, HarmonyPatch(typeof(BotController), nameof(BotController.CreateInput))]
        private static bool FreezeBot(BotController __instance)
        {
            if (!P || !P.On || !P.FreezeBots.Value) return true;
            var r = __instance.GetComponent<ActiveRagdoll>();
            if (r && !CheeseApi.IsSeated(r)) r.Input(new RagdollInput(false, false, false, Vector3.zero));
            return false;
        }
    }
}
