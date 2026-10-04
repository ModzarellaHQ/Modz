using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace Modz
{
    [BepInPlugin(GUID, "Euphoria Ragdolls", "2.2.0")]
    [BepInDependency(CorePlugin.GUID)]
    public class EuphoriaPlugin : BaseUnityPlugin
    {
        public const string GUID = "modz.euphoria";
        internal static ManualLogSource Log;
        public static EuphoriaPlugin Instance;

        public ConfigEntry<bool> Enabled, OnPlayer, OnBots;
        public ConfigEntry<float> Strength, DeathTwitch;

        private readonly Dictionary<ActiveRagdoll, Brain> brains = new Dictionary<ActiveRagdoll, Brain>();

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Bodies brace, shield their heads, flinch, writhe and struggle instead of flopping (GTA IV style).");
            Strength = Config.Bind("Euphoria", "Muscle strength", 1f, ModCommon.Desc("How hard the ragdolls fight.", new AcceptableValueRange<float>(0.2f, 3f)));
            OnPlayer = Config.Bind("Euphoria", "On you", true, "Your own ragdoll reacts too.");
            OnBots = Config.Bind("Euphoria", "On bots", true, "Bots react.");
            DeathTwitch = Config.Bind("Euphoria", "Death throes (s)", 2.5f, ModCommon.Desc("How long a dying body keeps twitching.", new AcceptableValueRange<float>(0f, 8f), true));
            Log.LogInfo("Euphoria Ragdolls loaded");
        }

        private float nextScan;

        private void FixedUpdate()
        {
            if (!Enabled.Value || !ModCommon.InRound) return;
            if (Time.time > nextScan)
            {
                nextScan = Time.time + 0.5f;
                foreach (var r in ModCommon.AllRagdolls())
                    if (!brains.ContainsKey(r)) brains[r] = new Brain(r);
                var gone = new List<ActiveRagdoll>();
                foreach (var k in brains.Keys) if (!k) gone.Add(k);
                foreach (var k in gone) brains.Remove(k);
            }
            foreach (var kv in brains)
            {
                if (!kv.Key) continue;
                bool local = ModCommon.IsLocal(kv.Key);
                if (local ? !OnPlayer.Value : !OnBots.Value) continue;
                kv.Value.Tick(Time.fixedDeltaTime);
            }
        }
    }

    internal class Brain
    {
        private readonly ActiveRagdoll r;
        private readonly Dictionary<RagdollPart, Vector3> lastVel = new Dictionary<RagdollPart, Vector3>();
        private RagdollPart hitPart;
        private Vector3 hitDir;
        private float hitTime = -10f, offFeet, deathTime = -1f, seed;
        private bool wasDead;

        public Brain(ActiveRagdoll r) { this.r = r; seed = Random.value * 100f; }

        public void Tick(float dt)
        {
            if (!r || !r.spine1 || !r.spine1.rigidBody || r.spine1.rigidBody.isKinematic) return;
            if (CheeseApi.IsSeated(r)) return;
            float k = 240f * EuphoriaPlugin.Instance.Strength.Value, d = 18f;
            float sc = ModCommon.BodyScale(r);
            var root = r.spine1.rigidBody;
            Vector3 vel = root.velocity;
            bool dead = CheeseApi.IsDead(r);
            bool onFeet = ModCommon.Grounded(r) && !dead;

            DetectHit(dt);

            if (dead)
            {
                if (!wasDead) deathTime = Time.time;
                wasDead = true;
                float t = Time.time - deathTime, life = EuphoriaPlugin.Instance.DeathTwitch.Value;
                if (t < life)
                {
                    float fade = 1f - t / life;
                    foreach (var p in r.GetRagdollParts())
                        if (Live(p) && Random.value < 0.06f * fade)
                            p.rigidBody.AddTorque(Random.insideUnitSphere * 25f * fade, ForceMode.VelocityChange);
                    if (hitPart && Live(hitPart)) ReachHand(r.handRight, r.lowerArmRight, r.upperArmRight, hitPart.transform.position, k * 0.4f * fade, d);
                }
                return;
            }
            wasDead = false;

            if (onFeet)
            {
                offFeet = 0f;
                if (Time.time - hitTime < 0.6f && hitPart && Live(hitPart))
                {
                    float f = 1f - (Time.time - hitTime) / 0.6f;
                    Align(r.spine2, r.head, (Vector3.up + hitDir * 0.6f).normalized, k * 0.6f * f, d);
                    ReachHand(r.handLeft, r.lowerArmLeft, r.upperArmLeft, hitPart.transform.position, k * f, d);
                }
                CarIncoming(k, d, sc);
                return;
            }
            offFeet += dt;

            float spin = root.angularVelocity.magnitude;
            bool airborne = !AnyTouching();
            Vector3 down = Vector3.down;

            if (airborne)
            {
                Vector3 fall = vel.sqrMagnitude > 1f ? (vel.normalized + down * 0.8f).normalized : down;
                bool groundSoon = Physics.Raycast(root.position, fall, out var hit, Mathf.Max(sc, vel.magnitude * 0.45f), ModCommon.GroundMask, QueryTriggerInteraction.Ignore);
                if (spin > 7f)
                {
                    ReachHand(r.handLeft, r.lowerArmLeft, r.upperArmLeft, r.head.transform.position + r.head.transform.right * -sc * 0.06f, k, d);
                    ReachHand(r.handRight, r.lowerArmRight, r.upperArmRight, r.head.transform.position + r.head.transform.right * sc * 0.06f, k, d);
                    Curl(k * 0.8f, d); // tuck into a ball
                }
                else if (groundSoon)
                {
                    Vector3 p = hit.point + Vector3.up * sc * 0.1f;
                    ReachHand(r.handLeft, r.lowerArmLeft, r.upperArmLeft, p - Vector3.Cross(Vector3.up, fall).normalized * sc * 0.25f, k * 1.2f, d);
                    ReachHand(r.handRight, r.lowerArmRight, r.upperArmRight, p + Vector3.Cross(Vector3.up, fall).normalized * sc * 0.25f, k * 1.2f, d);
                    Align(r.spine2, r.head, (Vector3.up * 0.8f - fall * 0.4f).normalized, k * 0.5f, d);
                    LegsTo(-fall + down, k * 0.6f, d);
                }
                else
                {
                    float t = Time.time * 7f + seed;
                    Pedal(t, k * 0.7f, d);
                    Vector3 fwd = Vector3.ProjectOnPlane(r.spine2.transform.forward, Vector3.up).normalized;
                    if (!CheeseApi.HandsBusy(r)) Align(r.upperArmLeft, r.lowerArmLeft, (Quaternion.AngleAxis(t * 60f, Vector3.Cross(Vector3.up, fwd)) * Vector3.up).normalized, k * 0.6f, d);
                    if (!CheeseApi.HandsBusy(r)) Align(r.upperArmRight, r.lowerArmRight, (Quaternion.AngleAxis(t * 60f + 180f, Vector3.Cross(Vector3.up, fwd)) * Vector3.up).normalized, k * 0.6f, d);
                }
                CarIncoming(k, d, sc);
                return;
            }

            bool wounded = WoundPoint(out Vector3 wound) || Time.time - hitTime < 4f && hitPart && Live(hitPart);
            if (wounded)
            {
                Vector3 w = hitPart && Live(hitPart) && Time.time - hitTime < 4f ? hitPart.transform.position : wound;
                ReachHand(r.handLeft, r.lowerArmLeft, r.upperArmLeft, w, k, d);
                ReachHand(r.handRight, r.lowerArmRight, r.upperArmRight, w + Vector3.up * sc * 0.05f, k * 0.8f, d);
                float t = Time.time * 2.2f + seed;
                Vector3 thigh = (r.spine2.transform.forward * Mathf.Lerp(0.2f, 1f, 0.5f + 0.5f * Mathf.Sin(t)) - Vector3.up * 0.2f).normalized;
                Align(r.upperLegLeft, r.lowerLegLeft, thigh, k * 0.4f, d);
                Align(r.upperLegRight, r.lowerLegRight, (r.spine2.transform.forward * Mathf.Lerp(0.2f, 1f, 0.5f - 0.5f * Mathf.Sin(t)) - Vector3.up * 0.2f).normalized, k * 0.4f, d);
                root.AddTorque(r.spine2.transform.up * Mathf.Sin(t * 0.7f) * 6f * EuphoriaPlugin.Instance.Strength.Value, ForceMode.Acceleration);
                Align(r.spine2, r.head, (Vector3.up + r.spine2.transform.forward * 0.4f).normalized, k * 0.3f, d);
            }
            else if (offFeet > 0.4f)
            {
                Vector3 under = root.position + Vector3.down * sc * 0.6f;
                ReachHand(r.handLeft, r.lowerArmLeft, r.upperArmLeft, under - r.spine2.transform.right * sc * 0.25f, k, d);
                ReachHand(r.handRight, r.lowerArmRight, r.upperArmRight, under + r.spine2.transform.right * sc * 0.25f, k, d);
                Align(r.spine2, r.head, Vector3.up, k * 0.6f, d);
                LegsTo(Vector3.down + r.spine2.transform.forward * 0.4f, k * 0.5f, d);
            }
            CarIncoming(k, d, sc);
        }

        private void DetectHit(float dt)
        {
            bool busy = CheeseApi.HandsBusy(r);
            foreach (var p in r.GetRagdollParts())
            {
                if (!Live(p) || busy && IsArm(p)) continue;
                Vector3 v = p.rigidBody.velocity;
                if (lastVel.TryGetValue(p, out var lv))
                {
                    Vector3 dv = v - lv;
                    if (dv.magnitude > 45f && p != r.spine1 && Time.time - hitTime > 0.3f)
                    { hitPart = p; hitDir = dv.normalized; hitTime = Time.time; }
                }
                lastVel[p] = v;
            }
        }

        private bool IsArm(RagdollPart p) => p == r.handLeft || p == r.handRight || p == r.lowerArmLeft || p == r.lowerArmRight || p == r.upperArmLeft || p == r.upperArmRight;

        private bool AnyTouching()
        {
            foreach (var p in r.GetRagdollParts()) if (p && p.touchingGround) return true;
            return false;
        }

        private Transform woundCache;
        private float nextWoundScan;

        private bool WoundPoint(out Vector3 at)
        {
            if (Time.time > nextWoundScan)
            {
                nextWoundScan = Time.time + 0.5f;
                woundCache = null;
                foreach (Transform t in r.GetComponentsInChildren<Transform>())
                    if (t.name == "GoreStumpBleeder" || t.name == "GoreWound") { woundCache = t; break; }
            }
            at = woundCache ? woundCache.position : Vector3.zero;
            return woundCache;
        }

        private static Rigidbody carCache;
        private static float nextCarScan;

        private void CarIncoming(float k, float d, float sc)
        {
            if (Time.time > nextCarScan)
            {
                nextCarScan = Time.time + 0.5f;
                var go = GameObject.Find(CheeseApi.CarName);
                carCache = go ? go.GetComponent<Rigidbody>() : null;
            }
            var crb = carCache;
            if (!crb || CheeseApi.IsSeated(r)) return;
            Vector3 to = r.GetRootPosition() - crb.position;
            float closing = Vector3.Dot(crb.velocity, to.normalized);
            if (closing < 50f || to.magnitude > closing * 0.8f) return; // less than ~0.8 s away
            Vector3 face = r.head.transform.position + (crb.position - r.head.transform.position).normalized * sc * 0.4f;
            ReachHand(r.handLeft, r.lowerArmLeft, r.upperArmLeft, face - Vector3.up * sc * 0.05f, k * 1.4f, d);
            ReachHand(r.handRight, r.lowerArmRight, r.upperArmRight, face + Vector3.up * sc * 0.05f, k * 1.4f, d);
        }

        private static bool Live(RagdollPart p) => p && p.rigidBody && !p.rigidBody.isKinematic && p.transform.localScale.x > 0.01f;

        private void Curl(float k, float d)
        {
            Vector3 fwd = r.spine2.transform.forward;
            Align(r.upperLegLeft, r.lowerLegLeft, (fwd + Vector3.up * 0.2f).normalized, k, d);
            Align(r.upperLegRight, r.lowerLegRight, (fwd + Vector3.up * 0.2f).normalized, k, d);
            Align(r.lowerLegLeft, r.footLeft, -fwd, k * 0.6f, d);
            Align(r.lowerLegRight, r.footRight, -fwd, k * 0.6f, d);
            Align(r.spine2, r.head, (r.spine2.transform.up + fwd * 0.6f).normalized, k * 0.5f, d);
        }

        private void LegsTo(Vector3 dir, float k, float d)
        {
            dir.Normalize();
            Align(r.upperLegLeft, r.lowerLegLeft, dir, k, d);
            Align(r.upperLegRight, r.lowerLegRight, dir, k, d);
        }

        private void Pedal(float t, float k, float d)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(r.spine2.transform.forward, Vector3.up).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            Vector3 a = (Vector3.down + fwd * Mathf.Sin(t) * 0.8f).normalized, b = (Vector3.down - fwd * Mathf.Sin(t) * 0.8f).normalized;
            Align(r.upperLegLeft, r.lowerLegLeft, (a - side * 0.1f).normalized, k, d);
            Align(r.upperLegRight, r.lowerLegRight, (b + side * 0.1f).normalized, k, d);
            Align(r.lowerLegLeft, r.footLeft, (Vector3.down - fwd * Mathf.Cos(t) * 0.6f).normalized, k * 0.6f, d);
            Align(r.lowerLegRight, r.footRight, (Vector3.down + fwd * Mathf.Cos(t) * 0.6f).normalized, k * 0.6f, d);
        }

        private void ReachHand(RagdollPart hand, RagdollPart lower, RagdollPart upper, Vector3 target, float k, float d)
        {
            if (!Live(upper) || !Live(lower) || CheeseApi.HandsBusy(r)) return;
            Vector3 toT = target - upper.transform.position;
            Align(upper, lower, (toT.normalized + Vector3.down * 0.15f).normalized, k, d);
            if (hand && Live(hand)) Align(lower, hand, (target - lower.transform.position).normalized, k * 0.8f, d);
        }

        private static void Align(RagdollPart part, RagdollPart tip, Vector3 target, float k, float d)
        {
            if (!Live(part) || !tip || target.sqrMagnitude < 1e-6f) return;
            Vector3 cur = tip.transform.position - part.transform.position;
            if (cur.sqrMagnitude < 1e-6f) return;
            cur.Normalize();
            Vector3 axis = Vector3.Cross(cur, target);
            float ang = Mathf.Atan2(axis.magnitude, Vector3.Dot(cur, target));
            if (axis.sqrMagnitude > 1e-8f) axis.Normalize();
            part.rigidBody.AddTorque(axis * (ang * k) - part.rigidBody.angularVelocity * d, ForceMode.Acceleration);
        }
    }
}
