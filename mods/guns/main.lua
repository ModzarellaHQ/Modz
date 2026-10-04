local S = "Guns"
local damage = setting.number{ section = S, name = "Damage", default = 1, min = 0.2, max = 5, desc = "Bullet damage multiplier." }
local infinite = setting.toggle{ section = S, name = "Infinite ammo", default = false, desc = "Never reload." }
local volume = setting.number{ section = S, name = "Volume", default = 0.8, min = 0, max = 1, desc = "Gunshot volume." }
local recoil = setting.number{ section = S, name = "Recoil", default = 1, min = 0, max = 3, desc = "How much the view kicks per shot.", advanced = true }
local zoom = setting.number{ section = S, name = "Aim zoom", default = 0.7, min = 0.3, max = 1, desc = "Field of view while aiming (1 = no zoom).", advanced = true }
local pistol_key = setting.key{ name = "Glock", default = "Alpha1", desc = "Draw the pistol." }
local rifle_key = setting.key{ name = "AK-47", default = "Alpha2", desc = "Draw the rifle." }
local holster_key = setting.key{ name = "Holster", default = "Alpha3", desc = "Put the gun away." }
local reload_key = setting.key{ name = "Reload", default = "R", desc = "Reload." }

local sounds = audio.folder("sounds")

-- z runs from the back of the gun (0) to the muzzle (1); y from the bottom (0) to the top (1)
local guns = {
  { name = "Glock 17", file = "glock.glb", flip = true, length = 2.3, mag = 17, rpm = 450, auto = false, power = 150,
    spread = 0.35, bloom = 0.9, kick = 3.2, reload = 1.5, shots = { sounds.pistol_shot1, sounds.pistol_shot2 }, reload_sound = sounds.pistol_reload,
    grip = { 0.25, 0.3 }, support = { 0.28, 0.25 }, hip = { 0.1, -0.1, 0.38 }, ads = 0.5, sight = { 0.12, 1.0 }, muzzle = { 1, 0.8 }, eject = { 0.55, 0.85 }, key = pistol_key },
  { name = "AK-47", file = "ak47.glb", flip = false, length = 7.2, mag = 30, rpm = 600, auto = true, power = 200,
    spread = 0.25, bloom = 0.45, kick = 1.25, reload = 2.4, shots = { sounds.rifle_shot1, sounds.rifle_shot2, sounds.rifle_shot3 }, reload_sound = sounds.rifle_reload,
    grip = { 0.36, 0.25 }, support = { 0.6, 0.45 }, hip = { 0.13, -0.19, 0.5 }, ads = 0.4, stock = { 0.02, 0.62 }, sight = { 0.42, 0.95 }, muzzle = { 1, 0.72 }, eject = { 0.47, 0.75 }, key = rifle_key },
}

local flash_mat = mat.unlit(mat.blob(32, 0.5, 61), rgb(1, 0.85, 0.5))
local tracer_mat = mat.unlit(nil, rgb(1, 0.92, 0.65, 0.9))
local hole_mat = mat.unlit(mat.blob(32, 0.8, 62), rgb(0.06, 0.05, 0.05, 0.92))
local dust_mat = mat.unlit(mat.blob(32, 0.4, 63))
local brass_mat = mat.solid(rgb(0.8, 0.62, 0.25), 0.85, 0.8)

local held
local st = { aim = Vector3.zero, aiming = false, ads = 0, bloom = 0, kick = 0, reload_until = 0, next_shot = 0, hit_marker = 0,
             recoil_debt = 0, heading = nil, down_time = 0, sway = Vector3.zero, last_yaw = 0, last_pitch = 0, queued = 0 }

local function fp() return camera.first_person() end

-- a point on the held gun in its own space
local function point(def, z, y)
  local b = def.bounds
  local cx, cz = b.center.x, b.center.z
  if def.flip then cx, cz = -cx, -cz end
  return vec(cx, b.min.y + b.size.y * y, (z - 0.5) * b.size.z + cz) * held.scale
end

local function load_model(def)
  if not def.model then
    def.model = model.load(def.file)
    def.bounds = def.model.BodyBounds
  end
  return def.model
end

local function play(clip, v, pitch)
  if held and clip then audio.play(held.voice, clip, v * volume.value * 1.25, pitch or rand(0.96, 1.04)) end
end

local function aim_ray(me)
  if fp() then
    local cam = camera.main().transform
    return cam.position, cam.forward
  end
  local rig = camera.rig()
  return me.spine2.transform.position + Vector3.up * game.scale(me) * 0.25, euler(rig.pitch, rig.yaw, 0) * Vector3.forward
end

local function find_aim(me)
  local origin, dir = aim_ray(me)
  st.aim = origin + dir * 1500
  for _, h in ipairs(physics.raycast_all(origin, dir, 3000)) do
    local part = physics.part(h.collider)
    if not (part and part.ragdoll == me) then st.aim = h.point break end
  end
  return st.aim
end

-- third person: rifle in the shoulder, pistol held out in both hands
local function body_pose(me)
  local def, sc = held.def, game.scale(me)
  local aim = find_aim(me)
  local facing = st.heading or body.facing(me)
  local right = Vector3.Cross(Vector3.up, facing)
  local shoulder = me.upperArmRight.transform.position
  local anchor
  if def.auto then
    anchor = shoulder + facing * sc * 0.06 + Vector3.up * sc * Mathf.Lerp(-0.02, 0.04, st.ads)
  else
    anchor = me.spine2.transform.position + facing * sc * Mathf.Lerp(0.5, 0.58, st.ads) + Vector3.up * sc * Mathf.Lerp(0.16, 0.3, st.ads) + right * sc * 0.03
  end
  local dir = (aim - anchor).normalized
  if Vector3.Angle(Vector3.ProjectOnPlane(dir, Vector3.up), facing) > 50 then
    dir = (Vector3.RotateTowards(facing, Vector3.ProjectOnPlane(dir, Vector3.up).normalized, math.rad(50), 0) + Vector3.up * dir.y).normalized
  end
  local rot = Quaternion.LookRotation(dir, Vector3.up) * euler(-st.kick * (def.auto and 5 or 12), 0, 0)
  local hold = def.auto and point(def, def.stock[1], def.stock[2]) or point(def, def.grip[1], def.grip[2])
  local pos = anchor - rot * hold - rot * Vector3.forward * st.kick * sc * 0.04
  if Time.time < st.reload_until then
    rot = rot * euler(35, 0, -25)
    pos = pos - Vector3.up * sc * 0.08
  end
  return pos, rot
end

-- first person: a viewmodel at the hip or centred on the sights
local function view_pose(me)
  local def, sc = held.def, game.scale(me)
  local cam = camera.main().transform
  find_aim(me)
  local sight = point(def, def.sight[1], def.sight[2])
  local hip = cam.position + (cam.right * def.hip[1] + cam.up * def.hip[2] + cam.forward * def.hip[3]) * sc
  local ads = cam.position + cam.forward * sc * def.ads
  local rot = cam.rotation * euler(st.sway.y, st.sway.x, st.sway.x * 0.6) * euler(-st.kick * (def.auto and 3 or 7), 0, 0)
  local bob = math.sin(Time.time * 9) * Mathf.Clamp01(me.velocity.magnitude / 60) * sc * 0.006 * (1 - st.ads * 0.8)
  local pos
  if st.ads > 0 then
    local ads_pos = ads - rot * sight
    local hip_pos = hip - rot * point(def, def.grip[1], def.grip[2])
    pos = Vector3.Lerp(hip_pos, ads_pos, st.ads)
  else
    pos = hip - rot * point(def, def.grip[1], def.grip[2])
  end
  pos = pos + cam.up * bob - cam.forward * st.kick * sc * 0.03
  if Time.time < st.reload_until then
    local t = 1 - math.abs((st.reload_until - Time.time) / def.reload * 2 - 1)
    rot = rot * euler(30 * t, 0, -35 * t)
    pos = pos - cam.up * sc * 0.05 * t
  end
  return pos, rot
end

local function set_control(me, on)
  body.set_hands_busy(me, on)
  body.arm_swing(me, not on)
  body.reach_items(me, not on)
  body.arm_strength(me, on and 0.15 or nil)
  if not on then body.heading(me, nil) end
end

local function reset_camera()
  camera.shift(Vector3.zero)
  camera.fov(1)
  camera.hide_arms(false)
end

local function holster()
  if not held then return end
  for _, j in ipairs(held.joints) do destroy(j) end
  if alive(held.owner) then set_control(held.owner, false) end
  destroy(held.go)
  held, st.aiming, st.heading = nil, false, nil
  reset_camera()
end

local function drop()
  local h = held
  for _, j in ipairs(h.joints) do destroy(j) end
  local box = add(h.go, "BoxCollider")
  box.center, box.size = point(h.def, 0.5, 0.5), h.def.bounds.size * h.scale
  h.rb.isKinematic = false
  h.rb.mass = 1
  h.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic
  h.rb.velocity = alive(h.owner) and h.owner.spine1.rigidBody.velocity or Vector3.zero
  h.rb.angularVelocity = Random.insideUnitSphere * 6
  h.go.name = "DroppedGun"
  destroy(h.go, 30)
  if alive(h.owner) then set_control(h.owner, false) end
  held, st.heading = nil, nil
  reset_camera()
  toast(string.format("Dropped the %s · %s to draw it again", h.def.name, h.def.key.label))
end

local function equip(def)
  holster()
  local me = game.player()
  if not def or not me or game.seated(me) then return end
  local m = load_model(def)
  def.ammo = def.ammo or def.mag
  held = { def = def, owner = me, joints = {}, scale = def.length * game.scale(me) / 9 }
  held.go = new_object("HeldGun")
  held.rb = add(held.go, "Rigidbody")
  held.rb.isKinematic = true
  held.rb.interpolation = RigidbodyInterpolation.Interpolate
  held.model = model.clone(m.Body, held.go.transform)
  if def.flip then held.model.transform.localRotation = euler(0, 180, 0) end
  held.model.transform.localScale = Vector3.one * held.scale
  held.muzzle = new_object("muzzle", held.go.transform).transform
  held.muzzle.localPosition = point(def, def.muzzle[1], def.muzzle[2])
  st.heading = body.facing(me)
  local pos, rot = body_pose(me)
  held.go.transform:SetPositionAndRotation(pos, rot)
  held.rb.position, held.rb.rotation = pos, rot
  held.grip, held.support = point(def, def.grip[1], def.grip[2]), point(def, def.support[1], def.support[2])
  for _, pair in ipairs({ { me.handRight, held.grip }, { me.handLeft, held.support } }) do
    local j = body.grip(pair[1], held.rb, pair[2])
    if j then table.insert(held.joints, j) end
  end
  held.voice = audio.source(held.go, { spatial = 0.4, min = 30, max = 800 })
  set_control(me, true)
  play(def.reload_sound, 0.4)
end

-- effects

local function muzzle_flash(sc)
  local go = new_object("MuzzleFlash")
  go.transform:SetPositionAndRotation(held.muzzle.position, held.muzzle.rotation)
  fx.emit(fx.particles(go, { duration = 0.05, lifetime = 0.04, speed = { sc * 0.4, sc * 2.5 }, size = { sc * 0.1, sc * 0.26 }, angle = 14, material = flash_mat }), 7)
  local light = add(go, "Light")
  light.type, light.color, light.range, light.intensity = LightType.Point, rgb(1, 0.75, 0.4), sc * 5, 3.5
  destroy(light, 0.04)
  destroy(go, 0.3)
end

local function tracer(a, b, sc)
  local lr = add(new_object("Tracer"), "LineRenderer")
  local d = b - a
  lr.positionCount = 2
  lr:SetPosition(0, a + d.normalized * math.min(d.magnitude * 0.15, sc * 3))
  lr:SetPosition(1, a + d.normalized * math.min(d.magnitude, sc * 40))
  lr.startWidth, lr.endWidth = sc * 0.012, sc * 0.004
  lr.sharedMaterial = tracer_mat
  destroy(lr.gameObject, 0.05)
end

local function shell(sc)
  local t = held.go.transform
  local go = primitive("Cylinder", nil, true)
  local d = sc * (held.def.auto and 0.011 or 0.009)
  go.transform.localScale = vec(d, d * 2.2, d)
  go.transform.position = t:TransformPoint(point(held.def, held.def.eject[1], held.def.eject[2])) + t.right * sc * 0.02
  go.transform.rotation = t.rotation * euler(0, 0, 90)
  go:GetComponent("Renderer").sharedMaterial = brass_mat
  local rb = add(go, "Rigidbody")
  rb.mass = 0.01
  rb.velocity = (t.right * 1.2 + t.up * 0.7 - t.forward * 0.3) * sc * rand(1.1, 1.6)
  rb.angularVelocity = Random.insideUnitSphere * 30
  destroy(go, 5)
end

local function impact(h, sc, metal)
  local go = new_object("BulletImpact")
  go.transform:SetPositionAndRotation(h.point, Quaternion.LookRotation(h.normal))
  local ps = fx.particles(go, metal and {
    duration = 0.1, lifetime = { 0.15, 0.4 }, speed = { sc * 0.5, sc * 4 }, size = { sc * 0.015, sc * 0.03 },
    color = { rgb(1, 0.85, 0.4), rgb(1, 0.6, 0.2) }, gravity = 0.4, angle = 35, material = dust_mat, stretch = 0.03,
  } or {
    duration = 0.1, lifetime = { 0.4, 0.9 }, speed = { sc * 0.5, sc * 1.5 }, size = { sc * 0.08, sc * 0.2 },
    color = rgb(0.55, 0.5, 0.42, 0.7), gravity = 0.05, angle = 35, material = dust_mat,
  })
  fx.emit(ps, metal and 12 or 8)
  destroy(go, 1.2)
  if not h.rigidbody then
    local w = sc * rand(0.03, 0.045)
    fx.decal("BulletHole", 150, h.point, h.normal, Vector3.zero, w, w, hole_mat)
  end
end

local function shoot(me)
  local def, sc = held.def, game.scale(me)
  local from = held.muzzle.position
  local target = find_aim(me)
  local dir = (target - from).normalized
  if Vector3.Angle(dir, held.muzzle.forward) > 12 then dir = held.muzzle.forward end
  local spread = (def.spread + st.bloom) * (st.aiming and 0.3 or 1) * (me.velocity.magnitude > 20 and 1.8 or 1)
  dir = Quaternion.AngleAxis(rand(0, 360), dir) * (Quaternion.AngleAxis(rand(0, spread), Vector3.Cross(dir, Vector3.up).normalized) * dir)
  st.bloom = math.min(2.5, st.bloom + def.bloom)

  local stop = from + dir * 1500
  for _, h in ipairs(physics.raycast_all(from, dir, 3000)) do
    local part = physics.part(h.collider)
    if not (part and part.ragdoll == me) then
      stop = h.point
      local power = def.power * damage.value
      if part and part.ragdoll then
        if part == part.ragdoll.head then power = power * 2 end
        events.emit("bullet_hit", part, h.point, dir, power)
        if body.live(part) then part.rigidBody:AddForceAtPosition(dir * power * 0.12, h.point, ForceMode.VelocityChange) end
        if part == part.ragdoll.head or part == part.ragdoll.spine2 or math.random() < 0.25 then game.unground(part.ragdoll, true) end
        st.hit_marker = Time.time + 0.18
        play(sounds.hit, 0.6, 1)
      else
        if h.rigidbody and not h.rigidbody.isKinematic then
          h.rigidbody:AddForceAtPosition(dir * math.min(h.rigidbody.mass, 50) * 10, h.point, ForceMode.Impulse)
        end
        impact(h, sc, game.is_vehicle(h.rigidbody))
      end
      break
    end
  end

  play(def.shots[randi(1, #def.shots + 1)], 1)
  muzzle_flash(sc)
  tracer(from, stop, sc)
  shell(sc)
  st.kick = 1
  local k = def.kick * recoil.value * (st.aiming and 0.65 or 1)
  local rig = camera.rig()
  rig.pitch = rig.pitch - k
  rig.yaw = rig.yaw + rand(-0.3, 0.3) * k
  st.recoil_debt = st.recoil_debt + k * 0.75
  camera.shake(0.08 * recoil.value, 10)
end

local function start_reload()
  if Time.time < st.reload_until then return end
  st.reload_until = Time.time + held.def.reload
  play(held.def.reload_sound, 0.9)
  held.def.ammo = held.def.mag
end

-- callbacks

menu.button("Glock", function() equip(guns[1]) end)
menu.button("AK-47", function() equip(guns[2]) end)
menu.button("Holster", holster)

function on_disable() holster() end
function on_unload() holster() end
function on_round_start() held, st.heading = nil, nil; reset_camera() end

function update(dt)
  local me = game.player()
  if not me or game.seated(me) or body.dead(me) then holster() return end
  for _, def in ipairs(guns) do
    if def.key.down then if held and held.def == def then holster() else equip(def) end end
  end
  if holster_key.down then holster() end
  if not held then return end
  if held.owner ~= me then holster() return end

  local def = held.def
  local rig = camera.rig()
  st.aiming = (input.mouse(1) or st.force_aim) and Time.time >= st.reload_until
  st.ads = Mathf.MoveTowards(st.ads, st.aiming and 1 or 0, dt * 7)
  st.bloom = Mathf.MoveTowards(st.bloom, 0, dt * (def.auto and 2.2 or 3))
  st.kick = Mathf.MoveTowards(st.kick, 0, dt * 9)
  local recover = math.min(st.recoil_debt, st.recoil_debt * dt * 6 + dt * 2)
  rig.pitch = rig.pitch + recover
  st.recoil_debt = st.recoil_debt - recover
  local dyaw, dpitch = Mathf.DeltaAngle(st.last_yaw, rig.yaw), rig.pitch - st.last_pitch
  st.last_yaw, st.last_pitch = rig.yaw, rig.pitch
  st.sway = Vector3.Lerp(st.sway, vec(Mathf.Clamp(-dyaw * 0.6, -4, 4), Mathf.Clamp(-dpitch * 0.6, -4, 4), 0), dt * 10)

  camera.hide_arms(fp())
  camera.fov(Mathf.Lerp(1, zoom.value, st.ads))
  camera.shift(vec(0.42, 0.2, 0) * game.scale(me) * (1 + st.ads * 0.6))

  if game.counting_down() or Time.time < st.reload_until then return end
  if def.ammo <= 0 and not infinite.value and input.mouse_down(0) then play(sounds.dry_fire, 0.8) end
  if reload_key.down and def.ammo < def.mag and not infinite.value then start_reload() return end
  local trigger = (def.auto and input.mouse(0) or input.mouse_down(0)) or st.queued > 0
  if trigger and Time.time >= st.next_shot and (def.ammo > 0 or infinite.value) then
    st.next_shot = Time.time + 60 / def.rpm
    if st.queued > 0 then st.queued = st.queued - 1 end
    shoot(me)
    if not infinite.value then
      def.ammo = def.ammo - 1
      if def.ammo <= 0 then start_reload() end
    end
  end
end

function fixed_update(dt)
  if not held then return end
  local me = held.owner
  local sc = game.scale(me)

  local up = Vector3.Dot((me.head.transform.position - me.spine1.transform.position).normalized, Vector3.up)
  st.down_time = (up < 0.3 and not game.grounded(me)) and st.down_time + dt or 0
  if st.down_time > 0.8 then st.down_time = 0; drop() return end

  local look = Vector3.ProjectOnPlane(camera.main().transform.forward, Vector3.up)
  if look.sqrMagnitude > 1e-4 then
    st.heading = Vector3.RotateTowards(st.heading or look.normalized, look.normalized, math.rad(420) * dt, 0).normalized
    body.heading(me, st.heading)
  end

  local pos, rot = body_pose(me)
  local k = 1 - math.exp(-dt * 25)
  if (held.rb.position - pos).sqrMagnitude > sc * sc then held.rb.position = pos else pos = Vector3.Lerp(held.rb.position, pos, k) end
  held.rb:MovePosition(pos)
  held.rb:MoveRotation(Quaternion.Slerp(held.rb.rotation, rot, k))

  local t = held.go.transform
  body.reach(me.handRight, me.lowerArmRight, me.upperArmRight, t:TransformPoint(held.grip), 260, 20)
  body.reach(me.handLeft, me.lowerArmLeft, me.upperArmLeft, t:TransformPoint(held.support), 260, 20)
end

function late_update()
  if held and fp() then
    local pos, rot = view_pose(held.owner)
    held.go.transform:SetPositionAndRotation(pos, rot)
  end
end

function draw()
  if not held then return end
  local def = held.def
  local cx, cy = ui.width() / 2, ui.height() / 2
  if not fp() then
    local p = camera.to_screen(st.aim)
    if p then cx, cy = p.x, p.y end
  end
  if not (fp() and st.ads > 0.8) then
    local gap = 5 + (def.spread + st.bloom) * (st.aiming and 0.3 or 1) * 9
    local white = rgb(1, 1, 1, 0.9)
    ui.rect(cx - gap - 7, cy - 1, 7, 2, white)
    ui.rect(cx + gap, cy - 1, 7, 2, white)
    ui.rect(cx - 1, cy - gap - 7, 2, 7, white)
    ui.rect(cx - 1, cy + gap, 2, 7, white)
  end
  if Time.time < st.hit_marker then
    for i = 0, 3 do
      local a = math.rad(45 + i * 90)
      ui.rect(cx + math.cos(a) * 11 - 4.5, cy + math.sin(a) * 11 - 1, 9, 2, rgb(1, 1, 1, 0.95), 45 + i * 90)
    end
  end
  local ammo = infinite.value and "∞" or Time.time < st.reload_until and "reloading" or string.format("%d / %d", def.ammo, def.mag)
  ui.hud(def.name .. "   " .. ammo, string.format("LMB fire · RMB aim · %s reload · %s holster", reload_key.label, holster_key.label))
end

function test_fire(n) st.queued = n end
function test_aim(on) st.force_aim = on end
