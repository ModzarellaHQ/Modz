using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Modz
{
    [BepInPlugin(GUID, "Gore", "2.1.0")]
    [BepInDependency(CorePlugin.GUID)]
    public class GorePlugin : BaseUnityPlugin
    {
        public const string GUID = "modz.gore";
        internal static ManualLogSource Log;
        public static GorePlugin Instance;

        public ConfigEntry<bool> Enabled;
        public ConfigEntry<float> BleedImpact;
        public ConfigEntry<float> SeverImpact;
        public ConfigEntry<bool> Accumulate;
        public ConfigEntry<float> StumpBleedSeconds;
        public ConfigEntry<float> BloodAmount;
        public ConfigEntry<bool> GoreOnPlayer;
        public ConfigEntry<bool> GoreOnBots;
        public ConfigEntry<bool> AllowHead;
        public ConfigEntry<bool> AllowUpperLimbs;
        public ConfigEntry<float> GibLifetime;
        public ConfigEntry<bool> LogImpacts;
        public ConfigEntry<bool> Overkill;
        public ConfigEntry<bool> CarGore;
        public ConfigEntry<bool> Lethal;
        public ConfigEntry<bool> LethalForYou;
        public ConfigEntry<float> BleedOut;
        public ConfigEntry<float> GoreVolume;
        public ConfigEntry<bool> BloodyBodies;
        public ConfigEntry<bool> TorsoTear;
        public ConfigEntry<bool> ScreenBlood;

        private readonly Dictionary<ActiveRagdoll, RagdollGore> state = new Dictionary<ActiveRagdoll, RagdollGore>();
        internal Material bloodMat, decalMat;
        internal Material[] bloodTints;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Limbs bleed and come off on hard impacts (offline mode only).");
            BloodAmount = Config.Bind("Gore", "Blood amount", 3f, ModCommon.Desc("How much blood everything sprays.", new AcceptableValueRange<float>(0f, 8f)));
            LethalForYou = Config.Bind("Gore", "You can die", true, "Bleeding out, decapitation or being torn apart kills you (Enter = next round).");
            Lethal = Config.Bind("Gore", "Bots can die", true, "Bots die from the same things and go limp.");
            GoreOnPlayer = Config.Bind("Gore", "Gore on you", true, "Your own limbs can come off.");
            GoreOnBots = Config.Bind("Gore", "Gore on bots", true, "Bots' limbs can come off.");
            ScreenBlood = Config.Bind("Gore", "Screen blood", true, "Blood splatters on the screen when gore happens near the camera.");
            GoreVolume = Config.Bind("Gore", "Gore volume", 0.8f, ModCommon.Desc("Squelches, cracks and splats.", new AcceptableValueRange<float>(0f, 1f)));

            BleedImpact = Config.Bind("Gore", "Bleed impact", 75f, ModCommon.Desc("Impact speed at which a limb starts bleeding (game units/s).", new AcceptableValueRange<float>(20f, 500f), true));
            SeverImpact = Config.Bind("Gore", "Sever impact", 150f, ModCommon.Desc("Single impact speed that tears a limb off.", new AcceptableValueRange<float>(30f, 800f), true));
            BleedOut = Config.Bind("Gore", "Blood until death", 30f, ModCommon.Desc("Blood a body holds before dying (a stump loses ~1/s).", new AcceptableValueRange<float>(3f, 100f), true));
            StumpBleedSeconds = Config.Bind("Gore", "Stump bleed seconds", 25f, ModCommon.Desc("How long a stump spurts.", new AcceptableValueRange<float>(1f, 60f), true));
            Accumulate = Config.Bind("Gore", "Damage accumulates", true, ModCommon.Desc("Repeated hits eventually tear the limb off too.", advanced: true));
            AllowHead = Config.Bind("Gore", "Decapitation", true, ModCommon.Desc("The head can come off.", advanced: true));
            AllowUpperLimbs = Config.Bind("Gore", "Whole limbs", true, ModCommon.Desc("Upper arms/legs can come off (taking everything below).", advanced: true));
            Overkill = Config.Bind("Gore", "Overkill", true, ModCommon.Desc("Extreme impacts rip off extra limbs, burst heads and can explode whole bodies.", advanced: true));
            TorsoTear = Config.Bind("Gore", "Tear in half", true, ModCommon.Desc("Colossal impacts rip the upper body off the hips.", advanced: true));
            CarGore = Config.Bind("Gore", "Car gore", true, ModCommon.Desc("Vehicles crush limbs, get splattered, leave bloody tyre tracks and squish gibs.", advanced: true));
            BloodyBodies = Config.Bind("Gore", "Bloody bodies", true, ModCommon.Desc("Bodies get stained red and show wounds.", advanced: true));
            GibLifetime = Config.Bind("Gore", "Severed limb lifetime", 45f, ModCommon.Desc("Seconds before a lost limb despawns.", new AcceptableValueRange<float>(5f, 300f), true));
            LogImpacts = Config.Bind("Gore", "Log impacts", false, ModCommon.Desc("Debug: log every limb impact over the bleed threshold.", advanced: true));

            CheeseApi.BulletHit = Shot;
            CheeseApi.DeadCheck = IsDead;

            bloodMat = ModCommon.UnlitMaterial(ModCommon.BlobTexture(32, 0f, 5));
            decalMat = ModCommon.UnlitMaterial(ModCommon.BlobTexture(128, 0.9f, 11));
            bloodTints = new Material[4];
            for (int i = 0; i < 4; i++)
                bloodTints[i] = new Material(decalMat) { color = Color.Lerp(new Color(0.45f, 0f, 0.02f, 0.92f), new Color(0.25f, 0f, 0.01f, 0.95f), i / 3f) };

            new Harmony(GUID).PatchAll(typeof(GorePatches));
            gameObject.AddComponent<CarGore>();
            Log.LogInfo("Gore loaded");
        }

        private readonly List<(RagdollGore gore, RagdollPart part, Vector3 vel)> pendingSevers = new List<(RagdollGore, RagdollPart, Vector3)>();
        internal void QueueSever(RagdollGore g, RagdollPart p, Vector3 v) => pendingSevers.Add((g, p, v));
        private readonly List<(RagdollGore g, Vector3 p, Vector3 v)> pendingExplodes = new List<(RagdollGore, Vector3, Vector3)>();
        internal void QueueExplode(RagdollGore g, Vector3 p, Vector3 v) => pendingExplodes.Add((g, p, v));

        private void FixedUpdate()
        {
            if (pendingExplodes.Count > 0)
            {
                var ex = pendingExplodes.ToArray();
                pendingExplodes.Clear();
                foreach (var (g, p, v) in ex) g.Explode(p, v);
            }
            if (pendingSevers.Count == 0) return;
            var batch = pendingSevers.ToArray();
            pendingSevers.Clear();
            foreach (var (g, p, v) in batch)
                if (p && !g.IsSevered(p)) g.Sever(p, v);
        }

        private Texture2D vignette;
        private AudioSource heart;
        private static AudioClip heartClip;

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            Modz.ScreenBlood.Draw();
            if (!Enabled.Value || !ModCommon.InRound) return;
            var me = ModCommon.LocalRagdoll();
            if (!me || !state.TryGetValue(me, out var g)) return;
            float lost = Mathf.Clamp01(g.BloodLost / BleedOut.Value);
            if (!vignette) vignette = Vignette();
            if (lost > 0.05f || g.Dead)
            {
                float pulse = lost > 0.5f ? 0.1f * Mathf.Max(0f, Mathf.Sin(Time.time * (4f + lost * 6f))) : 0f;
                GUI.color = new Color(0.5f, 0f, 0f, Mathf.Clamp01(lost * 0.55f + pulse + (g.Dead ? 0.5f : 0f)));
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), vignette);
                GUI.color = Color.white;
                if (!g.Dead)
                {
                    var bg = new Rect(20, Screen.height - 46, 220, 14);
                    GUI.color = new Color(0, 0, 0, 0.6f); GUI.DrawTexture(bg, Texture2D.whiteTexture);
                    GUI.color = Color.Lerp(new Color(0.9f, 0.1f, 0.1f), new Color(0.35f, 0f, 0f), lost);
                    GUI.DrawTexture(new Rect(bg.x + 2, bg.y + 2, (bg.width - 4) * (1f - lost), bg.height - 4), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                    GUI.Label(new Rect(bg.x, bg.y - 18, 300, 18), lost > 0.6f ? "<b>BLEEDING OUT</b>" : "Blood");
                }
            }
            if (g.Dead)
            {
                var big = new GUIStyle(GUI.skin.label) { fontSize = 54, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.85f, 0.05f, 0.05f) } };
                var sub = new GUIStyle(big) { fontSize = 20, normal = { textColor = Color.white } };
                GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 80), ("you " + (g.DeathReason == "decapitated" ? "lost your head" : g.DeathReason == "torn in half" ? "were torn in half" : g.DeathReason == "blown to pieces" ? "were blown to pieces" : "bled out")).ToUpperInvariant(), big);
                GUI.Label(new Rect(0, Screen.height * 0.35f + 80, Screen.width, 40), "Press Enter for the next round", sub);
            }
        }

        private static Texture2D Vignette()
        {
            const int n = 128;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x / (n - 1f) * 2f - 1f, dy = y / (n - 1f) * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.414f;
                t.SetPixel(x, y, new Color(1, 1, 1, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 1f, d))));
            }
            t.Apply();
            return t;
        }

        private void LateUpdate()
        {
            if (!Enabled.Value || !ModCommon.InRound) { if (heart) heart.volume = 0f; return; }
            var me = ModCommon.LocalRagdoll();
            RagdollGore g = null;
            if (me) state.TryGetValue(me, out g);
            if (!heart)
            {
                heartClip = heartClip ? heartClip : GoreAudio.Heartbeat();
                heart = gameObject.AddComponent<AudioSource>();
                heart.clip = heartClip; heart.loop = true; heart.spatialBlend = 0f; heart.volume = 0f; heart.Play();
            }
            float lost = g != null ? Mathf.Clamp01(g.BloodLost / BleedOut.Value) : 0f;
            heart.volume = g != null && !g.Dead && lost > 0.35f ? Mathf.InverseLerp(0.35f, 1f, lost) * GoreVolume.Value * ModCommon.GameSfxVolume * 3f : 0f;
            heart.pitch = 0.9f + lost * 0.7f;
            if (g != null && g.Dead && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.enterKey.wasPressedThisFrame)
                GameManager.Instance.LoadRandomMap();

            foreach (var kv in state)
            {
                if (!kv.Key || kv.Value.Dead || ModCommon.IsLocal(kv.Key)) continue;
                float f = kv.Value.BloodLost / BleedOut.Value;
                if (f > 0.5f && Random.value < Time.deltaTime * f * 0.8f) ModCommon.Unground(kv.Key, true);
            }
        }

        private void Update()
        {
            if (state.Count == 0) return;
            var dead = new List<ActiveRagdoll>();
            foreach (var kv in state) if (!kv.Key) dead.Add(kv.Key);
            foreach (var k in dead) state.Remove(k);
        }

        internal void ForceHit(RagdollPart part, float impact, Vector3 point, Vector3 dir)
        {
            if (!part || !part.ragdoll || !ModCommon.InRound) return;
            var r = part.ragdoll;
            if (ModCommon.IsLocal(r) ? !GoreOnPlayer.Value : !GoreOnBots.Value) return;
            if (impact < BleedImpact.Value) return;
            Get(r).Hit(part, impact, point, dir);
        }

        public void Shot(RagdollPart part, Vector3 point, Vector3 dir, float power)
        {
            if (!Enabled.Value || !part || !part.ragdoll || !ModCommon.InRound) return;
            var r = part.ragdoll;
            if (ModCommon.IsLocal(r) ? !GoreOnPlayer.Value : !GoreOnBots.Value) return;
            var g = Get(r);
            if (g.IsSevered(part)) return;
            float sc = ModCommon.BodyScale(r);
            Blood.Burst(point, -dir, sc, 0.3f);
            Blood.Burst(point + dir * sc * 0.15f, dir, sc, 0.7f + power / 400f);
            g.Wound(part, point, dir);
            g.Bleed(part, 30f, 0.9f);
            g.LoseBlood(BleedOut.Value * (part == r.head ? 0.5f : part == r.spine1 || part == r.spine2 ? 0.14f : 0.06f));
            GoreAudio.Play(GoreAudio.Splat, point, 0.8f);
            if (part == r.head && power > 120f)
            {
                g.QueueHeadBurst();
                QueueSever(g, part, dir * power);
                return;
            }
            g.Hit(part, power * 0.9f, point, dir * power);
        }

        internal bool IsDead(ActiveRagdoll r) => r && state.TryGetValue(r, out var g) && g.Dead;

        internal RagdollGore Get(ActiveRagdoll r)
        {
            if (!state.TryGetValue(r, out var g))
            {
                g = new RagdollGore(r);
                state[r] = g;
            }
            return g;
        }

        public static bool DebugSever(ActiveRagdoll r, string limb)
        {
            foreach (var p in r.GetRagdollParts())
            {
                if (!p || Limbs.Name(r, p) != limb) continue;
                var g = Instance.Get(r);
                if (g.IsSevered(p)) return false;
                g.Sever(p, Vector3.up * 50f);
                return true;
            }
            return false;
        }

        internal void OnImpact(RagdollPart part, Collision collision)
        {
            if (!Enabled.Value || !ModCommon.InRound || !part || !part.ragdoll || !part.ragdoll.active) return;
            var r = part.ragdoll;
            bool local = ModCommon.IsLocal(r);
            if (local ? !GoreOnPlayer.Value : !GoreOnBots.Value) return;

            int layer = collision.gameObject.layer;
            bool isCar = CheeseApi.IsVehicle(collision.rigidbody);
            if (isCar && CheeseApi.IsSeated(r)) return;
            if (!isCar && layer != LayerMask.NameToLayer("Ground") && layer != LayerMask.NameToLayer("Obstacle")) return;
            if (collision.contactCount == 0) return;
            var gm = GameManager.Instance;
            if (ModCommon.CountingDown || gm.roundTime < gm.countdownLength + 1.5f) return;

            var contact = collision.GetContact(0);
            float impact = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
            bool extremity = part == r.footLeft || part == r.footRight || part == r.handLeft || part == r.handRight || part == r.lowerLegLeft || part == r.lowerLegRight;
            if (extremity && !isCar) impact /= 1.6f;
            if (impact < BleedImpact.Value) return;
            if (LogImpacts.Value) Log.LogInfo($"{r.name} {Limbs.Name(r, part)} impact {impact:F0}");

            if (isCar && CarGore.Value)
            {
                impact *= 1.4f; // bumpers are worse than grass
                CarGoreFx.Splatter(collision.rigidbody, contact.point, ModCommon.BodyScale(r));
            }
            Get(r).Hit(part, impact, contact.point, collision.relativeVelocity);
        }
    }

    internal static class GorePatches
    {
        private static void Drive(float drive, float damper, params RagdollPart[] parts)
        {
            var jd = new JointDrive { positionSpring = drive, positionDamper = damper, maximumForce = float.PositiveInfinity };
            foreach (var p in parts) if (p && p.joint) p.joint.slerpDrive = jd;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(ActiveRagdoll), "SetBodyDrive")]
        private static bool SafeBody(ActiveRagdoll __instance, float drive, float damper)
        { Drive(drive, damper, __instance.spine1, __instance.spine2, __instance.head); return false; }

        [HarmonyPrefix, HarmonyPatch(typeof(ActiveRagdoll), "SetFeetDrive")]
        private static bool SafeFeet(ActiveRagdoll __instance, float drive, float damper)
        { Drive(drive, damper, __instance.footLeft, __instance.footRight); return false; }

        [HarmonyPrefix, HarmonyPatch(typeof(ActiveRagdoll), "SetArmDrive")]
        private static bool SafeArms(ActiveRagdoll __instance, float drive, float damper)
        { Drive(drive, damper, __instance.lowerArmLeft, __instance.lowerArmRight, __instance.handLeft, __instance.handRight); return false; }

        [HarmonyPostfix, HarmonyPatch(typeof(RagdollPart), "OnCollisionEnter")]
        private static void PartHit(RagdollPart __instance, Collision collision)
        {
            try { GorePlugin.Instance.OnImpact(__instance, collision); }
            catch (System.Exception e) { GorePlugin.Log.LogError(e); }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(RagdollHand), "OnCollisionEnter")]
        private static void HandHit(RagdollHand __instance, Collision collision)
        {
            try { GorePlugin.Instance.OnImpact(__instance, collision); }
            catch (System.Exception e) { GorePlugin.Log.LogError(e); }
        }
    }

    internal static class Limbs
    {
        public static string Name(ActiveRagdoll r, RagdollPart p)
        {
            if (p == r.head) return "head";
            if (p == r.upperArmLeft) return "left upper arm";
            if (p == r.lowerArmLeft) return "left forearm";
            if (p == r.handLeft) return "left hand";
            if (p == r.upperArmRight) return "right upper arm";
            if (p == r.lowerArmRight) return "right forearm";
            if (p == r.handRight) return "right hand";
            if (p == r.upperLegLeft) return "left thigh";
            if (p == r.lowerLegLeft) return "left shin";
            if (p == r.footLeft) return "left foot";
            if (p == r.upperLegRight) return "right thigh";
            if (p == r.lowerLegRight) return "right shin";
            if (p == r.footRight) return "right foot";
            if (p == r.spine2) return "chest";
            return "pelvis";
        }

        public static bool Severable(ActiveRagdoll r, RagdollPart p)
        {
            if (p == r.spine1 || p == r.spine2) return false;
            if (p == r.head) return GorePlugin.Instance.AllowHead.Value;
            if (p == r.upperArmLeft || p == r.upperArmRight || p == r.upperLegLeft || p == r.upperLegRight)
                return GorePlugin.Instance.AllowUpperLimbs.Value;
            return true;
        }

        public static RagdollPart Parent(RagdollPart p)
        {
            var t = p.transform.parent;
            while (t)
            {
                var rp = t.GetComponent<RagdollPart>();
                if (rp) return rp;
                t = t.parent;
            }
            return null;
        }
    }

    internal class RagdollGore
    {
        private readonly ActiveRagdoll r;
        private readonly Dictionary<RagdollPart, float> damage = new Dictionary<RagdollPart, float>();
        private readonly HashSet<RagdollPart> severed = new HashSet<RagdollPart>();
        private readonly Dictionary<RagdollPart, Bleeder> bleeders = new Dictionary<RagdollPart, Bleeder>();
        private float lastSever;

        private bool headBurst;
        public bool Dead { get; private set; }
        public string DeathReason = "";
        private readonly List<GameObject> wounds = new List<GameObject>();
        private bool exploded;

        public void QueueHeadBurst() => headBurst = true;

        public void Wound(RagdollPart part, Vector3 point, Vector3 dir)
        {
            if (!GorePlugin.Instance.BloodyBodies.Value || !part) return;
            float sc = ModCommon.BodyScale(r);
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ModCommon.StripCollider(q);
            q.name = "GoreWound";
            Vector3 outward = point - part.transform.position;
            outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : -dir.normalized;
            q.transform.SetParent(part.transform, true);
            q.transform.position = point + outward * sc * 0.01f;
            q.transform.rotation = Quaternion.LookRotation(-outward) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
            float ls = Mathf.Max(0.001f, part.transform.lossyScale.x);
            float sz = sc * Random.Range(0.12f, 0.22f) / ls;
            q.transform.localScale = new Vector3(sz, sz * Random.Range(0.7f, 1.2f), 1f);
            q.GetComponent<Renderer>().sharedMaterial = GorePlugin.Instance.bloodTints[3];
            wounds.Add(q);
            if (wounds.Count > 25) { if (wounds[0]) Object.Destroy(wounds[0]); wounds.RemoveAt(0); }
        }

        public void Explode(Vector3 point, Vector3 vel)
        {
            if (exploded || !r) return;
            exploded = true;
            float sc = ModCommon.BodyScale(r);
            Blood.Burst(point, Vector3.up, sc, 6f);
            Blood.Burst(point, vel.normalized, sc, 4f);
            GoreAudio.Play(GoreAudio.Squelch, point, 1f);
            GoreAudio.Play(GoreAudio.Crack, point, 1f);
            headBurst = true;
            foreach (var p in new RagdollPart[] { r.head, r.upperArmLeft, r.upperArmRight, r.upperLegLeft, r.upperLegRight, r.spine2 })
                if (p && !severed.Contains(p)) GorePlugin.Instance.QueueSever(this, p, vel + Random.onUnitSphere * sc * 4f);
            Die("blown to pieces");
        }
        public float BloodLost;
        private float stain;
        private readonly List<Material> stainMats = new List<Material>();
        private readonly List<Color> stainBase = new List<Color>();

        public RagdollGore(ActiveRagdoll r) { this.r = r; }

        private float BleedFrom(float dmg) => Mathf.Clamp(dmg, 0f, 2f) * GorePlugin.Instance.BleedOut.Value * 0.04f;

        public void LoseBlood(float amount)
        {
            if (Dead) return;
            BloodLost += amount;
            if (BloodLost >= GorePlugin.Instance.BleedOut.Value) Die("bled out");
        }

        public void Die(string why)
        {
            var cfg = GorePlugin.Instance;
            if (Dead || !r) return;
            if (ModCommon.IsLocal(r) ? !cfg.LethalForYou.Value : !cfg.Lethal.Value) return;
            DeathReason = why;
            if (CheeseApi.IsSeated(r)) return;
            Dead = true;
            ModCommon.Unground(r, true);
            r.moveDirection = Vector3.zero;
            var limp = new JointDrive { positionSpring = 0f, positionDamper = 2f, maximumForce = float.PositiveInfinity };
            foreach (var p in r.GetRagdollParts())
            {
                if (p && p.joint) p.joint.slerpDrive = limp;
                if (p is RagdollHand h) h.BreakHold();
            }
            GoreAudio.Play(GoreAudio.Crack, r.GetRootPosition(), 0.6f);
            GorePlugin.Log.LogInfo($"{(ModCommon.IsLocal(r) ? "YOU" : r.name)} died ({why})");
            if (ModCommon.IsLocal(r)) ModCommon.Toast("You died (" + why + ")");
        }

        public void Stain(float add)
        {
            if (!GorePlugin.Instance.BloodyBodies.Value || !r) return;
            if (stainMats.Count == 0)
                foreach (var rend in r.GetComponentsInChildren<Renderer>())
                {
                    if (!rend.enabled || rend is ParticleSystemRenderer || !(rend is SkinnedMeshRenderer || rend is MeshRenderer)) continue;
                    foreach (var m in rend.materials) // instances, per ragdoll
                        if (m.HasProperty("_Color")) { stainMats.Add(m); stainBase.Add(m.color); }
                }
            stain = Mathf.Clamp01(stain + add);
            var blood = new Color(0.45f, 0.02f, 0.02f);
            for (int i = 0; i < stainMats.Count; i++)
                if (stainMats[i]) stainMats[i].color = Color.Lerp(stainBase[i], stainBase[i] * blood * 1.6f, stain * 0.85f);
        }

        private RagdollPart RandomIntactLimb(RagdollPart except)
        {
            var c = new List<RagdollPart>();
            foreach (var p in r.GetRagdollParts())
                if (p && p != except && p != r.head && !severed.Contains(p) && Limbs.Severable(r, p)) c.Add(p);
            return c.Count > 0 ? c[Random.Range(0, c.Count)] : null;
        }

        public bool IsSevered(RagdollPart p) => severed.Contains(p);

        public void Hit(RagdollPart part, float impact, Vector3 point, Vector3 relVel)
        {
            var cfg = GorePlugin.Instance;
            if (severed.Contains(part)) return;
            float bleed = cfg.BleedImpact.Value, sever = Mathf.Max(cfg.SeverImpact.Value, bleed + 1f);
            float dmg = (impact - bleed) / (sever - bleed) * 0.8f;
            damage.TryGetValue(part, out float total);
            total = cfg.Accumulate.Value ? total + dmg : Mathf.Max(total, dmg);
            damage[part] = total;

            if (cfg.Overkill.Value && impact >= sever * 3f) { GorePlugin.Instance.QueueExplode(this, point, relVel); return; }
            Blood.Burst(point, -relVel.normalized, ModCommon.BodyScale(r), Mathf.Clamp(0.5f + dmg * 1.5f, 0.5f, 3f));
            if (dmg > 0.3f) Wound(part, point, relVel);
            LoseBlood(BleedFrom(dmg));
            Stain(0.04f + dmg * 0.12f);
            GoreAudio.Play(dmg > 0.5f ? GoreAudio.Crack : GoreAudio.Splat, point, Mathf.Clamp(0.3f + dmg, 0.3f, 1f));
            Bleed(part, Mathf.Clamp(2f + dmg * 4f, 2f, 7f), Mathf.Clamp01(0.3f + dmg));

            if (part == r.spine2 && cfg.TorsoTear.Value && impact >= sever * 2.2f && Time.time - lastSever > 0.12f)
            {
                lastSever = Time.time;
                GorePlugin.Instance.QueueSever(this, part, relVel);
                return;
            }
            bool canSever = Limbs.Severable(r, part) && (impact >= sever || total >= 1f);
            if (canSever && Time.time - lastSever > 0.12f)
            {
                lastSever = Time.time;
                GorePlugin.Instance.QueueSever(this, part, relVel);
                if (cfg.Overkill.Value && impact >= sever * 1.6f)
                {
                    if (part == r.head) headBurst = true;
                    var extra = RandomIntactLimb(part);
                    if (extra) GorePlugin.Instance.QueueSever(this, extra, relVel);
                }
            }
        }

        public void Bleed(RagdollPart part, float seconds, float intensity)
        {
            if (!bleeders.TryGetValue(part, out var b) || !b)
            {
                b = Bleeder.Attach(part.transform, part.transform.position, ModCommon.BodyScale(r), false, this);
                bleeders[part] = b;
            }
            b.Refresh(seconds, intensity);
        }

        public void Sever(RagdollPart part, Vector3 relVel)
        {
            var parent = Limbs.Parent(part);
            if (!parent) return;
            float scale = ModCommon.BodyScale(r);
            var sub = new List<RagdollPart>(part.GetComponentsInChildren<RagdollPart>(true));
            if (!sub.Contains(part)) sub.Add(part);

            Vector3 vel = part.rigidBody ? part.rigidBody.velocity : Vector3.zero;
            Vector3 angVel = part.rigidBody ? part.rigidBody.angularVelocity : Vector3.zero;
            Vector3 jointPos = part.transform.position;

            foreach (var p in sub)
                if (p is RagdollHand h) h.BreakHold();

            bool burst = headBurst && part == r.head;
            headBurst = false;
            var gib = burst ? null : Gib.Create(r, part, sub, GorePlugin.Instance.GibLifetime.Value);
            if (burst) Blood.Burst(jointPos, Vector3.up, scale, 5f);
            if (gib)
            {
                var rb = gib.GetComponent<Rigidbody>();
                Vector3 pop = (Random.onUnitSphere + Vector3.up).normalized * scale * 2.5f;
                rb.velocity = vel + pop;
                rb.angularVelocity = angVel + Random.insideUnitSphere * 12f;
                foreach (var gc in gib.GetComponentsInChildren<Collider>())
                foreach (var p in r.GetRagdollParts())
                foreach (var pc in p.GetComponents<Collider>())
                    Physics.IgnoreCollision(gc, pc);
            }

            foreach (var p in sub)
            {
                severed.Add(p);
                if (p.joint) Object.DestroyImmediate(p.joint);
                foreach (var c in p.GetComponents<Collider>()) c.enabled = false;
                if (p.rigidBody)
                {
                    p.rigidBody.velocity = Vector3.zero;
                    p.rigidBody.isKinematic = true;
                    p.rigidBody.detectCollisions = false;
                }
                p.touchingGround = false;
                if (bleeders.TryGetValue(p, out var b) && b) Object.Destroy(b.gameObject);
                bleeders.Remove(p);
            }
            part.transform.localScale = Vector3.one * 0.001f;

            GoreAudio.Play(GoreAudio.Squelch, jointPos, 1f);
            GoreAudio.Play(GoreAudio.Crack, jointPos, 0.8f);
            Stain(0.2f);
            LoseBlood(GorePlugin.Instance.BleedOut.Value * (part == r.head ? 1f : 0.18f));
            if (part == r.head) Die("decapitated");
            if (part == r.spine2) Die("torn in half");

            var stump = Bleeder.Attach(parent.transform, jointPos, scale, true, this);
            stump.Refresh(GorePlugin.Instance.StumpBleedSeconds.Value, 1f);
            Blood.Burst(jointPos, Random.onUnitSphere, scale, 3.5f);

            GorePlugin.Log.LogInfo($"{(ModCommon.IsLocal(r) ? "YOU" : r.name)} lost {Limbs.Name(r, part)}");
            if (ModCommon.IsLocal(r)) StageManager.Instance.cameraRig.SetScreenShake(1.2f, 2f);
        }
    }

    internal partial class Bleeder : MonoBehaviour
    {
        private ParticleSystem ps;
        private float until, intensity, scale, nextDecal;
        private bool stump;
        private RagdollGore owner;
        private GameObject pool;
        private float poolSize;

        public static Bleeder Attach(Transform bone, Vector3 worldPos, float scale, bool stump, RagdollGore owner = null)
        {
            var go = new GameObject(stump ? "GoreStumpBleeder" : "GoreBleeder");
            go.transform.SetParent(bone, false);
            go.transform.position = worldPos;
            var b = go.AddComponent<Bleeder>();
            b.scale = scale;
            b.stump = stump;
            b.ps = Blood.MakeSystem(go, scale, true);
            b.owner = owner;
            return b;
        }

        public void Refresh(float seconds, float intensity)
        {
            until = Mathf.Max(until, Time.time + seconds);
            this.intensity = Mathf.Max(this.intensity, intensity);
        }

        private void Update()
        {
            float left = until - Time.time;
            var em = ps.emission;
            if (left <= 0f)
            {
                em.rateOverTime = 0f;
                if (ps.particleCount == 0) Destroy(gameObject);
                return;
            }
            float fade = Mathf.Clamp01(left / 2f);
            owner?.LoseBlood((stump ? 1f : 0.25f) * intensity * fade * Time.deltaTime);
            GrowPool(fade);
            float beat = stump ? Mathf.Pow(Mathf.Max(0f, Mathf.Sin(Time.time * 7.5f)), 4f) : 0.5f;
            em.rateOverTime = (stump ? 170f * beat + 18f : 30f) * intensity * fade * GorePlugin.Instance.BloodAmount.Value;
            var main = ps.main;
            float speed = stump ? scale * Mathf.Lerp(2f, 7.5f, beat) : scale * 1f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.6f, speed);
            if (stump && transform.parent)
            {
                Vector3 outward = transform.position - transform.parent.position;
                if (outward.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(outward);
            }

            if (Time.time > nextDecal)
            {
                nextDecal = Time.time + (stump ? 0.3f : 0.7f) / Mathf.Max(0.2f, intensity);
                Blood.Decal(transform.position, scale * Random.Range(0.25f, stump ? 0.7f : 0.45f));
            }
        }
    }

    internal partial class Bleeder
    {
        private void GrowPool(float fade)
        {
            if (!stump && intensity < 0.5f) return;
            var rbody = transform.parent ? transform.parent.GetComponentInParent<Rigidbody>() : null;
            if (rbody && rbody.velocity.sqrMagnitude > 16f) { pool = null; return; }
            if (!pool)
            {
                if (!Physics.Raycast(transform.position + Vector3.up * scale * 0.5f, Vector3.down, out var hit, scale * 4f, ModCommon.GroundMask, QueryTriggerInteraction.Ignore) || hit.rigidbody) return;
                pool = GameObject.CreatePrimitive(PrimitiveType.Quad);
                ModCommon.StripCollider(pool);
                pool.name = "GorePool";
                pool.transform.position = hit.point + hit.normal * 0.03f;
                pool.transform.rotation = Quaternion.LookRotation(-hit.normal) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
                var rend = pool.GetComponent<Renderer>();
                rend.sharedMaterial = GorePlugin.Instance.bloodTints[3];
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                poolSize = scale * 0.2f;
                Object.Destroy(pool, 60f);
            }
            poolSize = Mathf.Min(scale * (stump ? 2f : 1f), poolSize + scale * 0.3f * fade * Time.deltaTime);
            pool.transform.localScale = new Vector3(poolSize, poolSize * 0.85f, 1f);
        }
    }

    internal static class GoreAudio
    {
        private const int Rate = 44100;
        private static AudioClip squelch, crack, splat;
        public static AudioClip Squelch => squelch ? squelch : (squelch = Make(0));

        public static AudioClip Heartbeat()
        {
            var d = new float[Rate];
            for (int i = 0; i < Rate; i++)
            {
                float t = (float)i / Rate, v = 0f;
                foreach (var (at, f, a) in new[] { (0.0f, 55f, 1f), (0.28f, 48f, 0.7f) })
                    if (t >= at) { float tt = t - at; v += Mathf.Sin(2f * Mathf.PI * f * tt) * Mathf.Exp(-tt * 22f) * a; }
                d[i] = (float)System.Math.Tanh(v * 1.5f) * 0.9f;
            }
            var c = AudioClip.Create("heart", Rate, 1, Rate, false); c.SetData(d, 0); return c;
        }
        public static AudioClip Crack => crack ? crack : (crack = Make(1));
        public static AudioClip Splat => splat ? splat : (splat = Make(2));

        public static void Play(AudioClip clip, Vector3 pos, float vol)
        {
            float v = vol * GorePlugin.Instance.GoreVolume.Value * ModCommon.GameSfxVolume * 2f;
            if (v <= 0.01f) return;
            var go = new GameObject("GoreSound");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip; src.volume = v; src.pitch = Random.Range(0.85f, 1.15f);
            src.spatialBlend = 0.8f; src.minDistance = 20f; src.maxDistance = 400f; src.rolloffMode = AudioRolloffMode.Linear;
            src.Play();
            Object.Destroy(go, clip.length / src.pitch + 0.1f);
        }

        private static AudioClip Make(int kind)
        {
            var rng = new System.Random(100 + kind);
            float len = kind == 0 ? 0.55f : kind == 1 ? 0.18f : 0.3f;
            int n = (int)(Rate * len);
            var d = new float[n];
            float lp = 0f, y1 = 0f, y2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float x = (float)rng.NextDouble() * 2f - 1f;
                float v;
                if (kind == 0)
                {
                    float f = Mathf.Lerp(1400f, 180f, Mathf.Sqrt(t / len));
                    float w = 2f * Mathf.PI * f / Rate, r = 0.97f;
                    float y = x * 0.2f + 2f * r * Mathf.Cos(w) * y1 - r * r * y2; y2 = y1; y1 = y;
                    float bubbles = 0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 23f * t) * Mathf.Sin(2f * Mathf.PI * 7f * t);
                    v = y * bubbles * Mathf.Exp(-t * 5f) * Mathf.Min(1f, t * 80f);
                }
                else if (kind == 1)
                {
                    float click = t < 0.003f ? x : 0f;
                    v = click * 1.5f + Mathf.Sin(2f * Mathf.PI * 1800f * t) * Mathf.Exp(-t * 60f) * 0.6f + x * Mathf.Exp(-t * 90f) * 0.8f
                        + Mathf.Sin(2f * Mathf.PI * 420f * t) * Mathf.Exp(-t * 35f) * 0.4f;
                }
                else
                {
                    lp += 0.12f * (x - lp);
                    v = Mathf.Sin(2f * Mathf.PI * (90f - 60f * t) * t) * Mathf.Exp(-t * 18f) + lp * 1.5f * Mathf.Exp(-t * 14f);
                }
                d[i] = (float)System.Math.Tanh(v * 1.6f) * 0.9f;
            }
            var c = AudioClip.Create("gore" + kind, n, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }
    }

    internal static class ScreenBlood
    {
        private struct Splat { public Rect r; public float born, life, rot; public int tex; }
        private static readonly List<Splat> splats = new List<Splat>();
        private static Texture2D[] texs;
        private static float nextAllowed;

        public static void Near(Vector3 pos, float scale, float amount)
        {
            if (!GorePlugin.Instance.ScreenBlood.Value || !StageManager.Instance || !StageManager.Instance.cameraRig || Time.time < nextAllowed) return;
            var cam = StageManager.Instance.cameraRig.mainCamera;
            if (!cam) return;
            float dist = Vector3.Distance(cam.transform.position, pos);
            float reach = scale * 1.8f * Mathf.Sqrt(amount); // only when it's really in your face
            if (dist > reach) return;
            Vector3 vp = cam.WorldToViewportPoint(pos);
            if (vp.z < 0f) return;
            if (texs == null) { texs = new Texture2D[3]; for (int i = 0; i < 3; i++) texs[i] = ModCommon.BlobTexture(128, 0.45f, 40 + i); }
            nextAllowed = Time.time + 0.4f;
            int n = Mathf.Clamp(Mathf.RoundToInt(amount * (1f - dist / reach)) + 1, 1, 3);
            for (int i = 0; i < n && splats.Count < 12; i++)
            {
                float sz = Screen.height * Random.Range(0.05f, 0.16f) * (1f - dist / reach * 0.6f);
                float x = Mathf.Clamp01(vp.x + Random.Range(-0.3f, 0.3f)) * Screen.width, y = (1f - Mathf.Clamp01(vp.y + Random.Range(-0.3f, 0.3f))) * Screen.height;
                splats.Add(new Splat { r = new Rect(x - sz / 2, y - sz / 2, sz, sz * Random.Range(0.7f, 1.2f)), born = Time.time, life = Random.Range(2.5f, 4.5f), rot = Random.Range(0f, 360f), tex = Random.Range(0, 3) });
            }
        }

        public static void Draw()
        {
            if (splats.Count == 0) return;
            var prev = GUI.color;
            var m = GUI.matrix;
            for (int i = splats.Count - 1; i >= 0; i--)
            {
                var s = splats[i];
                float t = (Time.time - s.born) / s.life;
                if (t >= 1f) { splats.RemoveAt(i); continue; }
                GUI.color = new Color(0.45f, 0f, 0.01f, 0.6f * (1f - t * t));
                GUIUtility.RotateAroundPivot(s.rot, s.r.center);
                var r = s.r; r.y += t * s.r.height * 0.4f;
                GUI.DrawTexture(r, texs[s.tex]);
                GUI.matrix = m;
            }
            GUI.color = prev;
        }
    }

    internal static class Blood
    {
        private static readonly Queue<GameObject> decals = new Queue<GameObject>();
        public static IEnumerable<GameObject> Decals => decals;
        private const int MaxDecals = 400;

        public static ParticleSystem MakeSystem(GameObject go, float scale, bool continuous)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = continuous;
            main.duration = continuous ? 1f : 0.2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 1f, scale * 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.09f, scale * 0.26f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0f, 0.02f, 1f), new Color(0.3f, 0f, 0.01f, 1f));
            main.gravityModifier = 0.35f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = continuous ? 600 : 500;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = continuous ? 18f : 60f;
            shape.radius = scale * 0.03f;
            var col = ps.collision;
            col.enabled = !continuous; // world collision per particle is expensive; only bursts splash
            col.type = ParticleSystemCollisionType.World;
            col.mode = ParticleSystemCollisionMode.Collision3D;
            col.collidesWith = ModCommon.GroundMask;
            col.bounce = 0.05f;
            col.dampen = 0.9f;
            col.lifetimeLoss = 0.6f;
            col.quality = ParticleSystemCollisionQuality.Low;
            col.maxCollisionShapes = 16;
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.4f));
            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.sharedMaterial = GorePlugin.Instance.bloodMat;
            rend.renderMode = ParticleSystemRenderMode.Stretch;
            rend.velocityScale = 0.025f;
            rend.lengthScale = 1.2f;
            ps.Play();
            return ps;
        }

        public static void Burst(Vector3 pos, Vector3 dir, float scale, float amount)
        {
            ScreenBlood.Near(pos, scale, amount);
            if (GorePlugin.Instance.BloodAmount.Value <= 0f) return;
            var go = new GameObject("GoreBurst");
            go.transform.position = pos;
            if (dir.sqrMagnitude > 1e-4f) go.transform.rotation = Quaternion.LookRotation(dir + Vector3.up * 0.5f);
            var ps = MakeSystem(go, scale, false);
            var main = ps.main;
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 2f, scale * 7f * Mathf.Sqrt(amount));
            int n = Mathf.Min(450, Mathf.RoundToInt(40 * amount * GorePlugin.Instance.BloodAmount.Value));
            ps.Emit(n);
            if (amount >= 2f)
            {
                var ep = new ParticleSystem.EmitParams
                {
                    startSize = scale * 0.6f, startLifetime = 1.2f,
                    startColor = new Color(0.5f, 0.02f, 0.02f, 0.35f),
                };
                for (int i = 0; i < Mathf.RoundToInt(6 * amount); i++)
                {
                    ep.velocity = Random.insideUnitSphere * scale * 0.8f + Vector3.up * scale * 0.3f;
                    ep.position = pos + Random.insideUnitSphere * scale * 0.4f;
                    ep.applyShapeToPosition = false;
                    ps.Emit(ep, 1);
                }
            }
            Object.Destroy(go, 2.5f);
            for (int i = 0; i < Mathf.CeilToInt(amount * 3f); i++)
                Decal(pos + Random.insideUnitSphere * scale * 0.6f, scale * Random.Range(0.5f, 1.1f) * Mathf.Sqrt(amount));
            Vector3 d0 = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.up;
            int rays = Mathf.Min(14, 3 + Mathf.RoundToInt(amount * 2.5f * Mathf.Sqrt(GorePlugin.Instance.BloodAmount.Value)));
            for (int i = 0; i < rays; i++)
            {
                Vector3 rd = (d0 + Random.insideUnitSphere * 1.1f).normalized;
                if (Physics.Raycast(pos, rd, out var h, scale * (2f + amount), ModCommon.GroundMask | 1, QueryTriggerInteraction.Ignore) && !h.rigidbody && !h.collider.GetComponent<RagdollPart>())
                    SurfaceDecal(h.point, h.normal, rd, scale * Random.Range(0.25f, 0.6f) * Mathf.Sqrt(amount));
            }
        }

        private static Material[] trackMats;
        private static readonly Queue<GameObject> tracks = new Queue<GameObject>();

        public static void Track(Vector3 point, Vector3 normal, Vector3 forward, float width, float length, float wetness)
        {
            if (trackMats == null)
            {
                trackMats = new Material[4];
                for (int i = 0; i < 4; i++)
                    trackMats[i] = new Material(GorePlugin.Instance.decalMat) { color = new Color(0.4f, 0f, 0.02f, 0.25f + 0.22f * i) };
            }
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ModCommon.StripCollider(q);
            q.name = "GoreTrack";
            q.transform.position = point + normal * (0.025f + tracks.Count * 0.00005f);
            Vector3 f = Vector3.ProjectOnPlane(forward, normal);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            q.transform.rotation = Quaternion.LookRotation(-normal, f);
            q.transform.localScale = new Vector3(width, length, 1f);
            var rend = q.GetComponent<Renderer>();
            rend.sharedMaterial = trackMats[Mathf.Clamp(Mathf.FloorToInt(wetness * 4f), 0, 3)];
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            tracks.Enqueue(q);
            while (tracks.Count > 200) { var old = tracks.Dequeue(); if (old) Object.Destroy(old); }
        }

        public static void SurfaceDecal(Vector3 point, Vector3 normal, Vector3 along, float size)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ModCommon.StripCollider(q);
            q.name = "GoreDecal";
            q.transform.position = point + normal * (0.02f + decals.Count * 0.00005f);
            Vector3 f = Vector3.ProjectOnPlane(along, normal);
            q.transform.rotation = f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(-normal, f) : Quaternion.LookRotation(-normal);
            float stretch = 1f + Mathf.Clamp01(1f - Mathf.Abs(Vector3.Dot(along, normal))) * 1.5f;
            q.transform.localScale = new Vector3(size, size * stretch, 1f);
            var rend = q.GetComponent<Renderer>();
            rend.sharedMaterial = GorePlugin.Instance.bloodTints[Random.Range(0, 4)];
            rend.receiveShadows = false;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            decals.Enqueue(q);
            while (decals.Count > MaxDecals) { var old = decals.Dequeue(); if (old) Object.Destroy(old); }
        }

        public static void Decal(Vector3 from, float size)
        {
            if (!Physics.Raycast(from + Vector3.up * size, Vector3.down, out var hit, size * 30f, ModCommon.GroundMask, QueryTriggerInteraction.Ignore))
                return;
            if (hit.rigidbody) return; // don't paint the car / props
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ModCommon.StripCollider(q);
            q.name = "GoreDecal";
            q.transform.position = hit.point + hit.normal * (0.02f + decals.Count * 0.00005f);
            q.transform.rotation = Quaternion.LookRotation(-hit.normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            q.transform.localScale = new Vector3(size, size * Random.Range(0.7f, 1.3f), 1f);
            var rend = q.GetComponent<Renderer>();
            rend.sharedMaterial = GorePlugin.Instance.bloodTints[Random.Range(0, 4)];
            rend.receiveShadows = false;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            decals.Enqueue(q);
            while (decals.Count > MaxDecals)
            {
                var old = decals.Dequeue();
                if (old) Object.Destroy(old);
            }
        }
    }

    internal static class Gib
    {
        private const int MaxGibs = 20;
        private static readonly Queue<GameObject> live = new Queue<GameObject>();

        public static GameObject Create(ActiveRagdoll r, RagdollPart root, List<RagdollPart> sub, float lifetime)
        {
            var go = new GameObject("GoreGib");
            go.layer = root.gameObject.layer;
            float mass = 0f;
            foreach (var p in sub) if (p.rigidBody) mass += p.rigidBody.mass;

            bool meshed = false;
            try { meshed = TrySkinnedCopy(r, root, go); }
            catch (System.Exception e) { GorePlugin.Log.LogWarning("Skinned limb copy failed: " + e.Message); }
            if (!meshed)
            {
                try { meshed = TryMeshSlice(r, root, go); }
                catch (System.Exception e) { GorePlugin.Log.LogWarning("Mesh slice failed: " + e.Message); }
            }
            if (!meshed) { Object.Destroy(go); return null; }

            foreach (var p in sub)
            {
                var src = p.GetComponent<Collider>();
                if (!src) continue;
                var child = new GameObject("col");
                child.layer = go.layer;
                child.transform.SetParent(go.transform, false);
                child.transform.position = p.transform.position;
                child.transform.rotation = p.transform.rotation;
                child.transform.localScale = Vector3.one;
                var s = p.transform.lossyScale;
                switch (src)
                {
                    case CapsuleCollider c:
                        var cc = child.AddComponent<CapsuleCollider>();
                        cc.center = Vector3.Scale(c.center, s); cc.radius = c.radius * Mathf.Max(s.x, s.z); cc.height = c.height * s.y; cc.direction = c.direction;
                        break;
                    case BoxCollider b:
                        var bc = child.AddComponent<BoxCollider>();
                        bc.center = Vector3.Scale(b.center, s); bc.size = Vector3.Scale(b.size, s);
                        break;
                    case SphereCollider sp:
                        var sc = child.AddComponent<SphereCollider>();
                        sc.center = Vector3.Scale(sp.center, s); sc.radius = sp.radius * s.x;
                        break;
                    default:
                        var bb = child.AddComponent<BoxCollider>();
                        bb.size = src.bounds.size;
                        break;
                }
                var srcMat = src.sharedMaterial;
                foreach (var c in child.GetComponents<Collider>()) c.sharedMaterial = srcMat;
            }

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.1f, mass);
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            live.Enqueue(go);
            while (live.Count > MaxGibs) { var old = live.Dequeue(); if (old) Object.Destroy(old); }
            rb.drag = 0.05f;
            rb.angularDrag = 0.3f;

            go.AddComponent<Squish>().Init(ModCommon.BodyScale(r), true);
            var drip = Bleeder.Attach(go.transform, root.transform.position, ModCommon.BodyScale(r) * 0.7f, true);
            drip.Refresh(GorePlugin.Instance.StumpBleedSeconds.Value * 0.5f, 0.5f);
            Object.Destroy(go, lifetime);
            return go;
        }

        private static bool TrySkinnedCopy(ActiveRagdoll r, RagdollPart root, GameObject go)
        {
            SkinnedMeshRenderer smr = null;
            foreach (var x in r.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                if (x.enabled && x.gameObject.activeInHierarchy && x.sharedMesh) { smr = x; break; }
            if (!smr) return false;
            var bones = smr.bones;
            if (bones == null || bones.Length == 0) return false;

            go.transform.SetPositionAndRotation(root.transform.position, root.transform.rotation);
            Vector3 cut = root.transform.position;
            var copy = new Transform[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
            {
                var src = bones[i];
                var t = new GameObject("bone").transform;
                t.SetParent(go.transform, false);
                if (src && (src == root.transform || src.IsChildOf(root.transform)))
                {
                    t.SetPositionAndRotation(src.position, src.rotation);
                    t.localScale = src.lossyScale;
                    any = true;
                }
                else
                {
                    t.SetPositionAndRotation(cut, src ? src.rotation : Quaternion.identity);
                    t.localScale = Vector3.one * 0.0001f;
                }
                copy[i] = t;
            }
            if (!any) { foreach (var t in copy) Object.Destroy(t.gameObject); return false; }

            var skinGo = new GameObject("limb");
            skinGo.transform.SetParent(go.transform, false);
            var sk = skinGo.AddComponent<SkinnedMeshRenderer>();
            sk.sharedMesh = smr.sharedMesh;
            sk.sharedMaterials = smr.sharedMaterials; // includes the blood-stained instances
            sk.bones = copy;
            int rootIdx = System.Array.IndexOf(bones, smr.rootBone);
            sk.rootBone = rootIdx >= 0 ? copy[rootIdx] : go.transform;
            sk.updateWhenOffscreen = true;
            sk.quality = SkinQuality.Bone4;
            sk.shadowCastingMode = smr.shadowCastingMode;
            return true;
        }

        private static bool TryMeshSlice(ActiveRagdoll r, RagdollPart root, GameObject go)
        {
            SkinnedMeshRenderer smr = null;
            foreach (var s in r.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                if (s.enabled && s.gameObject.activeInHierarchy && s.sharedMesh) { smr = s; break; }
            if (!smr || !smr.sharedMesh.isReadable) return false;

            var bones = smr.bones;
            var cut = new bool[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] && (bones[i] == root.transform || bones[i].IsChildOf(root.transform))) { cut[i] = any = true; }
            if (!any) return false;

            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            var src = smr.sharedMesh;
            var weights = src.boneWeights;
            var verts = baked.vertices;
            var normals = baked.normals;
            var uvs = baked.uv;
            if (weights.Length != verts.Length) return false;

            var keep = new bool[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var w = weights[i];
                float sum = (cut[w.boneIndex0] ? w.weight0 : 0f) + (cut[w.boneIndex1] ? w.weight1 : 0f)
                          + (cut[w.boneIndex2] ? w.weight2 : 0f) + (cut[w.boneIndex3] ? w.weight3 : 0f);
                keep[i] = sum >= 0.5f;
            }

            var remap = new Dictionary<int, int>();
            var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nu = new List<Vector2>();
            var subTris = new List<List<int>>();
            var mats = new List<Material>();
            var srcMats = smr.sharedMaterials;
            for (int sm = 0; sm < baked.subMeshCount; sm++)
            {
                var tris = baked.GetTriangles(sm);
                var list = new List<int>();
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (!(keep[a] && keep[b] && keep[c])) continue;
                    foreach (int idx in new[] { a, b, c })
                    {
                        if (!remap.TryGetValue(idx, out int ni))
                        {
                            ni = nv.Count; remap[idx] = ni;
                            nv.Add(verts[idx]);
                            nn.Add(normals.Length > idx ? normals[idx] : Vector3.up);
                            nu.Add(uvs.Length > idx ? uvs[idx] : Vector2.zero);
                        }
                        list.Add(ni);
                    }
                }
                if (list.Count > 0) { subTris.Add(list); mats.Add(srcMats[Mathf.Min(sm, srcMats.Length - 1)]); }
            }
            Object.Destroy(baked);
            if (nv.Count < 3) return false;

            Vector3 pivotLocal = Quaternion.Inverse(smr.transform.rotation) * (root.transform.position - smr.transform.position);
            for (int i = 0; i < nv.Count; i++) nv[i] -= pivotLocal;

            var mesh = new Mesh { name = "GibMesh" };
            if (nv.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(nv); mesh.SetNormals(nn); mesh.SetUVs(0, nu);
            mesh.subMeshCount = subTris.Count;
            for (int i = 0; i < subTris.Count; i++) mesh.SetTriangles(subTris[i], i);
            mesh.RecalculateBounds();

            var vis = new GameObject("mesh");
            vis.transform.SetParent(go.transform, false);
            vis.AddComponent<MeshFilter>().sharedMesh = mesh;
            vis.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();

            go.transform.position = root.transform.position;
            go.transform.rotation = smr.transform.rotation;
            return true;
        }

    }

    internal class Squish : MonoBehaviour
    {
        private float scale;
        private bool big;
        private float born;

        public void Init(float scale, bool big) { this.scale = scale; this.big = big; born = Time.time; }

        private void OnCollisionEnter(Collision c)
        {
            if (Time.time - born < 0.3f || !GorePlugin.Instance.CarGore.Value) return;
            bool car = CheeseApi.IsVehicle(c.rigidbody);
            if (!car || c.relativeVelocity.magnitude < 35f) return;
            Vector3 p = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            Blood.Burst(p, Vector3.up, scale, big ? 3f : 1.2f);
            CarGoreFx.Splatter(c.rigidbody, p, scale);
            CarGore.MarkWheelsBloody(c.rigidbody, p);
            Destroy(gameObject);
        }
    }

    internal static class CarGoreFx
    {
        private const int MaxPerCar = 70;
        private static readonly Queue<GameObject> stuck = new Queue<GameObject>();

        public static void Splatter(Rigidbody car, Vector3 point, float scale)
        {
            if (!car) return;
            Vector3 center = car.worldCenterOfMass;
            Vector3 outDir = (point - center);
            outDir = outDir.sqrMagnitude > 1e-4f ? outDir.normalized : car.transform.forward;
            RaycastHit best = default; bool found = false;
            foreach (var col in car.GetComponents<Collider>())
            {
                var ray = new Ray(point + outDir * scale * 3f, -outDir);
                if (col.Raycast(ray, out var h, scale * 6f) && (!found || h.distance < best.distance)) { best = h; found = true; }
            }
            if (!found) return;
            for (int i = 0; i < 2; i++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                ModCommon.StripCollider(q);
                q.name = "GoreCarBlood";
                q.transform.SetParent(car.transform, true);
                Vector3 jitter = Vector3.ProjectOnPlane(Random.insideUnitSphere, best.normal) * scale * 0.25f;
                q.transform.position = best.point + jitter + best.normal * 0.03f;
                q.transform.rotation = Quaternion.LookRotation(-best.normal) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
                float sz = scale * Random.Range(0.2f, 0.5f);
                Vector3 ls = car.transform.lossyScale;
                q.transform.localScale = new Vector3(sz / ls.x, sz * Random.Range(0.7f, 1.3f) / ls.y, 1f);
                var rend = q.GetComponent<Renderer>();
                rend.sharedMaterial = GorePlugin.Instance.bloodTints[Random.Range(0, 4)];
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                stuck.Enqueue(q);
                while (stuck.Count > MaxPerCar) { var o = stuck.Dequeue(); if (o) Object.Destroy(o); }
            }
        }
    }

    internal class CarGore : MonoBehaviour
    {
        private static CarGore inst;
        private Rigidbody car;
        private Transform[] wheels;
        private readonly float[] wet = new float[4];
        private readonly Vector3[] lastTrack = new Vector3[4];
        private float nextFind, nextScan;
        private readonly Collider[] overlap = new Collider[16];
        private readonly Dictionary<RagdollPart, float> crushCooldown = new Dictionary<RagdollPart, float>();

        private void Awake() { inst = this; }

        public static void MarkWheelsBloody(Rigidbody car, Vector3 near)
        {
            if (!inst || inst.car != car || inst.wheels == null) return;
            for (int i = 0; i < 4; i++)
                if (inst.wheels[i] && Vector3.Distance(inst.wheels[i].position, near) < ModCommon.BodyScale(null) * 2.5f) inst.wet[i] = 1f;
        }

        private void FixedUpdate()
        {
            if (!GorePlugin.Instance.CarGore.Value || !GorePlugin.Instance.Enabled.Value || !ModCommon.InRound) return;
            if (Time.time > nextFind || (!car && Time.time > nextFind))
            {
                nextFind = Time.time + 1f;
                var go = GameObject.Find(CheeseApi.CarName);
                car = go ? go.GetComponent<Rigidbody>() : null;
                wheels = null;
                if (car)
                {
                    wheels = new Transform[4];
                    for (int i = 0; i < 4; i++) wheels[i] = car.transform.Find("wheel" + i);
                }
            }
            if (!car || wheels == null) return;

            float scale = ModCommon.BodyScale(null);
            float speed = car.velocity.magnitude;
            var driver = CheeseApi.Driver;

            for (int i = 0; i < 4; i++)
            {
                var w = wheels[i];
                if (!w) continue;
                if (!Physics.Raycast(w.position, -car.transform.up, out var ground, scale * 2f, ModCommon.GroundMask, QueryTriggerInteraction.Ignore))
                    continue;
                Vector3 contact = ground.point;

                if (speed > 20f)
                {
                    int n = Physics.OverlapSphereNonAlloc(contact + ground.normal * scale * 0.25f, scale * 0.35f, overlap, ~0, QueryTriggerInteraction.Ignore);
                    for (int k = 0; k < n; k++)
                    {
                        var part = overlap[k].GetComponent<RagdollPart>();
                        if (!part || !part.ragdoll || part.ragdoll == driver) continue;
                        if (crushCooldown.TryGetValue(part, out float t) && Time.time < t) continue;
                        crushCooldown[part] = Time.time + 0.35f;
                        GorePlugin.Instance.ForceHit(part, speed * 1.6f + 60f, contact, car.velocity);
                        if (part.rigidBody && !part.rigidBody.isKinematic)
                            part.rigidBody.AddForce(car.velocity * 0.3f + ground.normal * speed * 0.2f, ForceMode.VelocityChange);
                        wet[i] = 1f;
                    }
                }

                if (Time.time > nextScan)
                    foreach (var d in Blood.Decals)
                    {
                        if (!d) continue;
                        float rad = d.transform.lossyScale.x * 0.5f + scale * 0.2f;
                        if ((d.transform.position - contact).sqrMagnitude < rad * rad) { wet[i] = Mathf.Max(wet[i], 0.8f); break; }
                    }

                if (wet[i] > 0.02f && speed > 6f)
                {
                    float step = scale * 0.7f;
                    if ((contact - lastTrack[i]).sqrMagnitude > step * step)
                    {
                        Blood.Track(contact, ground.normal, car.velocity, scale * 0.32f, step * 1.15f, wet[i]);
                        lastTrack[i] = contact;
                        wet[i] -= 0.035f; // ~30 marks per soak
                    }
                }
                else lastTrack[i] = contact;
            }
            if (Time.time > nextScan) nextScan = Time.time + 0.1f;
            if (crushCooldown.Count > 64) crushCooldown.Clear();
        }
    }
}
