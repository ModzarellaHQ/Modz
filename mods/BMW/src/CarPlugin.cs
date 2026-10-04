using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace CheeseMods
{
    [BepInPlugin(GUID, "BMW", "2.0.0")]
    [BepInDependency(CorePlugin.GUID)]
    public class CarPlugin : BaseUnityPlugin
    {
        public const string GUID = "cheesemods.bmw";
        internal static ManualLogSource Log;
        public static CarPlugin Instance;

        public ConfigEntry<bool> Enabled, RamBoost, CrashPhysics, Eject, ShowDriverInModel;
        public ConfigEntry<float> EnginePower, TopSpeed, Steering, Grip, NitroPower, CarSize, CarVolume, DamageMul, EjectSpeed;
        public ConfigEntry<string> Paint;
        public ConfigEntry<KeyboardShortcut> EnterKey, FlipKey, ResetKey;

        public CheeseCar car;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "A drivable BMW M2 with crash damage, real engine sounds and a seated driver.");
            EnginePower = Config.Bind("Car", "Engine power", 1f, ModCommon.Desc("Acceleration multiplier.", new AcceptableValueRange<float>(0.1f, 5f)));
            TopSpeed = Config.Bind("Car", "Top speed", 160f, ModCommon.Desc("Engine speed limit (gravity can exceed it).", new AcceptableValueRange<float>(30f, 600f)));
            Grip = Config.Bind("Car", "Tyre grip", 1f, ModCommon.Desc("Sideways grip; lower drifts more.", new AcceptableValueRange<float>(0.05f, 3f)));
            Paint = Config.Bind("Car", "Paint", "Original", new ConfigDescription("Paint colour. Applies to the next car.",
                new AcceptableValueList<string>("Original", "Estoril Blue", "Alpine White", "Hellrot", "Brilliant Red", "Black Sapphire")));
            CarVolume = Config.Bind("Car", "Engine volume", 0.5f, ModCommon.Desc("Car sounds, on top of the game's Master x SFX volume.", new AcceptableValueRange<float>(0f, 1f)));
            Steering = Config.Bind("Car", "Steering", 1f, ModCommon.Desc("Steering multiplier.", new AcceptableValueRange<float>(0.2f, 3f), true));
            NitroPower = Config.Bind("Car", "Nitro (Shift)", 2.2f, ModCommon.Desc("Acceleration multiplier while holding Shift.", new AcceptableValueRange<float>(1f, 8f), true));
            CarSize = Config.Bind("Car", "Car size", 1f, ModCommon.Desc("Scale relative to the player. Applies to the next car.", new AcceptableValueRange<float>(0.5f, 3f), true));
            RamBoost = Config.Bind("Car", "Ram launches bots", true, ModCommon.Desc("Hitting a bot sends it flying.", advanced: true));
            ShowDriverInModel = Config.Bind("Car", "Show driver", true, ModCommon.Desc("Show your ragdoll sitting in the car.", advanced: true));
            CrashPhysics = Config.Bind("Crash", "Crash damage", true, "Dents, sparks, debris, broken glass, smoke and fire.");
            Eject = Config.Bind("Crash", "Eject driver", true, "Huge crashes throw you through the windscreen.");
            DamageMul = Config.Bind("Crash", "Damage multiplier", 1f, ModCommon.Desc("How easily the car deforms and breaks.", new AcceptableValueRange<float>(0.1f, 5f), true));
            EjectSpeed = Config.Bind("Crash", "Eject impact speed", 170f, ModCommon.Desc("Impact speed that ejects the driver.", new AcceptableValueRange<float>(60f, 500f), true));
            EnterKey = Config.Bind("Keys", "Enter / exit car", new KeyboardShortcut(KeyCode.E), "Spawn the car if needed and get in, or get out.");
            FlipKey = Config.Bind("Keys", "Flip car", new KeyboardShortcut(KeyCode.R), "While driving: back onto the wheels.");
            ResetKey = Config.Bind("Keys", "Reset car", new KeyboardShortcut(KeyCode.Backspace), "Car next to you, upright, stopped and repaired.");
            Enabled.SettingChanged += (_, __) => { if (!Enabled.Value) DespawnCar(); };

            MenuRegistry.Action(GUID, "Drive / exit", ToggleDrive);
            MenuRegistry.Action(GUID, "Reset", ResetCar);
            MenuRegistry.Action(GUID, "Repair", () => { if (car) car.Repair(); });
            MenuRegistry.Action(GUID, "New car", () => SpawnCar(false));
            MenuRegistry.Action(GUID, "Despawn", DespawnCar);
        }

        public void Toast(string msg) => ModCommon.Toast(msg);

        private void Update()
        {
            if (!ModCommon.InRound)
            {
                if (CheeseApi.Driver) { CheeseApi.Driver = null; CheeseApi.DriverVehicle = null; }
                return;
            }
            if (!Enabled.Value || ModCommon.Paused || CheeseApi.MenuOpen) return;
            if (EnterKey.Value.IsDown()) ToggleDrive();
            if (car && car.driver && ModCommon.IsLocal(car.driver) && FlipKey.Value.IsDown()) car.Flip();
            if (ResetKey.Value.IsDown()) ResetCar();
        }

        public void ToggleDrive()
        {
            if (!ModCommon.Active) { Toast("Mods only work in Play Offline."); return; }
            var me = ModCommon.LocalRagdoll();
            if (!me) return;
            if (car && car.driver == me) { car.Exit(); return; }
            if (CheeseApi.IsSeated(me)) { Toast("Get up first."); return; }
            if (!car) car = CheeseCar.Spawn(me, CarSize.Value);
            else if (Vector3.Distance(car.transform.position, me.GetRootPosition()) > ModCommon.BodyScale(me) * 6f) car.Teleport(me);
            car.Enter(me);
        }

        public void ResetCar()
        {
            var me = ModCommon.LocalRagdoll();
            if (!me) return;
            if (!car) { car = CheeseCar.Spawn(me, CarSize.Value); return; }
            car.ResetCar(car.driver == me ? null : me);
        }

        public void SpawnCar(bool drive)
        {
            var me = ModCommon.LocalRagdoll();
            if (!me) return;
            DespawnCar();
            car = CheeseCar.Spawn(me, CarSize.Value);
            if (drive) car.Enter(me);
        }

        public void DespawnCar()
        {
            if (!car) return;
            if (car.driver) car.Exit();
            Destroy(car.gameObject);
        }

        private void OnGUI()
        {
            if (!car || !car.driver || !ModCommon.IsLocal(car.driver)) return;
            var st = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight, normal = { textColor = Color.white } };
            string dmg = car.Damage > 0.05f ? $"  ·  damage {car.Damage * 100f:0}%" : "";
            GUI.Label(new Rect(Screen.width - 520, Screen.height - 80, 500, 60),
                $"{car.Speed:F0} u/s{dmg}\n<size=13>WASD drive · Space brake · Shift nitro · H horn · {FlipKey.Value} flip · {ResetKey.Value} reset · {EnterKey.Value} exit</size>", st);
        }
    }
}
