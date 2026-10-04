using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modz
{
    [BepInPlugin(GUID, "Guns", "2.1.1")]
    [BepInDependency(CorePlugin.GUID)]
    public class GunsPlugin : BaseUnityPlugin
    {
        public const string GUID = "modz.guns";
        internal static ManualLogSource Log;
        public static GunsPlugin Instance;

        public ConfigEntry<bool> Enabled, InfiniteAmmo, Crosshair;
        public ConfigEntry<KeyboardShortcut> PistolKey, RifleKey, HolsterKey, FireKey, AimKey, ReloadKey;
        public ConfigEntry<float> Damage, Recoil, Volume, AimZoom, GunSize;

        private readonly GunDef[] defs =
        {
            new GunDef { Name = "Glock 17", File = "glock.glb", Length = 1.9f, VisualBoost = 1.5f, Mag = 17, Rpm = 900, Auto = false, Power = 150f,
                Spread = 0.6f, RecoilPitch = 2.4f, Reload = 1.6f, Shots = new[] { "pistol_shot1", "pistol_shot2" }, ReloadSound = "pistol_reload",
                GripZ = 0.22f, GripY = 0.3f, MuzzleY = 0.8f, SupportZ = 0.24f, TwoHandGrip = true },
            new GunDef { Name = "AK-47", File = "ak47.glb", Length = 8.8f, VisualBoost = 1f, Mag = 30, Rpm = 600, Auto = true, Power = 200f,
                Spread = 1.1f, RecoilPitch = 1.1f, Reload = 2.4f, Shots = new[] { "rifle_shot1", "rifle_shot2", "rifle_shot3" }, ReloadSound = "rifle_reload",
                GripZ = 0.36f, GripY = 0.28f, MuzzleY = 0.72f, SupportZ = 0.62f, TwoHandGrip = false },
        };

        internal GunDef current;
        public static int ScriptShots;
        private GameObject gunGo;
        private Transform muzzle;
        private AudioSource gunAudio;
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private float nextShot, reloadUntil, kick, hitMarkerUntil, spreadBloom;
        internal bool aiming;
        internal float aimBlend;
        private ActiveRagdoll owner;
        private Rigidbody gunBody;              // kinematic, unscaled; hands are jointed to it
        private Transform model;                 // scaled visual child
        private readonly List<ConfigurableJoint> handJoints = new List<ConfigurableJoint>();
        private Vector3 holdPos, holdVel;
        private Quaternion holdRot = Quaternion.identity;
        private float tumbleTime;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Realistic Glock 17 and AK-47.");
            PistolKey = Config.Bind("Keys", "Glock", new KeyboardShortcut(KeyCode.Alpha1), "Draw the pistol.");
            RifleKey = Config.Bind("Keys", "AK-47", new KeyboardShortcut(KeyCode.Alpha2), "Draw the rifle.");
            HolsterKey = Config.Bind("Keys", "Holster", new KeyboardShortcut(KeyCode.Alpha3), "Put the gun away.");
            FireKey = Config.Bind("Keys", "Fire", new KeyboardShortcut(KeyCode.Mouse0), "Shoot.");
            AimKey = Config.Bind("Keys", "Aim down sights", new KeyboardShortcut(KeyCode.Mouse1), "Zoom in, steadier shots.");
            ReloadKey = Config.Bind("Keys", "Reload", new KeyboardShortcut(KeyCode.R), "Reload.");
            Damage = Config.Bind("Guns", "Damage", 1f, ModCommon.Desc("Bullet damage multiplier.", new AcceptableValueRange<float>(0.2f, 5f)));
            InfiniteAmmo = Config.Bind("Guns", "Infinite ammo", false, "Never reload.");
            Volume = Config.Bind("Guns", "Volume", 0.8f, ModCommon.Desc("Gunshot volume (on top of the game's Master x SFX).", new AcceptableValueRange<float>(0f, 1f)));
            Recoil = Config.Bind("Guns", "Recoil", 1f, ModCommon.Desc("Camera kick per shot.", new AcceptableValueRange<float>(0f, 3f), true));
            AimZoom = Config.Bind("Guns", "Aim zoom", 0.6f, ModCommon.Desc("Field of view while aiming (1 = no zoom).", new AcceptableValueRange<float>(0.3f, 1f), true));
            GunSize = Config.Bind("Guns", "Gun size", 1f, ModCommon.Desc("Visual scale of the guns.", new AcceptableValueRange<float>(0.5f, 2f), true));
            Crosshair = Config.Bind("Guns", "Crosshair", true, ModCommon.Desc("Show a crosshair while a gun is out.", advanced: true));
            Enabled.SettingChanged += (_, __) => { if (!Enabled.Value) Holster(); };

            string dir = Path.GetDirectoryName(typeof(GunsPlugin).Assembly.Location);
            string sdir = Path.Combine(dir, "sounds");
            if (Directory.Exists(sdir))
                foreach (var f in Directory.GetFiles(sdir, "*.wav"))
                    try { clips[Path.GetFileNameWithoutExtension(f)] = Wav.Load(f); } catch (System.Exception e) { Log.LogWarning(e.Message); }

            CheeseApi.HandsBusyCheck = r => GunOut && owner == r;
            MenuRegistry.Action(GUID, "Glock", () => Equip(defs[0]));
            MenuRegistry.Action(GUID, "AK-47", () => Equip(defs[1]));
            MenuRegistry.Action(GUID, "Holster", Holster);

            new Harmony(GUID).PatchAll(typeof(GunPatches));
            Log.LogInfo($"Guns loaded ({clips.Count} sounds)");
        }

        internal bool GunOut => current != null && gunGo && owner;

        private void Update()
        {
            if (!Enabled.Value || !ModCommon.InRound) { if (gunGo) Holster(); return; }
            var me = ModCommon.LocalRagdoll();
            if (!me || CheeseApi.IsSeated(me) || IsDead(me)) { if (gunGo) Holster(); return; }
            bool menu = MenuOpen();
            if (!menu && !ModCommon.Paused)
            {
                if (ModCommon.KeyDown(PistolKey.Value)) Equip(current == defs[0] ? null : defs[0]);
                if (ModCommon.KeyDown(RifleKey.Value)) Equip(current == defs[1] ? null : defs[1]);
                if (ModCommon.KeyDown(HolsterKey.Value)) Holster();
            }
            if (!GunOut) return;
            if (owner != me) { Holster(); return; }

            aiming = !menu && Pressed(AimKey.Value);
            aimBlend = Mathf.MoveTowards(aimBlend, aiming ? 1f : 0f, Time.deltaTime * 6f);
            spreadBloom = Mathf.MoveTowards(spreadBloom, 0f, Time.deltaTime * 4f);
            kick = Mathf.MoveTowards(kick, 0f, Time.deltaTime * 6f);

            if (menu || ModCommon.Paused || ModCommon.CountingDown) return;
            if (Time.time < reloadUntil) return;
            if (current.Ammo <= 0 && !InfiniteAmmo.Value && current.Mag > 0 && Pressed(FireKey.Value) && Down(FireKey.Value)) PlayClip("dry_fire", 0.8f);
            if (ModCommon.KeyDown(ReloadKey.Value) && current.Ammo < current.Mag && !InfiniteAmmo.Value) StartReload();
            bool trigger = (current.Auto ? Pressed(FireKey.Value) : Down(FireKey.Value)) || ScriptShots > 0;
            if (trigger && Time.time >= nextShot && (current.Ammo > 0 || InfiniteAmmo.Value))
            {
                nextShot = Time.time + 60f / current.Rpm;
                if (ScriptShots > 0) ScriptShots--;
                Shoot(me);
                if (!InfiniteAmmo.Value && --current.Ammo <= 0) StartReload();
            }
        }

        private void StartReload()
        {
            reloadUntil = Time.time + current.Reload;
            PlayClip(current.ReloadSound, 0.9f);
            current.Ammo = current.Mag;
        }

        public void Equip(GunDef def)
        {
            Holster();
            if (def == null) return;
            if (!ModCommon.Active) { ModCommon.Toast("Mods only work in Play Offline."); return; }
            var me = ModCommon.LocalRagdoll();
            if (!me || CheeseApi.IsSeated(me)) return;
            var tpl = def.Template();
            if (tpl == null) { ModCommon.Toast($"{def.Name} model missing ({def.File})"); return; }
            owner = me;
            current = def;
            if (current.Ammo < 0) current.Ammo = current.Mag;
            float sc = ModCommon.BodyScale(me) / 9f;
            gunGo = new GameObject("HeldGun");
            gunBody = gunGo.AddComponent<Rigidbody>();
            gunBody.isKinematic = true;
            gunBody.interpolation = RigidbodyInterpolation.Interpolate;
            var vis = Instantiate(tpl.Root, gunGo.transform, false);
            vis.name = "model";
            vis.transform.localScale = Vector3.one * def.Length * def.VisualBoost * GunSize.Value * sc;
            vis.SetActive(true);
            model = vis.transform;
            muzzle = new GameObject("muzzle").transform;
            muzzle.SetParent(model, false);
            muzzle.localPosition = tpl.Point(0.5f, def.MuzzleY);
            holdPos = TargetPose(me, out holdRot);
            gunGo.transform.SetPositionAndRotation(holdPos, holdRot);
            gunBody.position = holdPos; gunBody.rotation = holdRot;
            AttachHand(me.handRight, GripLocal());
            AttachHand(me.handLeft, SupportLocal());
            gunAudio = gunGo.AddComponent<AudioSource>();
            gunAudio.spatialBlend = 0.4f; gunAudio.minDistance = 30f; gunAudio.maxDistance = 800f; gunAudio.rolloffMode = AudioRolloffMode.Linear;
            PlayClip(def.ReloadSound, 0.4f);
        }

        public void Holster()
        {
            foreach (var j in handJoints) if (j) Destroy(j);
            handJoints.Clear();
            if (gunGo) Destroy(gunGo);
            gunGo = null; current = null; owner = null; aiming = false; tumbleTime = 0f;
        }

        private static bool IsDead(ActiveRagdoll r) => CheeseApi.IsDead(r);

        private static bool MenuOpen() => CheeseApi.MenuOpen;

        private static bool Pressed(KeyboardShortcut k)
        {
            if (!Application.isFocused) return false;
            var m = Mouse.current;
            switch (k.MainKey)
            {
                case KeyCode.Mouse0: return m != null && m.leftButton.isPressed;
                case KeyCode.Mouse1: return m != null && m.rightButton.isPressed;
                case KeyCode.Mouse2: return m != null && m.middleButton.isPressed;
                default: return k.IsPressed();
            }
        }

        private static bool Down(KeyboardShortcut k)
        {
            if (!Application.isFocused) return false;
            var m = Mouse.current;
            switch (k.MainKey)
            {
                case KeyCode.Mouse0: return m != null && m.leftButton.wasPressedThisFrame;
                case KeyCode.Mouse1: return m != null && m.rightButton.wasPressedThisFrame;
                case KeyCode.Mouse2: return m != null && m.middleButton.wasPressedThisFrame;
                default: return k.IsDown();
            }
        }

        private void PlayClip(string name, float vol)
        {
            if (gunAudio && clips.TryGetValue(name, out var c))
            {
                gunAudio.pitch = Random.Range(0.96f, 1.04f);
                gunAudio.PlayOneShot(c, vol * Volume.Value * ModCommon.GameSfxVolume * 2.5f);
            }
        }

        private Vector3 lastAim;

        internal Vector3 AimPoint(ActiveRagdoll me)
        {
            var rig = StageManager.Instance.cameraRig;
            var cam = rig.mainCamera.transform;
            var ray = FirstPerson()
                ? new Ray(cam.position, cam.forward)
                : new Ray(me.spine2.transform.position + Vector3.up * ModCommon.BodyScale(me) * 0.2f, Quaternion.Euler(rig.pitch, rig.yaw, 0f) * Vector3.forward);
            float best = float.MaxValue;
            Vector3 p = ray.GetPoint(1500f);
            foreach (var h in Physics.RaycastAll(ray, 3000f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.distance >= best || IsMine(h.collider, me)) continue;
                best = h.distance; p = h.point;
            }
            return lastAim = p;
        }

        private static bool IsMine(Collider c, ActiveRagdoll me)
        {
            var part = c.GetComponent<RagdollPart>();
            return part && part.ragdoll == me;
        }

        private float ModelScale => model ? model.localScale.x : 1f;
        private Vector3 GripLocal() => current.Template().Point(-0.5f + current.GripZ, current.GripY) * ModelScale;
        private Vector3 SupportLocal() => current.TwoHandGrip
            ? current.Template().Point(-0.5f + current.GripZ + 0.04f, current.GripY * 0.9f) * ModelScale
            : current.Template().Point(-0.5f + current.SupportZ, current.GripY * 1.25f) * ModelScale;
        private Vector3 StockLocal() => current.Template().Point(-0.5f, current.GripY + 0.2f) * ModelScale;

        private void AttachHand(RagdollPart hand, Vector3 localAnchorOnGun)
        {
            if (!hand || !hand.rigidBody || hand.rigidBody.isKinematic || hand.transform.localScale.x < 0.01f) return;
            if (hand is RagdollHand rh) rh.BreakHold();
            var j = hand.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = gunBody;
            j.autoConfigureConnectedAnchor = false;
            j.anchor = Vector3.zero;
            j.connectedAnchor = localAnchorOnGun;
            float m = hand.rigidBody.mass;
            var drive = new JointDrive { positionSpring = m * 3000f, positionDamper = m * 90f, maximumForce = m * 900f };
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Free;
            j.xDrive = j.yDrive = j.zDrive = drive;
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Free;
            j.enableCollision = false;
            j.enablePreprocessing = false;
            handJoints.Add(j);
        }

        private bool FirstPerson() => CheeseApi.FirstPerson;

        private Vector3 TargetPose(ActiveRagdoll me, out Quaternion rot)
        {
            float sc = ModCommon.BodyScale(me);
            var cam = StageManager.Instance.cameraRig.mainCamera.transform;
            Vector3 aim = AimPoint(me);
            Vector3 up = Vector3.up;
            Vector3 pos;
            if (FirstPerson())
            {
                Vector3 hip = cam.position + cam.right * sc * 0.13f - cam.up * sc * 0.12f + cam.forward * sc * 0.36f;
                Vector3 ads = cam.position - cam.up * sc * (current.Auto ? 0.055f : 0.06f) + cam.forward * sc * 0.3f;
                Vector3 grip = Vector3.Lerp(hip, ads, aimBlend);
                rot = Quaternion.LookRotation(aim - grip, cam.up);
                pos = grip - rot * GripLocal();
            }
            else
            {
                Vector3 chest = me.spine2.transform.position;
                Vector3 facing = ChestForward(me);
                Vector3 flatAim = ClampAim(Vector3.ProjectOnPlane(aim - chest, Vector3.up), facing);
                Vector3 right = Vector3.Cross(Vector3.up, flatAim);
                if (current.Auto)
                {
                    Vector3 shoulder = chest + right * sc * 0.12f + up * sc * Mathf.Lerp(0.12f, 0.3f, aimBlend);
                    rot = Quaternion.LookRotation(ClampAim(aim - shoulder, facing), up);
                    pos = shoulder - rot * StockLocal();
                }
                else
                {
                    Vector3 grip = chest + flatAim * sc * Mathf.Lerp(0.55f, 0.62f, aimBlend) + up * sc * Mathf.Lerp(0.12f, 0.38f, aimBlend) + right * sc * 0.03f;
                    rot = Quaternion.LookRotation(ClampAim(aim - grip, facing), up);
                    pos = grip - rot * GripLocal();
                }
            }
            float speed = me.velocity.magnitude;
            float bob = Mathf.Sin(Time.time * 9f) * Mathf.Clamp01(speed / 60f) * sc * 0.012f * (1f - aimBlend * 0.7f);
            pos += up * bob + rot * Vector3.right * Mathf.Sin(Time.time * 4.5f) * bob * 0.8f;
            rot *= Quaternion.Euler(Mathf.Sin(Time.time * 1.3f) * 0.4f * (1f - aimBlend), Mathf.Sin(Time.time * 0.9f) * 0.4f * (1f - aimBlend), 0f);
            rot *= Quaternion.Euler(-kick * (current.Auto ? 4f : 9f), 0f, 0f);
            pos -= rot * Vector3.forward * kick * sc * (current.Auto ? 0.05f : 0.035f);
            return pos;
        }

        internal static Vector3 ChestForward(ActiveRagdoll r)
        {
            Vector3 right = Vector3.ProjectOnPlane(r.upperArmRight.transform.position - r.upperArmLeft.transform.position, Vector3.up);
            return right.sqrMagnitude > 1e-4f ? Vector3.Cross(right, Vector3.up).normalized : Vector3.forward;
        }

        private static Vector3 ClampAim(Vector3 dir, Vector3 facing)
        {
            Vector3 flat = Vector3.ProjectOnPlane(dir, Vector3.up);
            float len = dir.magnitude;
            if (len < 1e-4f || flat.sqrMagnitude < 1e-6f) return facing;
            Vector3 f = Vector3.RotateTowards(facing, flat.normalized, 55f * Mathf.Deg2Rad, 0f);
            return (f * flat.magnitude + Vector3.up * dir.y) / len;
        }

        private void FixedUpdate()
        {
            if (!GunOut || !gunBody) return;
            var me = owner;
            bool knockedDown = me.head && me.spine1 && Vector3.Dot((me.head.transform.position - me.spine1.transform.position).normalized, Vector3.up) < 0.5f;
            tumbleTime = knockedDown ? tumbleTime + Time.fixedDeltaTime : 0f;
            if (tumbleTime > 0.5f) { Drop(); return; }

            Vector3 target = TargetPose(me, out var trot);
            bool fp = FirstPerson();
            float k = 1f - Mathf.Exp(-Time.fixedDeltaTime * (fp ? 40f : 22f));
            holdPos = (holdPos - target).sqrMagnitude > ModCommon.BodyScale(me) * ModCommon.BodyScale(me) ? target : Vector3.Lerp(holdPos, target, k);
            holdRot = Quaternion.Slerp(holdRot, trot, k);
            gunBody.MovePosition(holdPos);
            gunBody.MoveRotation(holdRot);
        }

        private void LateUpdate()
        {
            if (!GunOut || !FirstPerson()) return;
            var p = TargetPose(owner, out var r);
            gunGo.transform.SetPositionAndRotation(p, r);
        }

        private void Drop()
        {
            if (!gunGo) return;
            foreach (var j in handJoints) if (j) Destroy(j);
            handJoints.Clear();
            var go = gunGo;
            gunGo = null;
            var b = current.Template().B;
            var col = go.AddComponent<BoxCollider>();
            col.center = b.center * ModelScale;
            col.size = b.size * ModelScale;
            gunBody.isKinematic = false;
            gunBody.mass = 1f;
            gunBody.velocity = owner && owner.spine1 ? owner.spine1.rigidBody.velocity : Vector3.zero;
            gunBody.angularVelocity = Random.insideUnitSphere * 6f;
            Destroy(go, 30f);
            ModCommon.Toast($"Dropped the {current.Name} ({ModCommon.Key(current == defs[0] ? PistolKey.Value : RifleKey.Value)} to draw again)");
            current = null; owner = null; aiming = false; tumbleTime = 0f;
        }

        private void Shoot(ActiveRagdoll me)
        {
            float sc = ModCommon.BodyScale(me);
            Vector3 aim = AimPoint(me);
            Vector3 from = muzzle.position;
            float spread = current.Spread * (aiming ? 0.35f : 1f) * (1f + spreadBloom) * (me.velocity.magnitude > 20f ? 1.6f : 1f);
            Vector3 dir = (aim - from).normalized;
            if (Vector3.Angle(dir, muzzle.forward) > 15f) dir = muzzle.forward;
            dir = Quaternion.AngleAxis(Random.Range(0f, 360f), dir) * (Quaternion.AngleAxis(Random.Range(0f, spread), Vector3.Cross(dir, Vector3.up).normalized) * dir);
            spreadBloom = Mathf.Min(2f, spreadBloom + 0.35f);

            var hits = Physics.RaycastAll(from, dir, 3000f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance);
            Vector3 end = from + dir * 1500f;
            foreach (var h in hits)
            {
                if (IsMine(h.collider, me)) continue;
                end = h.point;
                Impact(h, dir, sc);
                break;
            }

            PlayClip(current.Shots[Random.Range(0, current.Shots.Length)], 1f);
            Fx.MuzzleFlash(muzzle, sc);
            Fx.Tracer(from, end, sc);
            Fx.Shell(model, sc, current.Length);
            kick = 1f;
            var rig = StageManager.Instance.cameraRig;
            float k = current.RecoilPitch * Recoil.Value * (aiming ? 0.6f : 1f);
            rig.pitch -= k;
            rig.yaw += Random.Range(-0.35f, 0.35f) * k;
            rig.SetScreenShake(0.15f * Recoil.Value, 8f);
        }

        private void Impact(RaycastHit h, Vector3 dir, float sc)
        {
            float power = current.Power * Damage.Value;
            var part = h.collider.GetComponent<RagdollPart>();
            if (part && part.ragdoll)
            {
                CheeseApi.BulletHit?.Invoke(part, h.point, dir, power);
                if (part.rigidBody && !part.rigidBody.isKinematic) part.rigidBody.AddForce(dir * (current.Auto ? 28f : 22f), ForceMode.VelocityChange);
                bool core = part == part.ragdoll.head || part == part.ragdoll.spine1 || part == part.ragdoll.spine2;
                if (core || Random.value < 0.3f) ModCommon.Unground(part.ragdoll, true);
                hitMarkerUntil = Time.time + 0.15f;
                return;
            }
            if (h.rigidbody && !h.rigidbody.isKinematic)
                h.rigidbody.AddForceAtPosition(dir * Mathf.Min(h.rigidbody.mass, 50f) * 12f, h.point, ForceMode.Impulse);
            bool metal = CheeseApi.IsVehicle(h.rigidbody);
            Fx.ImpactPuff(h.point, h.normal, sc, metal);
            if (!h.rigidbody) Fx.BulletHole(h.point, h.normal, sc);
        }

        private GUIStyle ammoStyle, hintStyle;

        private void OnGUI()
        {
            if (!GunOut) return;
            if (ammoStyle == null)
            {
                ammoStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight, normal = { textColor = Color.white } };
                hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.LowerRight, normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } };
            }
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            var camera = StageManager.Instance ? StageManager.Instance.cameraRig.mainCamera : null;
            if (!FirstPerson() && camera)
            {
                Vector3 vp = camera.WorldToViewportPoint(lastAim);
                if (vp.z > 0f) { cx = vp.x * Screen.width; cy = (1f - vp.y) * Screen.height; }
            }
            if (Crosshair.Value)
            {
                float gap = 6f + (current.Spread * (aiming ? 0.35f : 1f) * (1f + spreadBloom)) * 6f;
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                GUI.DrawTexture(new Rect(cx - gap - 8, cy - 1, 8, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + gap, cy - 1, 8, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1, cy - gap - 8, 2, 8), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1, cy + gap, 2, 8), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1, cy - 1, 2, 2), Texture2D.whiteTexture);
                if (Time.time < hitMarkerUntil)
                {
                    GUI.color = new Color(1f, 0.15f, 0.1f, 0.95f);
                    var m = GUI.matrix;
                    for (int i = 0; i < 4; i++)
                    {
                        GUIUtility.RotateAroundPivot(45f + i * 90f, new Vector2(cx, cy));
                        GUI.DrawTexture(new Rect(cx + 6, cy - 1, 9, 2), Texture2D.whiteTexture);
                        GUI.matrix = m;
                    }
                }
                GUI.color = Color.white;
            }
            string ammo = InfiniteAmmo.Value ? "∞" : Time.time < reloadUntil ? "reloading…" : $"{current.Ammo} / {current.Mag}";
            GUI.Label(new Rect(Screen.width - 420, Screen.height - 150, 400, 40), $"{current.Name}   {ammo}", ammoStyle);
            GUI.Label(new Rect(Screen.width - 520, Screen.height - 112, 500, 20),
                $"{ModCommon.Key(FireKey.Value)} fire · {ModCommon.Key(AimKey.Value)} aim · {ModCommon.Key(ReloadKey.Value)} reload · {ModCommon.Key(PistolKey.Value)}/{ModCommon.Key(RifleKey.Value)} switch · {ModCommon.Key(HolsterKey.Value)} holster", hintStyle);
        }
    }

    public class GunDef
    {
        public string Name, File, ReloadSound;
        public string[] Shots;
        public float Length, VisualBoost, Power, Spread, RecoilPitch, Reload, GripZ, GripY, MuzzleY, SupportZ;
        public int Mag, Rpm, Ammo = -1;
        public bool Auto, TwoHandGrip;
        private GunTemplate tpl;
        private bool tried;

        public GunTemplate Template()
        {
            if (tpl != null && tpl.Root) return tpl;
            if (tried && tpl == null) return null;
            tried = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(GunsPlugin).Assembly.Location), File);
            if (!System.IO.File.Exists(path)) return null;
            try { tpl = GunTemplate.Load(path); GunsPlugin.Log.LogInfo($"{Name}: {tpl.Model.Triangles} tris, flipped={tpl.Flipped}"); }
            catch (System.Exception e) { GunsPlugin.Log.LogError($"{Name} model failed: {e}"); }
            return tpl;
        }
    }

    public class GunTemplate
    {
        public GlbLoader.CarModel Model;
        public GameObject Root;
        public Bounds B;
        public bool Flipped;

        public Vector3 Point(float z, float yFrac) => new Vector3(B.center.x, B.min.y + B.size.y * yFrac, z * B.size.z + B.center.z);

        public static GunTemplate Load(string path)
        {
            var holder = new GameObject("GunTemplates_" + Path.GetFileNameWithoutExtension(path));
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);
            var root = new GameObject("gun");
            root.transform.SetParent(holder.transform, false);
            var model = GlbLoader.Load(path, 1f, 0f, root.transform);
            var parts = new List<GameObject> { model.Body };
            if (model.Wheels != null) parts.AddRange(model.Wheels); // shouldn't happen for guns
            float minZ = model.BodyBounds.min.z, maxZ = model.BodyBounds.max.z, len = maxZ - minZ;
            float fLo = float.MaxValue, fHi = float.MinValue, bLo = float.MaxValue, bHi = float.MinValue;
            foreach (var mf in model.Body.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || !mf.sharedMesh.isReadable) continue;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    if (v.z > maxZ - len * 0.18f) { fLo = Mathf.Min(fLo, v.y); fHi = Mathf.Max(fHi, v.y); }
                    if (v.z < minZ + len * 0.18f) { bLo = Mathf.Min(bLo, v.y); bHi = Mathf.Max(bHi, v.y); }
                }
            }
            var t = new GunTemplate { Model = model, Root = root, B = model.BodyBounds };
            if (fHi - fLo > bHi - bLo)
            {
                t.Flipped = true;
                model.Body.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                var c = t.B.center; c.x = -c.x; c.z = -c.z; t.B.center = c;
            }
            return t;
        }
    }

    internal static class Fx
    {
        private static Material flashMat, tracerMat, holeMat, dustMat, brassMat;
        private static readonly Queue<GameObject> holes = new Queue<GameObject>(), shells = new Queue<GameObject>();

        private static void Init()
        {
            if (flashMat) return;
            flashMat = new Material(ModCommon.UnlitMaterial(ModCommon.BlobTexture(32, 0.5f, 61))) { color = new Color(1f, 0.85f, 0.45f, 1f) };
            tracerMat = new Material(ModCommon.UnlitMaterial()) { color = new Color(1f, 0.9f, 0.6f, 0.7f) };
            holeMat = new Material(ModCommon.UnlitMaterial(ModCommon.BlobTexture(32, 0.8f, 62))) { color = new Color(0.06f, 0.05f, 0.05f, 0.92f) };
            dustMat = ModCommon.UnlitMaterial(ModCommon.BlobTexture(32, 0.4f, 63));
            brassMat = ModCommon.Solid(new Color(0.8f, 0.62f, 0.25f), 0.85f, 0.8f);
        }

        public static void MuzzleFlash(Transform muzzle, float sc)
        {
            Init();
            var go = new GameObject("MuzzleFlash");
            go.transform.SetPositionAndRotation(muzzle.position, muzzle.rotation);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main; main.loop = false; main.duration = 0.05f;
            main.startLifetime = 0.045f; main.startSpeed = new ParticleSystem.MinMaxCurve(sc * 0.5f, sc * 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(sc * 0.12f, sc * 0.3f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; em.rateOverTime = 0f;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 12f; sh.radius = 0.01f;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = flashMat;
            ps.Play(); ps.Emit(6);
            var light = go.AddComponent<Light>();
            light.type = LightType.Point; light.color = new Color(1f, 0.75f, 0.4f); light.range = sc * 4f; light.intensity = 3f;
            Object.Destroy(light, 0.04f);
            Object.Destroy(go, 0.3f);
        }

        public static void Tracer(Vector3 a, Vector3 b, float sc)
        {
            Init();
            var go = new GameObject("Tracer");
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            Vector3 dir = b - a;
            float len = dir.magnitude;
            Vector3 s0 = a + dir.normalized * Mathf.Min(len * 0.1f, sc * 2f);
            Vector3 s1 = a + dir.normalized * Mathf.Min(len, sc * 25f);
            lr.SetPosition(0, s0); lr.SetPosition(1, s1);
            lr.startWidth = sc * 0.018f; lr.endWidth = sc * 0.006f;
            lr.sharedMaterial = tracerMat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Object.Destroy(go, 0.04f);
        }

        public static void Shell(Transform gun, float sc, float gunLen)
        {
            Init();
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Shell";
            float d = sc * (gunLen > 4f ? 0.012f : 0.01f);
            go.transform.localScale = new Vector3(d, d * 2.2f, d);
            go.transform.position = gun.position + gun.up * sc * 0.06f + gun.right * sc * 0.04f;
            go.transform.rotation = gun.rotation * Quaternion.Euler(0f, 0f, 90f);
            go.GetComponent<Renderer>().sharedMaterial = brassMat;
            var col = go.GetComponent<Collider>();
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.01f;
            rb.velocity = (gun.right * 1f + gun.up * 0.6f - gun.forward * 0.2f) * sc * Random.Range(1.2f, 1.8f);
            rb.angularVelocity = Random.insideUnitSphere * 30f;
            Object.Destroy(go, 5f);
            shells.Enqueue(go);
            while (shells.Count > 60) { var o = shells.Dequeue(); if (o) Object.Destroy(o); }
        }

        public static void ImpactPuff(Vector3 p, Vector3 n, float sc, bool metal)
        {
            Init();
            var go = new GameObject("BulletImpact");
            go.transform.SetPositionAndRotation(p, Quaternion.LookRotation(n));
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main; main.loop = false; main.duration = 0.1f;
            main.startLifetime = metal ? new ParticleSystem.MinMaxCurve(0.15f, 0.4f) : new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(sc * 0.5f, sc * (metal ? 4f : 1.5f));
            main.startSize = metal ? new ParticleSystem.MinMaxCurve(sc * 0.015f, sc * 0.03f) : new ParticleSystem.MinMaxCurve(sc * 0.08f, sc * 0.2f);
            main.startColor = metal ? new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.4f), new Color(1f, 0.6f, 0.2f)) : new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.5f, 0.42f, 0.7f));
            main.gravityModifier = metal ? 0.4f : 0.05f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; em.rateOverTime = 0f;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 35f; sh.radius = 0.01f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = dustMat;
            if (metal) { r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.03f; }
            ps.Play(); ps.Emit(metal ? 12 : 8);
            Object.Destroy(go, 1.2f);
        }

        public static void BulletHole(Vector3 p, Vector3 n, float sc)
        {
            Init();
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(q.GetComponent<Collider>());
            q.name = "BulletHole";
            q.transform.position = p + n * 0.02f;
            q.transform.rotation = Quaternion.LookRotation(-n) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
            q.transform.localScale = Vector3.one * sc * Random.Range(0.04f, 0.06f);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = holeMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Object.Destroy(q, 60f);
            holes.Enqueue(q);
            while (holes.Count > 150) { var o = holes.Dequeue(); if (o) Object.Destroy(o); }
        }
    }

    internal static class GunPatches
    {
        [HarmonyPrefix, HarmonyPatch(typeof(ActiveRagdoll), nameof(ActiveRagdoll.Input))]
        private static void NoReachWithGun(ActiveRagdoll __instance, ref RagdollInput input)
        {
            var g = GunsPlugin.Instance;
            if (g && g.GunOut && ModCommon.IsLocal(__instance) && (input.TargetCheese || input.TargetRagdolls))
                input = new RagdollInput(false, false, input.Jump, input.Movement, input.InputNumber);
        }

        private static bool Holding(ActiveRagdoll r) => GunsPlugin.Instance && GunsPlugin.Instance.GunOut && ModCommon.IsLocal(r);

        [HarmonyPrefix, HarmonyPriority(Priority.High), HarmonyPatch(typeof(ActiveRagdoll), "SetArmDrive")]
        private static void LimpArms(ActiveRagdoll __instance, ref float drive, ref float damper)
        {
            if (Holding(__instance)) { drive *= 0.04f; damper *= 0.3f; }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(ActiveRagdoll), "SwingArms")]
        private static bool NoArmSwing(ActiveRagdoll __instance) => !Holding(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(ActiveRagdoll), "SetRootTargetRotation")]
        private static void FaceAim(ActiveRagdoll __instance, ref Vector3 heading)
        {
            var g = GunsPlugin.Instance;
            if (!g || !g.GunOut || !ModCommon.IsLocal(__instance) || !StageManager.Instance) return;
            Vector3 f = Vector3.ProjectOnPlane(StageManager.Instance.cameraRig.mainCamera.transform.forward, Vector3.up);
            if (f.sqrMagnitude > 1e-4f) heading = f.normalized;
        }

        private static Vector3 shoulderShift;
        private static float shoulderBlend;

        [HarmonyPrefix, HarmonyPatch(typeof(CameraRig), "LateUpdate")]
        private static void UndoShoulder(CameraRig __instance)
        {
            if (__instance.mainCamera) __instance.mainCamera.transform.position -= shoulderShift;
            shoulderShift = Vector3.zero;
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(CameraRig), "LateUpdate")]
        private static void AimZoom(CameraRig __instance)
        {
            var g = GunsPlugin.Instance;
            var cam = __instance.mainCamera;
            if (!cam) return;
            bool on = g && g.GunOut && !CheeseApi.FirstPerson;
            shoulderBlend = Mathf.MoveTowards(shoulderBlend, on ? 1f + (g ? g.aimBlend : 0f) : 0f, Time.deltaTime * 3f);
            var me = ModCommon.LocalRagdoll();
            if (shoulderBlend > 0f && me)
            {
                float sc = ModCommon.BodyScale(me);
                shoulderShift = (cam.transform.right * 0.3f + cam.transform.up * 0.08f) * sc * shoulderBlend;
                cam.transform.position += shoulderShift;
            }
            if (g && g.GunOut) cam.fieldOfView *= Mathf.Lerp(1f, g.AimZoom.Value, g.aimBlend);
        }
    }
}
