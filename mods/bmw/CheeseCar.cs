using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modz
{
    public partial class CheeseCar : MonoBehaviour
    {
        public ActiveRagdoll driver;

        public static bool ScriptActive, ScriptBrake, ScriptNitro;
        public static float ScriptThrottle, ScriptSteer;
        public float Speed => rb ? rb.velocity.magnitude : 0f;

        private Rigidbody rb;
        private float s, L, W, H, R, rest, k, c;
        private Vector3[] wheelLocal;
        private Transform[] wheelVis;
        private float[] wheelSpin;
        private float[] lastCompression;
        private Transform seat;
        private Transform camProxy;
        private Light[] headlights;
        private Transform wheelPoint;
        private KinematicSeat kseat;
        private float upsideDownTime;
        private Vector3 camVel;
        private bool importedModel;
        private CarSounds sounds;
        private AudioSource horn;
        private int lastGear;
        private float throttleSmooth;
        private FixedJoint seatJoint;
        private readonly List<Collider> carColliders = new List<Collider>();
        private readonly RaycastHit[] hits = new RaycastHit[8];
        private readonly float[] loads = new float[4];
        private float steerSmooth;

        public static CheeseCar Spawn(ActiveRagdoll near, float sizeMul)
        {
            float s = ModCommon.BodyScale(near) * sizeMul;
            var go = new GameObject(CheeseApi.CarName);
            go.layer = LayerMask.NameToLayer("Obstacle") >= 0 ? LayerMask.NameToLayer("Obstacle") : 0;
            var car = go.AddComponent<CheeseCar>();
            car.Build(s, near);
            car.Teleport(near);
            CarPlugin.Instance.Toast("BMW spawned — press E to drive, H to honk");
            return car;
        }

        private void Build(float scale, ActiveRagdoll refRagdoll)
        {
            s = scale;
            L = 4.3f * s; W = 1.75f * s; H = 0.55f * s; R = 0.34f * s; rest = 0.45f * s;

            var model = CarModels.Get();
            float modelH = 0f;
            if (model != null)
            {
                W = model.BodyBounds.size.x * L;
                modelH = model.BodyBounds.size.y * L;
                H = modelH * 0.5f;
                if (model.WheelRadius > 0f) R = model.WheelRadius * L;
                rest = Mathf.Max(0.3f * s, R * 1.2f);
            }

            float ragdollMass = 0f;
            foreach (var p in refRagdoll.GetRagdollParts()) if (p && p.rigidBody) ragdollMass += p.rigidBody.mass;
            float M = Mathf.Max(1f, ragdollMass * 6f);
            float g = Mathf.Abs(Physics.gravity.y);

            rb = gameObject.AddComponent<Rigidbody>();
            rb.mass = M;
            rb.drag = 0.02f;
            rb.angularDrag = 1.5f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.maxAngularVelocity = 12f;

            k = M * g / 4f / (0.45f * rest);
            c = 2f * 0.55f * Mathf.Sqrt(k * M / 4f);

            var phys = new PhysicMaterial("CarBody") { dynamicFriction = 0.15f, staticFriction = 0.15f, frictionCombine = PhysicMaterialCombine.Minimum, bounciness = 0.1f };
            var box = gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, H * 0.5f, 0f);
            box.size = new Vector3(W, H, L);
            box.sharedMaterial = phys;
            carColliders.Add(box);
            var bumpers = gameObject.AddComponent<BoxCollider>();
            bumpers.center = new Vector3(0f, H * 0.3f, 0f);
            bumpers.size = new Vector3(W * 0.98f, H * 0.4f, L + 0.3f * s);
            bumpers.sharedMaterial = phys;
            carColliders.Add(bumpers);

            rb.centerOfMass = new Vector3(0f, -0.25f * s, 0f);

            camProxy = new GameObject("CarCamera").transform;
            camProxy.SetParent(transform, false);
            seat = new GameObject("seat").transform;
            seat.SetParent(transform, false);
            wheelPoint = new GameObject("steeringWheel").transform;
            wheelPoint.SetParent(transform, false);
            kseat = gameObject.AddComponent<KinematicSeat>();
            wheelVis = new Transform[4];
            wheelSpin = new float[4];
            lastCompression = new float[4];

            if (model != null)
            {
                box.center = new Vector3(0f, R * 0.9f + (H - R * 0.9f) * 0.5f, 0f);
                box.size = new Vector3(W * 0.96f, H - R * 0.9f, L * 0.97f);
                bumpers.center = new Vector3(0f, H + modelH * 0.22f, -L * 0.05f);
                bumpers.size = new Vector3(W * 0.85f, modelH * 0.42f, L * 0.45f);
                BuildFromModel(model);
            }
            else BuildProcedural();
            BuildAudio();
        }

        private void BuildFromModel(GlbLoader.CarModel model)
        {
            var body = Instantiate(model.Body, transform, false);
            body.name = "model";
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.transform.localScale = Vector3.one * L;
            body.SetActive(true);
            importedModel = true;
            headlights = new Light[2];
            for (int i = 0; i < 2; i++)
            {
                var l = new GameObject("headlight").AddComponent<Light>();
                l.transform.SetParent(transform, false);
                l.transform.localPosition = new Vector3((i == 0 ? 1 : -1) * W * 0.33f, H * 0.62f, L * 0.47f);
                l.transform.localRotation = Quaternion.Euler(6f, 0f, 0f);
                l.type = LightType.Spot;
                l.spotAngle = 70f;
                l.range = L * 6f;
                l.intensity = 2.2f;
                l.color = new Color(1f, 0.95f, 0.85f);
                l.shadows = LightShadows.None;
                l.enabled = false;
                headlights[i] = l;
            }
            seat.localPosition = new Vector3(-W * 0.2f, H * 0.42f, -L * 0.07f);
            wheelPoint.localPosition = seat.localPosition + new Vector3(0f, s * 0.45f, s * 0.75f);
            string paintName = CarPlugin.Instance.Paint.Value;
            if (paintName != "Original")
            {
                Color paint = PaintColor(paintName);
                foreach (var r in body.GetComponentsInChildren<Renderer>())
                {
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        string n = mats[i] ? mats[i].name.ToLowerInvariant() : "";
                        if (n.Contains("paint") || n.Contains("coloured") || n.Contains("colored") || n.Contains("body"))
                            mats[i] = new Material(mats[i]) { color = paint };
                    }
                    r.sharedMaterials = mats;
                }
            }

            wheelLocal = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = model.Wheels != null ? model.WheelCenters[i] * L
                    : new Vector3((i % 2 == 0 ? 1 : -1) * W * 0.42f, R, (i < 2 ? 1 : -1) * L * 0.32f);
                wheelLocal[i] = new Vector3(c.x, c.y + rest * 0.55f, c.z);
                var pivot = new GameObject("wheel" + i).transform;
                pivot.SetParent(transform, false);
                pivot.localPosition = c;
                if (model.Wheels != null)
                {
                    var w = Instantiate(model.Wheels[i], pivot, false);
                    w.transform.localPosition = Vector3.zero;
                    w.transform.localScale = Vector3.one * L;
                    w.SetActive(true);
                }
                wheelVis[i] = pivot;
            }
        }

        private void BuildProcedural()
        {
            Color paint = PaintColor(CarPlugin.Instance.Paint.Value);
            Color black = new Color(0.03f, 0.03f, 0.035f);
            Color trim = new Color(0.08f, 0.08f, 0.09f);
            Color chrome = new Color(0.8f, 0.82f, 0.85f);
            Color glass = new Color(0.08f, 0.12f, 0.16f);
            Color leather = new Color(0.62f, 0.45f, 0.3f);
            float zf = L * 0.5f, zr = -L * 0.5f;
            var paintMat = ModCommon.Solid(paint, 0.88f, 0.55f);

            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.5f, 0f), Quaternion.identity, new Vector3(W, H, L), paint, 0.88f, null, paintMat);
            Part(PrimitiveType.Cube, new Vector3(0f, H + 0.025f * s, L * 0.30f), Quaternion.Euler(1.5f, 0f, 0f), new Vector3(W * 0.97f, 0.05f * s, L * 0.36f), paint, 0.88f, null, paintMat); // hood
            Part(PrimitiveType.Cube, new Vector3(0f, H + 0.04f * s, -L * 0.37f), Quaternion.identity, new Vector3(W * 0.97f, 0.08f * s, L * 0.24f), paint, 0.88f, null, paintMat);      // boot lid
            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.42f, 0f), Quaternion.identity, new Vector3(W * 1.012f, 0.08f * s, L * 0.86f), trim, 0.3f);                                    // rub strip
            Part(PrimitiveType.Cube, new Vector3(0f, H - 0.015f * s, L * 0.0f), Quaternion.identity, new Vector3(W * 1.006f, 0.018f * s, L * 0.9f), chrome, 0.95f, null, null, 0.9f);     // chrome beltline
            Part(PrimitiveType.Cube, new Vector3(0f, 0.04f * s, 0f), Quaternion.identity, new Vector3(W * 0.96f, 0.08f * s, L * 0.98f), black, 0.1f);                                   // sills
            foreach (float side in new[] { -1f, 1f })
            {
                float x = side * (W * 0.5f + 0.004f * s);
                Part(PrimitiveType.Cube, new Vector3(x, H * 0.55f, L * 0.13f), Quaternion.identity, new Vector3(0.01f * s, H * 0.85f, 0.012f * s), black, 0.1f);
                Part(PrimitiveType.Cube, new Vector3(x, H * 0.55f, -L * 0.15f), Quaternion.identity, new Vector3(0.01f * s, H * 0.85f, 0.012f * s), black, 0.1f);
                Part(PrimitiveType.Cube, new Vector3(x + side * 0.01f * s, H * 0.78f, -L * 0.1f), Quaternion.identity, new Vector3(0.02f * s, 0.035f * s, 0.14f * s), chrome, 0.95f, null, null, 0.9f);
                Part(PrimitiveType.Cube, new Vector3(side * (W * 0.5f + 0.09f * s), H + 0.14f * s, L * 0.11f), Quaternion.Euler(0f, side * 12f, 0f), new Vector3(0.16f * s, 0.1f * s, 0.06f * s), paint, 0.88f, null, paintMat);
                foreach (float wz in new[] { L * 0.33f, -L * 0.31f })
                    Part(PrimitiveType.Cylinder, new Vector3(side * (W * 0.5f + 0.003f * s), 0.02f * s, wz), Quaternion.Euler(0f, 0f, 90f), new Vector3(2.3f * R, 0.004f * s, 2.3f * R), black, 0.05f);
            }

            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.66f, zf + 0.012f * s), Quaternion.identity, new Vector3(W * 0.97f, 0.3f * s, 0.03f * s), black, 0.3f); // headlight panel
            foreach (float side in new[] { -1f, 1f })
            {
                Part(PrimitiveType.Cube, new Vector3(side * 0.1f * s, H * 0.66f, zf + 0.03f * s), Quaternion.identity, new Vector3(0.15f * s, 0.25f * s, 0.03f * s), chrome, 0.95f, null, null, 0.9f);
                Part(PrimitiveType.Cube, new Vector3(side * 0.1f * s, H * 0.66f, zf + 0.04f * s), Quaternion.identity, new Vector3(0.12f * s, 0.21f * s, 0.02f * s), black, 0.2f);
                for (int i = 0; i < 3; i++)
                    Part(PrimitiveType.Cube, new Vector3(side * (0.06f + 0.04f * i) * s, H * 0.66f, zf + 0.052f * s), Quaternion.identity, new Vector3(0.008f * s, 0.2f * s, 0.008f * s), chrome, 0.95f, null, null, 0.9f);
                foreach (float hx in new[] { 0.34f, 0.6f })
                {
                    Part(PrimitiveType.Cylinder, new Vector3(side * hx * s, H * 0.66f, zf + 0.03f * s), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.2f * s, 0.012f * s, 0.2f * s), chrome, 0.95f, null, null, 0.9f);
                    Part(PrimitiveType.Cylinder, new Vector3(side * hx * s, H * 0.66f, zf + 0.04f * s), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.16f * s, 0.01f * s, 0.16f * s), new Color(1f, 0.98f, 0.9f), 0.98f, null, Emissive(new Color(1f, 0.97f, 0.85f)));
                }
                Part(PrimitiveType.Cube, new Vector3(side * 0.62f * s, H * 0.28f, zf + 0.15f * s), Quaternion.identity, new Vector3(0.18f * s, 0.06f * s, 0.02f * s), new Color(1f, 0.55f, 0.05f), 0.8f, null, Emissive(new Color(1f, 0.5f, 0.05f), 0.6f));
            }
            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.3f, zf + 0.07f * s), Quaternion.identity, new Vector3(W * 1.0f, 0.2f * s, 0.14f * s), trim, 0.35f);             // front bumper
            Part(PrimitiveType.Cube, new Vector3(0f, 0.06f * s, zf + 0.05f * s), Quaternion.identity, new Vector3(W * 0.9f, 0.1f * s, 0.12f * s), black, 0.2f);             // splitter
            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.3f, zf + 0.145f * s), Quaternion.identity, new Vector3(0.52f * s, 0.12f * s, 0.01f * s), Color.white, 0.5f);     // plate
            Badge(new Vector3(0f, H + 0.056f * s, zf - 0.08f * s), Quaternion.Euler(90f, 0f, 0f), 0.12f * s);                                                            // hood roundel

            foreach (float side in new[] { -1f, 1f })
            {
                Part(PrimitiveType.Cube, new Vector3(side * 0.52f * s, H * 0.7f, zr - 0.01f * s), Quaternion.identity, new Vector3(0.5f * s, 0.2f * s, 0.03f * s), new Color(0.75f, 0.02f, 0.02f), 0.9f, null, Emissive(new Color(0.9f, 0.02f, 0.02f), 0.8f));
                Part(PrimitiveType.Cube, new Vector3(side * 0.62f * s, H * 0.64f, zr - 0.022f * s), Quaternion.identity, new Vector3(0.16f * s, 0.05f * s, 0.01f * s), new Color(1f, 0.55f, 0.05f), 0.9f);
                Part(PrimitiveType.Cylinder, new Vector3(side * 0.55f * s, 0.12f * s, zr - 0.06f * s), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.08f * s, 0.06f * s, 0.08f * s), chrome, 0.95f, null, null, 0.9f); // exhausts
            }
            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.7f, zr - 0.012f * s), Quaternion.identity, new Vector3(0.56f * s, 0.2f * s, 0.02f * s), black, 0.4f);
            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.3f, zr - 0.07f * s), Quaternion.identity, new Vector3(W * 1.0f, 0.2f * s, 0.14f * s), trim, 0.35f);              // rear bumper
            Part(PrimitiveType.Cube, new Vector3(0f, H * 0.3f, zr - 0.145f * s), Quaternion.identity, new Vector3(0.52f * s, 0.12f * s, 0.01f * s), Color.white, 0.5f);     // plate
            Part(PrimitiveType.Cube, new Vector3(0f, H + 0.13f * s, zr + 0.07f * s), Quaternion.Euler(-8f, 0f, 0f), new Vector3(W * 0.9f, 0.025f * s, 0.18f * s), black, 0.35f); // spoiler
            foreach (float side in new[] { -1f, 1f })
                Part(PrimitiveType.Cube, new Vector3(side * W * 0.4f, H + 0.1f * s, zr + 0.08f * s), Quaternion.identity, new Vector3(0.03f * s, 0.06f * s, 0.1f * s), black, 0.35f);
            Badge(new Vector3(0f, H + 0.085f * s, zr + 0.25f * s), Quaternion.Euler(90f, 0f, 0f), 0.1f * s);

            Vector3 wsBase = new Vector3(0f, H + 0.05f * s, L * 0.1f);
            Quaternion wsRot = Quaternion.Euler(-32f, 0f, 0f);
            Part(PrimitiveType.Cube, wsBase + wsRot * new Vector3(0f, 0.27f * s, 0f), wsRot, new Vector3(W * 0.9f, 0.54f * s, 0.015f * s), glass, 0.97f);
            foreach (float side in new[] { -1f, 1f })
                Part(PrimitiveType.Cube, wsBase + wsRot * new Vector3(side * W * 0.455f, 0.27f * s, 0f), wsRot, new Vector3(0.04f * s, 0.56f * s, 0.04f * s), black, 0.3f);
            Part(PrimitiveType.Cube, wsBase + wsRot * new Vector3(0f, 0.55f * s, 0f), wsRot, new Vector3(W * 0.92f, 0.04f * s, 0.04f * s), black, 0.3f);
            Part(PrimitiveType.Cube, new Vector3(0f, H + 0.1f * s, L * 0.04f), Quaternion.identity, new Vector3(W * 0.9f, 0.2f * s, 0.22f * s), black, 0.25f);               // dashboard
            Part(PrimitiveType.Cube, new Vector3(0f, H + 0.005f * s, -L * 0.1f), Quaternion.identity, new Vector3(W * 0.88f, 0.01f * s, L * 0.32f), black, 0.05f);            // carpet
            Part(PrimitiveType.Cube, new Vector3(0f, H + 0.1f * s, -L * 0.27f), Quaternion.identity, new Vector3(W * 0.86f, 0.2f * s, 0.32f * s), black, 0.15f);             // folded hood
            foreach (float side in new[] { -1f, 1f })
            {
                Part(PrimitiveType.Cube, new Vector3(side * 0.38f * s, H + 0.08f * s, -L * 0.06f), Quaternion.identity, new Vector3(0.5f * s, 0.14f * s, 0.5f * s), leather, 0.35f);
                Part(PrimitiveType.Cube, new Vector3(side * 0.38f * s, H + 0.38f * s, -L * 0.06f - 0.28f * s), Quaternion.Euler(-10f, 0f, 0f), new Vector3(0.48f * s, 0.6f * s, 0.12f * s), leather, 0.35f);
                Part(PrimitiveType.Cube, new Vector3(side * 0.38f * s, H + 0.75f * s, -L * 0.06f - 0.33f * s), Quaternion.Euler(-10f, 0f, 0f), new Vector3(0.3f * s, 0.14f * s, 0.1f * s), leather, 0.35f);
            }
            Vector3 swPos = new Vector3(-0.38f * s, H + 0.36f * s, L * 0.0f);
            Part(PrimitiveType.Cylinder, swPos, Quaternion.Euler(-60f, 0f, 0f), new Vector3(0.42f * s, 0.02f * s, 0.42f * s), black, 0.4f);
            Part(PrimitiveType.Cylinder, swPos + new Vector3(0f, -0.1f * s, 0.1f * s), Quaternion.Euler(30f, 0f, 0f), new Vector3(0.05f * s, 0.12f * s, 0.05f * s), black, 0.4f);

            seat.localPosition = new Vector3(-0.38f * s, H + 0.25f * s, -L * 0.08f);
            wheelPoint.localPosition = new Vector3(-0.38f * s, H + 0.36f * s, 0f);

            wheelLocal = new[]
            {
                new Vector3(W * 0.44f, H * 0.2f, L * 0.33f), new Vector3(-W * 0.44f, H * 0.2f, L * 0.33f),
                new Vector3(W * 0.44f, H * 0.2f, -L * 0.31f), new Vector3(-W * 0.44f, H * 0.2f, -L * 0.31f),
            };
            Color silver = new Color(0.72f, 0.73f, 0.75f);
            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject("wheel" + i).transform;
                pivot.SetParent(transform, false);
                pivot.localPosition = wheelLocal[i];
                float side = Mathf.Sign(wheelLocal[i].x);
                float ww = 0.4f * s;
                Vector3 outer = new Vector3(side * ww * 0.5f, 0f, 0f);
                Part(PrimitiveType.Cylinder, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), new Vector3(2f * R, ww * 0.5f, 2f * R), new Color(0.04f, 0.04f, 0.04f), 0.15f, pivot);
                Part(PrimitiveType.Cylinder, outer * 0.98f, Quaternion.Euler(0f, 0f, 90f), new Vector3(1.45f * R, 0.02f * s, 1.45f * R), new Color(0.15f, 0.15f, 0.16f), 0.6f, pivot);
                Part(PrimitiveType.Cylinder, outer * 1.01f, Quaternion.Euler(0f, 0f, 90f), new Vector3(1.5f * R, 0.008f * s, 1.5f * R), silver, 0.9f, pivot, null, 0.8f).transform.localScale = new Vector3(1.5f * R, 0.008f * s, 1.5f * R);
                for (int sp = 0; sp < 10; sp++)
                {
                    var spoke = Part(PrimitiveType.Cube, outer * 1.04f, Quaternion.Euler(sp * 36f, 0f, 0f), new Vector3(0.015f * s, 1.3f * R, 0.04f * s), silver, 0.9f, pivot, null, 0.8f);
                    spoke.transform.localPosition = outer * 1.04f + spoke.transform.localRotation * new Vector3(0f, 0.36f * R, 0f);
                    spoke.transform.localScale = new Vector3(0.015f * s, 0.7f * R, 0.04f * s);
                }
                Part(PrimitiveType.Cylinder, outer * 1.06f, Quaternion.Euler(0f, 0f, 90f), new Vector3(0.4f * R, 0.015f * s, 0.4f * R), silver, 0.95f, pivot, null, 0.9f);
                Badge(outer * 1.1f, Quaternion.Euler(0f, side * 90f, 0f) * Quaternion.Euler(0f, 180f, 0f), 0.3f * R, pivot);
                wheelVis[i] = pivot;
            }

        }

        private void BuildAudio()
        {
            sounds = gameObject.AddComponent<CarSounds>();
            horn = gameObject.AddComponent<AudioSource>();
            horn.clip = CarSounds.Get("horn", CarAudio.Horn);
            horn.loop = true;
            horn.spatialBlend = 0.7f;
            horn.minDistance = 25f;
            horn.maxDistance = 600f;
            horn.rolloffMode = AudioRolloffMode.Linear;
        }

        public static Color PaintColor(string name)
        {
            switch (name)
            {
                case "Alpine White": return new Color(0.93f, 0.93f, 0.91f);
                case "Hellrot": return new Color(0.75f, 0.04f, 0.04f);
                case "Black Sapphire": return new Color(0.04f, 0.045f, 0.06f);
                case "Brilliant Red": return new Color(0.85f, 0.08f, 0.05f);
                default: return new Color(0.1f, 0.22f, 0.6f); // Estoril Blue
            }
        }

        private static Material Emissive(Color c, float strength = 1f)
        {
            var m = new Material(ModCommon.Solid(c, 0.95f)) { color = c };
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * strength);
            }
            return m;
        }

        private static Material badgeMat;

        private void Badge(Vector3 localPos, Quaternion localRot, float size, Transform parent = null)
        {
            if (!badgeMat)
            {
                const int n = 128;
                var t = new Texture2D(n, n, TextureFormat.RGBA32, true);
                var blue = new Color(0.0f, 0.45f, 0.8f);
                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color col;
                    if (d > 1f) col = new Color(0, 0, 0, 0);
                    else if (d > 0.94f) col = new Color(0.75f, 0.76f, 0.78f);
                    else if (d > 0.62f) col = new Color(0.02f, 0.02f, 0.02f);
                    else if (d > 0.58f) col = new Color(0.75f, 0.76f, 0.78f);
                    else col = (dx >= 0) == (dy >= 0) ? Color.white : blue;
                    t.SetPixel(x, y, col);
                }
                t.Apply(true);
                badgeMat = ModCommon.UnlitMaterial(t);
            }
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(parent ? parent : transform, false);
            q.transform.localPosition = localPos;
            q.transform.localRotation = localRot;
            q.transform.localScale = Vector3.one * size;
            q.GetComponent<Renderer>().sharedMaterial = badgeMat;
        }

        private GameObject Part(PrimitiveType type, Vector3 pos, Quaternion rot, Vector3 scale, Color col, float gloss = 0.35f, Transform parent = null, Material mat = null, float metal = 0f)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent ? parent : transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat ? mat : ModCommon.Solid(col, gloss, metal);
            return go;
        }

        public void Teleport(ActiveRagdoll near)
        {
            Vector3 root = near.GetRootPosition();
            Vector3 facing = Facing(near);
            Vector3 pos = root + Vector3.Cross(Vector3.up, facing) * (W * 1.1f) + Vector3.up * s;
            Vector3 up = Vector3.up;
            if (Physics.Raycast(pos + Vector3.up * 5f * s, Vector3.down, out var hit, 30f * s, ModCommon.GroundMask, QueryTriggerInteraction.Ignore))
            {
                up = hit.normal;
                pos = hit.point + up * (rest * 0.6f + R * 0.5f);
            }
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.ProjectOnPlane(facing, up), up));
            if (rb)
            {
                rb.position = transform.position;
                rb.rotation = transform.rotation;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        public void ResetCar(ActiveRagdoll near)
        {
            var d = driver;
            if (d) Exit();
            if (near && near != d) Teleport(near);
            else
            {
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                rb.position += Vector3.up * s * 1.2f;
                rb.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            Repair(silent: true);
            if (d) Enter(d);
            CarPlugin.Instance.Toast("Car reset");
        }

        public void Flip()
        {
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            rb.position += Vector3.up * s * 1.5f;
            rb.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            rb.angularVelocity = Vector3.zero;
            rb.velocity *= 0.3f;
        }

        private static Vector3 Facing(ActiveRagdoll r)
        {
            Vector3 hips = r.upperLegRight.transform.position - r.upperLegLeft.transform.position;
            Vector3 f = Vector3.Cross(hips, Vector3.up);
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            return f.normalized;
        }

        public void Enter(ActiveRagdoll r)
        {
            if (driver) Exit();
            if (!r || !r.spine1 || !r.spine1.rigidBody) return;

            SetIgnore(r, true);
            driver = r;
            CheeseApi.Driver = r;
            CheeseApi.DriverVehicle = rb;
            kseat.Seat = seat;
            kseat.HandTarget = wheelPoint;
            kseat.Pose = SeatPose.Drive;
            kseat.Sit(r, rb.velocity);
            if (sounds) { sounds.PlayOnce("door", 0.8f); if (!Wrecked) sounds.PlayOnce("engine_start", 0.9f); }
            if (importedModel && !CarPlugin.Instance.ShowDriverInModel.Value) SetDriverVisible(r, false);
            if (ModCommon.IsLocal(r)) StageManager.Instance.cameraRig.SetTarget(camProxy);
            CarPlugin.Log.LogInfo("Driver seated");
        }

        public void Exit()
        {
            if (sounds && driver) { sounds.PlayOnce("engine_stop", 0.7f); sounds.PlayOnce("door", 0.8f); }
            var r = driver;
            driver = null;
            Vector3 up = Vector3.up * s * 6f + transform.right * s * 3f;
            if (kseat) kseat.Stand(rb.velocity + up);
            if (CheeseApi.Driver == r) { CheeseApi.Driver = null; CheeseApi.DriverVehicle = null; }
            if (!r) return;
            SetDriverVisible(r, true);
            if (ModCommon.IsLocal(r) && StageManager.Instance) StageManager.Instance.cameraRig.SetTarget(r.spine1.transform);
            ModCommon.Unground(r, true);
            StartCoroutine(RestoreCollision(r));
        }

        private readonly List<Renderer> hiddenRenderers = new List<Renderer>();

        private void SetDriverVisible(ActiveRagdoll r, bool visible)
        {
            if (!visible)
            {
                foreach (var rend in r.GetComponentsInChildren<Renderer>())
                    if (rend.enabled) { rend.enabled = false; hiddenRenderers.Add(rend); }
            }
            else
            {
                foreach (var rend in hiddenRenderers) if (rend) rend.enabled = true;
                hiddenRenderers.Clear();
            }
        }

        private IEnumerator RestoreCollision(ActiveRagdoll r)
        {
            yield return new WaitForSeconds(0.8f);
            if (r && driver != r) SetIgnore(r, false);
        }

        private void SetIgnore(ActiveRagdoll r, bool ignore)
        {
            foreach (var p in r.GetRagdollParts())
            {
                if (!p) continue;
                foreach (var pc in p.GetComponents<Collider>())
                foreach (var cc in carColliders)
                    if (pc && cc) Physics.IgnoreCollision(pc, cc, ignore);
            }
        }

        private void Update()
        {
            if (headlights != null) foreach (var l in headlights) l.enabled = driver && !Wrecked;
            if (!sounds || !rb) return;
            var cfg = CarPlugin.Instance;
            sounds.Volume = cfg.CarVolume.Value;
            float spd = Mathf.Abs(Vector3.Dot(rb.velocity, transform.forward));
            float x = Mathf.Clamp01(spd / (cfg.TopSpeed.Value * 1.25f)) * 6f;
            int gear = Mathf.Min(5, (int)x);
            float rpm = spd < 4f ? 0.12f + 0.3f * throttleSmooth : Mathf.Lerp(0.35f + gear * 0.04f, 1f, x - gear);
            bool shifted = gear > lastGear && throttleSmooth > 0.5f;
            lastGear = gear;
            sounds.Drive(driver && !Wrecked, rpm, throttleSmooth, slipAmt, scrapeAmt, shifted);
            scrapeAmt = Mathf.MoveTowards(scrapeAmt, 0f, Time.deltaTime * 4f);

            bool honk = driver && ModCommon.IsLocal(driver) && !CheeseApi.MenuOpen && !ModCommon.Paused
                        && Application.isFocused && Keyboard.current != null && Keyboard.current.hKey.isPressed;
            horn.volume = cfg.CarVolume.Value * ModCommon.GameSfxVolume * 2f;
            if (honk && !horn.isPlaying) horn.Play();
            else if (!honk && horn.isPlaying) horn.Stop();
        }

        private void LateUpdate()
        {
            if (camProxy)
            {
                Vector3 want = transform.position + Vector3.up * 2.6f * s;
                camProxy.position = (camProxy.position - want).sqrMagnitude > s * s * 25f ? want : Vector3.SmoothDamp(camProxy.position, want, ref camVel, 0.04f);
            }
        }

        private void OnDestroy()
        {
            if (driver && ModCommon.IsLocal(driver) && StageManager.Instance) StageManager.Instance.cameraRig.SetTarget(driver.spine1.transform);
            if (driver && CheeseApi.Driver == driver) { CheeseApi.Driver = null; CheeseApi.DriverVehicle = null; }
        }

        private void FixedUpdate()
        {
            if (!rb) return;
            prevVel = rb.velocity;
            if (driver == null && kseat && kseat.Rider) Exit();
            if (driver != null && !driver) { driver = null; CheeseApi.Driver = null; CheeseApi.DriverVehicle = null; }

            var cfg = CarPlugin.Instance;
            float throttle = 0f, steer = 0f;
            bool brake = false, nitro = false;
            if (driver && !ModCommon.Paused && !CheeseApi.MenuOpen && !ModCommon.CountingDown && Application.isFocused)
            {
                var kb = Keyboard.current;
                var pad = Gamepad.current;
                if (kb != null)
                {
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle += 1f;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) throttle -= 1f;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer += 1f;
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer -= 1f;
                    brake = kb.spaceKey.isPressed;
                    nitro = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                }
                if (pad != null)
                {
                    throttle += pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
                    steer += pad.leftStick.ReadValue().x;
                    brake |= pad.buttonSouth.isPressed;
                    nitro |= pad.buttonWest.isPressed;
                }
                if (ScriptActive)
                {
                    throttle = ScriptThrottle; steer = ScriptSteer; brake = ScriptBrake; nitro = ScriptNitro;
                }
                throttle = Mathf.Clamp(throttle, -1f, 1f);
                steer = Mathf.Clamp(steer, -1f, 1f);
            }
            if (Wrecked && throttle > 0f) throttle = 0f;
            steerSmooth = Mathf.MoveTowards(steerSmooth, steer, Time.fixedDeltaTime * 5f);
            throttleSmooth = Mathf.MoveTowards(throttleSmooth, Mathf.Abs(throttle), Time.fixedDeltaTime * 4f);

            float g = Mathf.Abs(Physics.gravity.y);
            float M = rb.mass;
            float fwdSpeed = Vector3.Dot(rb.velocity, transform.forward);
            float maxSteer = 34f * cfg.Steering.Value / (1f + Mathf.Abs(fwdSpeed) / 140f);
            float steerAngle = steerSmooth * maxSteer;
            int grounded = 0;
            Vector3 groundNormal = Vector3.zero;
            float maxSlip = 0f;
            for (int i = 0; i < 4; i++) loads[i] = 0f;
            float longDemand = Mathf.Clamp01(Mathf.Abs(throttle) * (nitro ? 0.7f : 0.4f) + (brake ? 0.6f : 0f));

            for (int i = 0; i < 4; i++)
            {
                Vector3 origin = transform.TransformPoint(wheelLocal[i]);
                Vector3 down = -transform.up;
                float maxLen = rest + R;
                bool hitGround = false;
                RaycastHit best = default;
                int n = Physics.RaycastNonAlloc(origin, down, hits, maxLen, ModCommon.GroundMask, QueryTriggerInteraction.Ignore);
                float bestD = float.MaxValue;
                for (int h = 0; h < n; h++)
                {
                    if (hits[h].rigidbody == rb) continue;
                    if (hits[h].collider.GetComponent<RagdollPart>()) continue;
                    if (hits[h].distance < bestD) { bestD = hits[h].distance; best = hits[h]; hitGround = true; }
                }

                float len = hitGround ? best.distance - R : rest;
                len = Mathf.Clamp(len, 0f, rest);
                wheelVis[i].localPosition = wheelLocal[i] + Vector3.down * len;
                bool front = i < 2;
                wheelVis[i].localRotation = Quaternion.Euler(0f, front ? steerAngle : 0f, 0f);

                if (!hitGround) { lastCompression[i] = 0f; continue; }
                grounded++;
                groundNormal += best.normal;

                float compression = rest - len;
                float compVel = (compression - lastCompression[i]) / Time.fixedDeltaTime;
                lastCompression[i] = compression;
                float load = Mathf.Max(0f, k * compression + c * compVel);
                rb.AddForceAtPosition(transform.up * load, origin);
                loads[i] = load;

                Vector3 wheelFwd = Quaternion.AngleAxis(front ? steerAngle : 0f, transform.up) * transform.forward;
                Vector3 wheelRight = Vector3.Cross(transform.up, wheelFwd).normalized;
                Vector3 contact = best.point;
                Vector3 pv = rb.GetPointVelocity(contact);
                float slip = Vector3.Dot(pv, wheelRight);
                maxSlip = Mathf.Max(maxSlip, Mathf.Abs(slip));
                float wantF = -slip * (M / 4f) / Time.fixedDeltaTime;
                float limit = 1.3f * cfg.Grip.Value * Mathf.Max(load, M * g * 0.15f) * Mathf.Sqrt(1f - longDemand * longDemand * 0.6f);
                if (brake && !front && driver) limit *= 0.35f; // handbrake drift
                rb.AddForceAtPosition(wheelRight * Mathf.Clamp(wantF, -limit, limit), contact);

                float wf = Vector3.Dot(pv, wheelFwd);
                wheelSpin[i] += wf / R * Mathf.Rad2Deg * Time.fixedDeltaTime;
            }
            for (int i = 0; i < 4; i++)
                wheelVis[i].localRotation *= Quaternion.Euler(wheelSpin[i], 0f, 0f);
            float longSlip = (brake || throttle < 0f) && Mathf.Abs(fwdSpeed) > 30f ? 0.5f : (throttle > 0.9f && Mathf.Abs(fwdSpeed) < 25f && grounded > 0 ? 0.4f : 0f);
            slipAmt = grounded > 0 ? Mathf.Clamp01(Mathf.Max((maxSlip - 12f) / 45f, longSlip)) : 0f;

            float antiRoll = k * 0.5f;
            for (int axle = 0; axle < 2; axle++)
            {
                int a = axle * 2, b = a + 1;
                float diff = lastCompression[a] - lastCompression[b];
                if (lastCompression[a] > 0f) rb.AddForceAtPosition(-transform.up * diff * antiRoll, transform.TransformPoint(wheelLocal[a]));
                if (lastCompression[b] > 0f) rb.AddForceAtPosition(transform.up * diff * antiRoll, transform.TransformPoint(wheelLocal[b]));
            }
            if (grounded > 0)
            {
                float top0 = Mathf.Max(30f, cfg.TopSpeed.Value);
                rb.AddForce(-transform.up * (0.5f * M * g * Mathf.Min(fwdSpeed * fwdSpeed / (top0 * top0), 1.5f)));
            }
            if (grounded > 0 && Mathf.Abs(steer) < 0.05f)
                rb.AddTorque(-transform.up * Vector3.Dot(rb.angularVelocity, transform.up) * M * 0.6f * s * s * 0.05f);
            upsideDownTime = transform.up.y < 0.25f && rb.velocity.magnitude < 15f ? upsideDownTime + Time.fixedDeltaTime : 0f;
            if (upsideDownTime > 2.5f) { upsideDownTime = 0f; Flip(); }

            if (grounded > 0)
            {
                groundNormal.Normalize();
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, groundNormal).normalized;
                float share = grounded / 4f;
                float speedFrac = Mathf.Clamp01(Mathf.Abs(fwdSpeed) / Mathf.Max(30f, cfg.TopSpeed.Value));
                float accel = 0.9f * g * cfg.EnginePower.Value * (nitro ? cfg.NitroPower.Value : 1f) * (Damage >= 0.8f ? 0.5f : 1f) * Mathf.Lerp(1.35f, 0.65f, speedFrac);
                float top = cfg.TopSpeed.Value * (nitro ? 1.5f : 1f);
                if (throttle > 0f && fwdSpeed < top) rb.AddForce(fwd * accel * throttle * M * share);
                else if (throttle < 0f)
                {
                    if (fwdSpeed > 5f) rb.AddForce(-fwd * 1.4f * g * M * share);              // braking
                    else if (fwdSpeed > -top * 0.35f) rb.AddForce(fwd * accel * 0.6f * throttle * M * share); // reverse
                }
                if (!driver) brake = true; // parking brake: an empty car stays where you left it
                if (brake)
                    rb.AddForce(-fwd * Mathf.Sign(fwdSpeed) * Mathf.Min((driver ? 1.1f : 3f) * g, Mathf.Abs(fwdSpeed) / Time.fixedDeltaTime) * M * share);
                else if (Mathf.Approximately(throttle, 0f))
                    rb.AddForce(-fwd * fwdSpeed * 0.15f * M * share); // rolling resistance
            }
            else if (driver)
            {
                rb.AddRelativeTorque(new Vector3(throttle * 2.5f, steer * 2f, -steer * 1.5f), ForceMode.Acceleration);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!ModCommon.InRound) return;
            var part = collision.collider.GetComponent<RagdollPart>();
            if (!part)
            {
                if (!collision.collider.name.StartsWith("Gore") && collision.collider.name != "CarDebris") HandleCrash(collision);
                return;
            }
            if (!CarPlugin.Instance.RamBoost.Value || !part.ragdoll || part.ragdoll == driver) return;
            float rel = collision.relativeVelocity.magnitude;
            if (rel < 30f) return;
            var victim = part.ragdoll;
            ModCommon.Unground(victim, true);
            Vector3 launch = rb.velocity * 0.7f + Vector3.up * Mathf.Min(rel, 200f) * 0.6f;
            foreach (var p in victim.GetRagdollParts())
                if (p && p.rigidBody && !p.rigidBody.isKinematic) p.rigidBody.velocity = launch + Random.insideUnitSphere * 10f;
            if (StageManager.Instance && victim == ModCommon.LocalRagdoll()) StageManager.Instance.cameraRig.SetScreenShake(1f, 2f);
        }
    }

    public static class CarAudio
    {
        private const int Rate = 44100;

        public static AudioClip Engine()
        {
            var d = new float[Rate];
            float[] amp = { 1f, 0.7f, 0.55f, 0.35f, 0.42f, 0.2f, 0.18f, 0.1f, 0.12f, 0.06f, 0.05f, 0.04f };
            for (int i = 0; i < Rate; i++)
            {
                float t = (float)i / Rate, v = 0f;
                for (int k = 0; k < amp.Length; k++) v += amp[k] * Mathf.Sin(2f * Mathf.PI * 60f * (k + 1) * t + k * 0.7f);
                v *= 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 30f * t);
                v += 0.25f * Mathf.Sin(2f * Mathf.PI * 30f * t);
                d[i] = (float)System.Math.Tanh(v * 0.6f) * 0.8f;
            }
            var c = AudioClip.Create("engine", Rate, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        public static AudioClip Horn()
        {
            var d = new float[Rate];
            float lp = 0f;
            for (int i = 0; i < Rate; i++)
            {
                float t = (float)i / Rate;
                float a = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 420f * t)), b = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 525f * t));
                lp += 0.25f * ((a + b) * 0.5f - lp);
                d[i] = lp * 0.7f;
            }
            var c = AudioClip.Create("horn", Rate, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }
    }
}
