using System.Collections.Generic;
using UnityEngine;

namespace Modz
{
    public partial class CheeseCar
    {
        public float Damage { get; private set; }
        public bool Wrecked => Damage >= 1f;

        private Vector3 prevVel;
        private float lastCrash, scrapeAmt, slipAmt;
        private bool glassBroken;
        private readonly Dictionary<MeshFilter, (Mesh original, Vector3[] verts)> dents = new Dictionary<MeshFilter, (Mesh, Vector3[])>();
        private ParticleSystem smoke, fire;
        private static Material fxMat;

        private static Material FxMat => fxMat ? fxMat : (fxMat = ModCommon.UnlitMaterial(ModCommon.BlobTexture(32, 0f, 21)));

        private void HandleCrash(Collision collision)
        {
            var cfg = CarPlugin.Instance;
            if (!cfg.CrashPhysics.Value || collision.contactCount == 0) return;
            var contact = collision.GetContact(0);
            float impact = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
            bool terrain = collision.gameObject.layer == LayerMask.NameToLayer("Ground");
            if (impact < (terrain ? 110f : 45f) || Time.time - lastCrash < 0.12f) return;
            lastCrash = Time.time;
            float str = Mathf.Clamp01((impact - 45f) / 220f);
            bool breakGlass = !glassBroken && impact > 120f;
            if (breakGlass) glassBroken = true;

            CarPlugin.Log.LogInfo($"Crash: impact {impact:F0} into {collision.collider.name}, damage {Damage * 100f:F0}%{(breakGlass ? ", glass" : "")}");
            if (sounds) sounds.Crash(str, breakGlass);
            Sparks(contact.point, contact.normal, str);
            Debris(contact.point, collision.relativeVelocity, str, breakGlass);
            Vector3 into = collision.relativeVelocity.sqrMagnitude > 1f ? collision.relativeVelocity.normalized : -contact.normal;
            Dent(contact.point, into, s * (0.6f + 1.3f * str), s * (0.06f + 0.4f * str) * cfg.DamageMul.Value);

            Damage = Mathf.Min(1.2f, Damage + impact / 750f * cfg.DamageMul.Value);
            UpdateDamageFx();
            if (driver && ModCommon.IsLocal(driver)) StageManager.Instance.cameraRig.SetScreenShake(0.4f + str * 1.6f, 2.5f);

            if (driver && cfg.Eject.Value && impact > cfg.EjectSpeed.Value)
            {
                var d = driver;
                Vector3 v = prevVel;
                Exit();
                foreach (var p in d.GetRagdollParts())
                    if (p && p.rigidBody && !p.rigidBody.isKinematic)
                        p.rigidBody.velocity = v * 0.9f + Vector3.up * v.magnitude * 0.25f + Random.insideUnitSphere * 8f;
                CarPlugin.Instance.Toast("EJECTED!");
            }
        }

        private void Sparks(Vector3 point, Vector3 normal, float str)
        {
            var go = new GameObject("CarSparks");
            go.transform.position = point;
            go.transform.rotation = Quaternion.LookRotation(normal);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false; main.duration = 0.2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(s * 2f, s * 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(s * 0.02f, s * 0.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.9f, 0.4f), new Color(1f, 0.5f, 0.1f));
            main.gravityModifier = 0.5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; em.rateOverTime = 0f;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 70f; sh.radius = s * 0.05f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = FxMat; r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.03f; r.lengthScale = 2f;
            ps.Play();
            ps.Emit((int)(20 + 70 * str));
            Destroy(go, 1f);
        }

        private static readonly Queue<GameObject> debris = new Queue<GameObject>();

        private void Debris(Vector3 point, Vector3 relVel, float str, bool glass)
        {
            int n = Mathf.RoundToInt(str * 5f) + (glass ? 14 : 0);
            Color paint = PaintColor(CarPlugin.Instance.Paint.Value);
            for (int i = 0; i < n; i++)
            {
                bool shard = glass && i >= n - 14;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "CarDebris";
                float sz = shard ? s * Random.Range(0.03f, 0.08f) : s * Random.Range(0.08f, 0.2f);
                go.transform.position = point + Random.insideUnitSphere * s * 0.3f;
                go.transform.rotation = Random.rotation;
                go.transform.localScale = shard ? new Vector3(sz, sz * 0.15f, sz * 0.8f) : new Vector3(sz, sz * 0.3f, sz * 1.4f);
                var col = shard ? new Color(0.7f, 0.85f, 0.95f, 0.6f) : (Random.value < 0.5f ? paint : new Color(0.05f, 0.05f, 0.05f));
                go.GetComponent<Renderer>().sharedMaterial = shard ? FxMatTinted(col) : ModCommon.Solid(col, 0.5f);
                var rbd = go.AddComponent<Rigidbody>();
                rbd.mass = 0.2f;
                rbd.velocity = rb.velocity * 0.6f - relVel * 0.15f + Random.onUnitSphere * s * 3f + Vector3.up * s * 2f;
                rbd.angularVelocity = Random.insideUnitSphere * 25f;
                foreach (var cc in carColliders) Physics.IgnoreCollision(go.GetComponent<Collider>(), cc);
                Destroy(go, shard ? 6f : 15f);
                debris.Enqueue(go);
                while (debris.Count > 80) { var o = debris.Dequeue(); if (o) Destroy(o); }
            }
        }

        private static readonly Dictionary<Color, Material> tinted = new Dictionary<Color, Material>();
        private static Material FxMatTinted(Color c)
        {
            if (!tinted.TryGetValue(c, out var m) || !m) tinted[c] = m = new Material(ModCommon.UnlitMaterial()) { color = c };
            return m;
        }

        private void Dent(Vector3 point, Vector3 dir, float radius, float depth)
        {
            var model = transform.Find("model");
            if (!model || depth <= 0f) return;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                if (!dents.TryGetValue(mf, out var d))
                {
                    if (!mf.sharedMesh.isReadable) continue;
                    var orig = mf.sharedMesh;
                    mf.sharedMesh = Instantiate(orig);
                    d = (orig, mf.sharedMesh.vertices);
                    dents[mf] = d;
                }
                float sc = mf.transform.lossyScale.x;
                Vector3 lp = mf.transform.InverseTransformPoint(point);
                Vector3 ld = mf.transform.InverseTransformDirection(dir).normalized;
                float r = radius / sc, r2 = r * r, dd = depth / sc;
                var v = d.verts;
                bool any = false;
                for (int i = 0; i < v.Length; i++)
                {
                    float dist2 = (v[i] - lp).sqrMagnitude;
                    if (dist2 >= r2) continue;
                    float f = 1f - Mathf.Sqrt(dist2) / r;
                    v[i] += ld * (dd * f * f) + Random.insideUnitSphere * (dd * 0.08f * f);
                    any = true;
                }
                if (any) { mf.sharedMesh.vertices = v; mf.sharedMesh.RecalculateBounds(); }
            }
        }

        private void UpdateDamageFx()
        {
            if (Damage >= 0.35f && !smoke) smoke = MakeSmoke(false);
            if (Wrecked && !fire)
            {
                fire = MakeSmoke(true);
                CarPlugin.Instance.Toast("Engine destroyed — Repair car in the F1 menu");
            }
            if (smoke)
            {
                var em = smoke.emission; em.rateOverTime = Mathf.Lerp(6f, 40f, Mathf.InverseLerp(0.35f, 1f, Damage));
                var main = smoke.main;
                main.startColor = Color.Lerp(new Color(0.75f, 0.75f, 0.75f, 0.5f), new Color(0.08f, 0.08f, 0.08f, 0.8f), Mathf.InverseLerp(0.35f, 1f, Damage));
            }
        }

        private ParticleSystem MakeSmoke(bool isFire)
        {
            var go = new GameObject(isFire ? "CarFire" : "CarSmoke");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, H * 1.2f, L * 0.36f);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = isFire ? new ParticleSystem.MinMaxCurve(0.3f, 0.7f) : new ParticleSystem.MinMaxCurve(1.5f, 3f);
            main.startSpeed = isFire ? new ParticleSystem.MinMaxCurve(s * 0.8f, s * 2f) : new ParticleSystem.MinMaxCurve(s * 0.5f, s * 1.2f);
            main.startSize = isFire ? new ParticleSystem.MinMaxCurve(s * 0.3f, s * 0.7f) : new ParticleSystem.MinMaxCurve(s * 0.4f, s * 0.9f);
            main.startColor = isFire ? new ParticleSystem.MinMaxGradient(new Color(1f, 0.6f, 0.1f, 0.9f), new Color(1f, 0.25f, 0.05f, 0.8f)) : new ParticleSystem.MinMaxGradient(new Color(0.7f, 0.7f, 0.7f, 0.5f));
            main.gravityModifier = isFire ? -0.05f : -0.02f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 300;
            var em = ps.emission; em.rateOverTime = isFire ? 45f : 8f;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 15f; sh.radius = s * 0.25f;
            var sol = ps.sizeOverLifetime; sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, isFire ? 1f : 0.6f, 1f, isFire ? 0.2f : 2.5f));
            var col = ps.colorOverLifetime; col.enabled = true;
            var gr = new Gradient();
            gr.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                       new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = gr;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = FxMat;
            ps.Play();
            return ps;
        }

        public void Repair(bool silent = false)
        {
            Damage = 0f;
            glassBroken = false;
            foreach (var kv in dents)
            {
                if (!kv.Key) continue;
                var copy = kv.Key.sharedMesh;
                kv.Key.sharedMesh = kv.Value.original;
                if (copy && copy != kv.Value.original) Destroy(copy);
            }
            dents.Clear();
            if (smoke) Destroy(smoke.gameObject);
            if (fire) Destroy(fire.gameObject);
            if (!silent) CarPlugin.Instance.Toast("Car repaired");
        }

        private void OnCollisionStay(Collision collision)
        {
            if (collision.rigidbody || collision.collider.GetComponent<RagdollPart>()) return;
            float tangential = Vector3.ProjectOnPlane(collision.relativeVelocity, collision.contactCount > 0 ? collision.GetContact(0).normal : Vector3.up).magnitude;
            if (tangential > 12f) scrapeAmt = Mathf.Max(scrapeAmt, Mathf.Clamp01(tangential / 120f));
            if (tangential > 40f && Random.value < 0.15f && collision.contactCount > 0)
                Sparks(collision.GetContact(0).point, collision.GetContact(0).normal, 0.05f);
        }
    }
}
