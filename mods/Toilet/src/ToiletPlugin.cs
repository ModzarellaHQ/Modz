using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CheeseMods
{
    [BepInPlugin(GUID, "Rocket Toilet", "2.0.0")]
    [BepInDependency(CorePlugin.GUID)]
    public class ToiletPlugin : BaseUnityPlugin
    {
        public const string GUID = "cheesemods.toilet";
        internal static ManualLogSource Log;
        public static ToiletPlugin Instance;

        public ConfigEntry<bool> Enabled;
        public ConfigEntry<KeyboardShortcut> SitKey, ThrustKey;
        public ConfigEntry<float> Power, AirControl, PoopAmount, Volume, Size, EjectSpeed;

        internal Toilet toilet;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Sit on a toilet and blast off on a column of poop.");
            SitKey = Config.Bind("Keys", "Sit / stand up", new KeyboardShortcut(KeyCode.T), "Spawn the toilet if needed and sit on it, or stand up.");
            ThrustKey = Config.Bind("Keys", "Poop thrust (hold)", new KeyboardShortcut(KeyCode.Space), "Hold while seated to shit your way into the sky (G works too). Ctrl = gentle hover.");
            Power = Config.Bind("Toilet", "Thrust power", 1.8f, ModCommon.Desc("Thrust in multiples of gravity (1 = hover).", new AcceptableValueRange<float>(0.5f, 6f)));
            AirControl = Config.Bind("Toilet", "Air control", 1f, ModCommon.Desc("How hard WASD tilts the toilet.", new AcceptableValueRange<float>(0f, 3f), true));
            PoopAmount = Config.Bind("Toilet", "Poop amount", 1f, ModCommon.Desc("Particles and chunks.", new AcceptableValueRange<float>(0f, 4f)));
            Volume = Config.Bind("Toilet", "Volume", 0.7f, ModCommon.Desc("On top of the game's Master x SFX volume.", new AcceptableValueRange<float>(0f, 1f)));
            EjectSpeed = Config.Bind("Toilet", "Eject impact speed", 200f, ModCommon.Desc("Crash speed that throws you off.", new AcceptableValueRange<float>(60f, 600f), true));
            Size = Config.Bind("Toilet", "Size", 1f, ModCommon.Desc("Toilet scale. Applies on next spawn.", new AcceptableValueRange<float>(0.5f, 3f), true));
            Enabled.SettingChanged += (_, __) => { if (!Enabled.Value) Despawn(); };

            MenuRegistry.Action(GUID, "Sit / stand", ToggleSit);
            MenuRegistry.Action(GUID, "New toilet here", () => { Despawn(); var me = ModCommon.LocalRagdoll(); if (me) toilet = Toilet.Spawn(me); });
            MenuRegistry.Action(GUID, "Despawn", Despawn);

                        Log.LogInfo("Rocket Toilet loaded");
        }

        private void Update()
        {
            if (!Enabled.Value || !ModCommon.InRound || ModCommon.Paused) return;
            if (SitKey.Value.IsDown()) ToggleSit();
        }

        public void ToggleSit()
        {
            if (!ModCommon.Active) { ModCommon.Toast("Mods only work in Play Offline."); return; }
            var me = ModCommon.LocalRagdoll();
            if (!me) return;
            if (toilet && toilet.Rider == me) { toilet.Stand(); return; }
            if (CheeseApi.IsSeated(me)) { ModCommon.Toast("Get out of the car first."); return; }
            if (!toilet) toilet = Toilet.Spawn(me);
            else if (Vector3.Distance(toilet.transform.position, me.GetRootPosition()) > ModCommon.BodyScale(me) * 5f) toilet.PlaceNear(me);
            toilet.Sit(me);
        }

        public void Despawn()
        {
            if (!toilet) return;
            if (toilet.Rider) toilet.Stand();
            Destroy(toilet.gameObject);
        }

        private void OnGUI()
        {
            if (!toilet || !toilet.Rider || !ModCommon.IsLocal(toilet.Rider)) return;
            var st = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight, normal = { textColor = new Color(0.85f, 0.65f, 0.35f) } };
            GUI.Label(new Rect(Screen.width - 520, Screen.height - 80, 500, 60),
                $"{(toilet.Thrusting ? "💩 BLASTING OFF 💩" : "On the throne")}  {toilet.Altitude:F0} up\n<size=13>Hold {ThrustKey.Value}/G: thrust · Ctrl: hover · mouse: aim · WASD: fly · {SitKey.Value}: stand up</size>", st);
        }

        internal static string ResolveModel()
        {
            return Path.Combine(Path.GetDirectoryName(typeof(ToiletPlugin).Assembly.Location), "toilet.glb");
        }
    }

    public class Toilet : MonoBehaviour
    {
        public ActiveRagdoll Rider { get; private set; }
        public static bool ScriptThrust;
        public bool Thrusting { get; private set; }
        public float Altitude { get; private set; }

        private Rigidbody rb;
        private float s, depth, height;
        private Transform seat, camProxy, nozzle;
        private KinematicSeat kseat;
        private readonly List<Collider> cols = new List<Collider>();
        private ParticleSystem poop;
        private AudioSource oneShot;
        private PoopSynth synth;
        private float nextChunk;
        private Vector3 prevVel, camVel;
        private static GlbLoader.CarModel template;
        private static string templateKey;
        private static Material poopMat, poopDecal;
        private static AudioClip plopClip;
        private static AudioClip[] fartClips;

        public static Toilet Spawn(ActiveRagdoll near)
        {
            var go = new GameObject(CheeseApi.ToiletName);
            int layer = LayerMask.NameToLayer("Obstacle");
            go.layer = layer >= 0 ? layer : 0;
            var t = go.AddComponent<Toilet>();
            t.Build(near);
            t.PlaceNear(near);
            return t;
        }

        private void Build(ActiveRagdoll refR)
        {
            var cfg = ToiletPlugin.Instance;
            s = ModCommon.BodyScale(refR) * cfg.Size.Value;
            depth = 0.85f * s;
            float rMass = 0f;
            foreach (var p in refR.GetRagdollParts()) if (p && p.rigidBody) rMass += p.rigidBody.mass;

            rb = gameObject.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(1f, rMass * 1.5f);
            rb.drag = 0.05f;
            rb.angularDrag = 2f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var model = LoadModel();
            Bounds b;
            if (model != null)
            {
                var vis = Instantiate(model.Body, transform, false);
                vis.name = "model";
                vis.transform.localScale = Vector3.one * depth;
                vis.SetActive(true);
                b = new Bounds(model.BodyBounds.center * depth, model.BodyBounds.size * depth);
            }
            else b = BuildProcedural();
            height = b.size.y;

            var box = gameObject.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size * 0.95f;
            box.sharedMaterial = new PhysicMaterial("Porcelain") { dynamicFriction = 0.4f, staticFriction = 0.5f, bounciness = 0.15f };
            cols.Add(box);
            rb.centerOfMass = new Vector3(0f, height * 0.3f, 0f);

            seat = new GameObject("seat").transform;
            seat.SetParent(transform, false);
            seat.localPosition = new Vector3(0f, height * 0.5f, b.center.z + b.size.z * 0.12f);
            camProxy = new GameObject("ToiletCamera").transform;
            camProxy.SetParent(transform, false);
            nozzle = new GameObject("nozzle").transform;
            nozzle.SetParent(transform, false);
            nozzle.localPosition = new Vector3(0f, 0.02f * s, b.center.z + b.size.z * 0.12f);
            nozzle.localRotation = Quaternion.Euler(90f, 0f, 0f); // emit downwards
            kseat = gameObject.AddComponent<KinematicSeat>();
            kseat.Seat = seat;
            kseat.Pose = SeatPose.Toilet;
            BuildFx();
        }

        private static GlbLoader.CarModel LoadModel()
        {
            var cfg = ToiletPlugin.Instance;
            string path = ToiletPlugin.ResolveModel();
            if (path == null || !File.Exists(path)) return null;
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (template != null && key == templateKey && template.Body) return template;
            var holder = new GameObject("ToiletTemplate");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);
            try
            {
                template = GlbLoader.Load(path, 1f, 0f, holder.transform);
                ToiletPlugin.Log.LogInfo($"Toilet model {Path.GetFileName(path)}: {template.Triangles} tris, {template.DrawCalls} draw calls");
            }
            catch (System.Exception e) { ToiletPlugin.Log.LogError("Toilet model failed, using built-in: " + e); template = null; }
            templateKey = key;
            return template;
        }

        private Bounds BuildProcedural()
        {
            var white = ModCommon.Solid(new Color(0.95f, 0.95f, 0.93f), 0.8f);
            void P(PrimitiveType t, Vector3 pos, Vector3 scale, Material m)
            {
                var go = GameObject.CreatePrimitive(t);
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(transform, false);
                go.transform.localPosition = pos;
                go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = m;
            }
            P(PrimitiveType.Cylinder, new Vector3(0, 0.2f * s, 0.1f * s), new Vector3(0.42f * s, 0.2f * s, 0.5f * s), white);
            P(PrimitiveType.Cylinder, new Vector3(0, 0.42f * s, 0.12f * s), new Vector3(0.48f * s, 0.025f * s, 0.56f * s), white);
            P(PrimitiveType.Cube, new Vector3(0, 0.6f * s, -0.22f * s), new Vector3(0.48f * s, 0.4f * s, 0.2f * s), white);
            return new Bounds(new Vector3(0, 0.4f * s, 0), new Vector3(0.5f * s, 0.8f * s, 0.75f * s));
        }

        private void BuildFx()
        {
            if (!poopMat)
            {
                poopMat = ModCommon.UnlitMaterial(ModCommon.BlobTexture(32, 0.6f, 77));
                poopDecal = new Material(ModCommon.UnlitMaterial(ModCommon.BlobTexture(96, 1f, 78))) { color = new Color(0.3f, 0.18f, 0.06f, 0.95f) };
                plopClip = PoopAudio.Plop();
                fartClips = new[] { PoopAudio.Fart(11), PoopAudio.Fart(29), PoopAudio.Fart(53) };
            }
            poop = nozzle.gameObject.AddComponent<ParticleSystem>();
            poop.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = poop.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(s * 3f, s * 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(s * 0.08f, s * 0.22f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.2f, 0.11f, 0.03f), new Color(0.13f, 0.07f, 0.02f));
            main.gravityModifier = 0.4f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 600;
            var em = poop.emission; em.rateOverTime = 0f;
            var sh = poop.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 14f; sh.radius = s * 0.06f;
            var col = poop.collision;
            col.enabled = true; col.type = ParticleSystemCollisionType.World; col.collidesWith = ModCommon.GroundMask;
            col.bounce = 0.1f; col.dampen = 0.8f; col.lifetimeLoss = 0.5f; col.quality = ParticleSystemCollisionQuality.Low; col.maxCollisionShapes = 16;
            var r = nozzle.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = poopMat;
            poop.Play();

            synth = gameObject.AddComponent<PoopSynth>();
            oneShot = gameObject.AddComponent<AudioSource>();
            oneShot.spatialBlend = 0.7f; oneShot.minDistance = 25f; oneShot.maxDistance = 500f; oneShot.rolloffMode = AudioRolloffMode.Linear;
        }

        private float Vol => ToiletPlugin.Instance.Volume.Value * ModCommon.GameSfxVolume * 2f;

        public void PlaceNear(ActiveRagdoll near)
        {
            Vector3 root = near.GetRootPosition();
            Vector3 face = Vector3.Cross(near.upperLegRight.transform.position - near.upperLegLeft.transform.position, Vector3.up);
            face.y = 0f;
            if (face.sqrMagnitude < 1e-4f) face = Vector3.forward;
            face.Normalize();
            Vector3 pos = root + face * s * 0.9f + Vector3.up * s;
            Vector3 up = Vector3.up;
            if (Physics.Raycast(pos + Vector3.up * 3f * s, Vector3.down, out var hit, 20f * s, ModCommon.GroundMask, QueryTriggerInteraction.Ignore))
            {
                pos = hit.point + hit.normal * 0.05f;
                up = hit.normal;
            }
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.ProjectOnPlane(-face, up), up));
            rb.position = transform.position; rb.rotation = transform.rotation;
            rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
        }

        public void Sit(ActiveRagdoll r)
        {
            if (Rider) Stand();
            if (!r || !r.spine1 || !r.spine1.rigidBody) return;
            SeatUtil.IgnoreCollisions(r, cols, true);
            Rider = r;
            kseat.Sit(r, rb.velocity);
            if (ModCommon.IsLocal(r)) StageManager.Instance.cameraRig.SetTarget(camProxy);
            oneShot.PlayOneShot(plopClip, 0.6f * Vol);
        }

        public void Stand(Vector3? launch = null)
        {
            var r = Rider;
            Rider = null;
            Thrusting = false;
            if (!r) { kseat.Stand(Vector3.zero); return; }
            if (ModCommon.IsLocal(r) && StageManager.Instance) StageManager.Instance.cameraRig.SetTarget(r.spine1.transform);
            Vector3 v = launch ?? rb.velocity + transform.forward * s * 2f + Vector3.up * s * 3f;
            kseat.Stand(v);
            ModCommon.Unground(r, true);
            StartCoroutine(Restore(r));
        }

        private System.Collections.IEnumerator Restore(ActiveRagdoll r)
        {
            yield return new WaitForSeconds(0.8f);
            if (r && Rider != r) SeatUtil.IgnoreCollisions(r, cols, false);
        }

        private void OnDestroy()
        {
            if (Rider) { CheeseApi.SetSeated(Rider, false); if (ModCommon.IsLocal(Rider) && StageManager.Instance) StageManager.Instance.cameraRig.SetTarget(Rider.spine1.transform); }
        }

        private void FixedUpdate()
        {
            if (!rb) return;
            prevVel = rb.velocity;
            if (Rider != null && !Rider) { Rider = null; kseat.Stand(Vector3.zero); }
            var cfg = ToiletPlugin.Instance;
            bool local = Rider && ModCommon.IsLocal(Rider);
            bool menu = MenuOpen();
            var kb = Keyboard.current;
            bool keys = local && !menu && !ModCommon.Paused && !ModCommon.CountingDown && kb != null;
            bool hold = keys && (cfg.ThrustKey.Value.IsPressed() || kb.gKey.isPressed) || local && ScriptThrust;
            bool hover = keys && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            Thrusting = hold || hover && Altitude > s * 0.3f;

            float g = Mathf.Abs(Physics.gravity.y);
            float total = rb.mass;

            if (Thrusting)
            {
                float upDot = Mathf.Max(0.3f, transform.up.y);
                float accel = hold ? cfg.Power.Value * g : (g - rb.velocity.y * 1.5f) / upDot;
                if (hold && hover) accel = Mathf.Lerp(accel, (g - rb.velocity.y * 1.5f) / upDot, 0.6f);
                rb.AddForce(transform.up * accel * total);
            }

            if (Rider)
            {
                Vector2 move = Vector2.zero;
                if (keys)
                {
                    if (kb.wKey.isPressed) move.y += 1f;
                    if (kb.sKey.isPressed) move.y -= 1f;
                    if (kb.dKey.isPressed) move.x += 1f;
                    if (kb.aKey.isPressed) move.x -= 1f;
                }
                bool airborne = Altitude > s * 0.3f || Thrusting;
                float camYaw = StageManager.Instance && StageManager.Instance.cameraRig ? StageManager.Instance.cameraRig.yaw : transform.eulerAngles.y;
                Vector3 heading = Quaternion.Euler(0f, airborne ? camYaw : transform.eulerAngles.y + move.x * 40f, 0f) * Vector3.forward;
                Vector3 right = Vector3.Cross(Vector3.up, heading);
                Vector3 tilt = airborne ? (heading * move.y + right * move.x) * 0.5f * cfg.AirControl.Value : Vector3.zero;
                Vector3 targetUp = (Vector3.up + tilt).normalized;
                Vector3 upErr = Vector3.Cross(transform.up, targetUp);
                float yawErr = Vector3.SignedAngle(Vector3.ProjectOnPlane(transform.forward, Vector3.up), heading, Vector3.up) * Mathf.Deg2Rad;
                Vector3 torque = upErr * 60f + Vector3.up * yawErr * (airborne ? 12f : 6f) - rb.angularVelocity * 8f;
                rb.AddTorque(torque * (airborne ? 1f : 0.5f), ForceMode.Acceleration);
                if (airborne && move == Vector2.zero)
                {
                    Vector3 lateral = Vector3.ProjectOnPlane(rb.velocity, Vector3.up);
                    rb.AddForce(-lateral * 0.8f * total);
                }
                if (synth) synth.Intensity = Thrusting ? (hold ? 1f : 0.55f) : 0f;
            }

            Altitude = Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out var hit, 500f, ModCommon.GroundMask, QueryTriggerInteraction.Ignore) ? Mathf.Max(0f, hit.distance - 0.5f) : 500f;

            var em = poop.emission;
            em.rateOverTime = Thrusting ? 160f * cfg.PoopAmount.Value : 0f;
            if (Thrusting && Time.time > nextChunk && cfg.PoopAmount.Value > 0f)
            {
                nextChunk = Time.time + 0.12f / cfg.PoopAmount.Value;
                PoopChunk.Spawn(nozzle.position, rb.velocity - transform.up * s * Random.Range(4f, 8f), s, poopMat, poopDecal, plopClip, Vol);
            }
        }

        private static bool MenuOpen() => CheeseApi.MenuOpen;

        private bool wasThrusting;

        private void Update()
        {
            if (synth) synth.Volume = Vol;
            if (Thrusting && !wasThrusting) oneShot.PlayOneShot(fartClips[Random.Range(0, fartClips.Length)], Vol * 0.8f);
            wasThrusting = Thrusting;
            if (Thrusting && Rider && ModCommon.IsLocal(Rider)) StageManager.Instance.cameraRig.SetScreenShake(0.25f, 4f);
        }

        private void LateUpdate()
        {
            if (!camProxy) return;
            Vector3 want = transform.position + Vector3.up * (height + s * 0.6f);
            camProxy.position = (camProxy.position - want).sqrMagnitude > s * s * 25f ? want : Vector3.SmoothDamp(camProxy.position, want, ref camVel, 0.05f);
        }

        private void OnCollisionEnter(Collision c)
        {
            if (!Rider || c.contactCount == 0 || c.collider.GetComponent<RagdollPart>()) return;
            float impact = Mathf.Abs(Vector3.Dot(c.relativeVelocity, c.GetContact(0).normal));
            if (impact > s * 6f) oneShot.PlayOneShot(plopClip, Mathf.Clamp01(impact / 200f) * Vol);
            if (impact > ToiletPlugin.Instance.EjectSpeed.Value)
            {
                var v = prevVel;
                Stand(v * 0.9f + Vector3.up * v.magnitude * 0.2f);
                ModCommon.Toast("Flushed off the throne!");
            }
        }
    }

    internal class PoopChunk : MonoBehaviour
    {
        private static readonly Queue<GameObject> live = new Queue<GameObject>();
        private static readonly Queue<GameObject> stains = new Queue<GameObject>();
        private Material decal;
        private AudioClip plop;
        private float vol, scale, born;

        public static void Spawn(Vector3 pos, Vector3 vel, float s, Material mat, Material decal, AudioClip plop, float vol)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "PoopChunk";
            float sz = s * Random.Range(0.08f, 0.15f);
            go.transform.position = pos;
            go.transform.rotation = Random.rotation;
            go.transform.localScale = new Vector3(sz, sz * 1.6f, sz);
            go.GetComponent<Renderer>().sharedMaterial = ModCommon.Solid(new Color(0.22f, 0.12f, 0.04f), 0.6f);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.1f;
            rb.velocity = vel + Random.insideUnitSphere * s;
            rb.angularVelocity = Random.insideUnitSphere * 10f;
            var c = go.AddComponent<PoopChunk>();
            c.decal = decal; c.plop = plop; c.vol = vol; c.scale = s; c.born = Time.time;
            Destroy(go, 8f);
            live.Enqueue(go);
            while (live.Count > 60) { var o = live.Dequeue(); if (o) Destroy(o); }
        }

        private void OnCollisionEnter(Collision col)
        {
            if (Time.time - born < 0.15f || col.contactCount == 0) return;
            if (CheeseApi.IsVehicle(col.rigidbody)) return;
            var ct = col.GetContact(0);
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "PoopStain";
            q.transform.position = ct.point + ct.normal * 0.03f;
            q.transform.rotation = Quaternion.LookRotation(-ct.normal) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
            float sz = scale * Random.Range(0.3f, 0.6f);
            q.transform.localScale = new Vector3(sz, sz * Random.Range(0.6f, 1.2f), 1f);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = decal;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Destroy(q, 45f);
            stains.Enqueue(q);
            while (stains.Count > 150) { var o = stains.Dequeue(); if (o) Destroy(o); }
            if (Random.value < 0.4f) AudioSource.PlayClipAtPoint(plop, ct.point, vol * 0.5f);
            Destroy(gameObject);
        }
    }

    internal static class PoopAudio
    {
        private const int Rate = 44100;

        public static AudioClip Fart(int seed)
        {
            var rng = new System.Random(seed);
            float R() => (float)rng.NextDouble();
            float dur = 0.45f + R() * 0.6f;
            int n = (int)(dur * Rate);
            var d = new float[n];
            float f0 = 55f + R() * 60f, fEnd = f0 * (0.6f + R() * 0.4f), flutterHz = 14f + R() * 24f, depth = 0.4f + R() * 0.4f;
            float phase = 0f, lp = 0f, lp2 = 0f, cutoff = 0.08f + R() * 0.07f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                phase += Mathf.Lerp(f0, fEnd, t) / Rate; phase -= Mathf.Floor(phase);
                float pulse = phase < 0.3f ? 1f : -0.43f;
                float flutter = 1f - depth * (0.5f + 0.5f * Mathf.Sin(i / (float)Rate * flutterHz * Mathf.PI * 2f));
                float x = pulse * flutter + (R() * 2f - 1f) * 0.3f;
                lp += cutoff * (x - lp); lp2 += cutoff * 1.6f * (lp - lp2);
                d[i] = Mathf.Clamp(lp2 * Mathf.Min(1f, t * 25f) * Mathf.Pow(1f - t, 0.7f) * 2.4f, -1f, 1f);
            }
            var c = AudioClip.Create("fart" + seed, n, 1, Rate, false); c.SetData(d, 0); return c;
        }

        public static AudioClip Plop()
        {
            var rng = new System.Random(9);
            int n = (int)(Rate * 0.35f);
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float f = 260f + 900f * Mathf.Exp(-t * 30f);          // falling pitch "bloop"
                float bloop = Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t * 14f);
                float x = (float)rng.NextDouble() * 2f - 1f;
                lp += 0.3f * (x - lp);
                float splash = lp * Mathf.Exp(-t * 35f) * 0.7f + x * Mathf.Exp(-t * 80f) * 0.2f;
                float wet = Mathf.Sin(2f * Mathf.PI * 140f * t) * Mathf.Exp(-t * 25f) * 0.5f; // squelchy body
                d[i] = (float)System.Math.Tanh((bloop + splash + wet) * 1.4f) * 0.85f;
            }
            var c = AudioClip.Create("plop", n, 1, Rate, false); c.SetData(d, 0); return c;
        }
    }
}
