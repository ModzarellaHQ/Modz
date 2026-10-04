local S = "Guns"
local damage = setting.number{ section = S, name = "Damage", default = 1, min = 0.2, max = 5, desc = "Bullet damage multiplier." }
local infinite = setting.toggle{ section = S, name = "Infinite ammo", default = false, desc = "Never reload." }
local volume = setting.number{ section = S, name = "Volume", default = 0.8, min = 0, max = 1, desc = "Gunshot volume." }
local recoil = setting.number{ section = S, name = "Recoil", default = 1, min = 0, max = 3, desc = "Camera kick per shot.", advanced = true }
local zoom = setting.number{ section = S, name = "Aim zoom", default = 0.6, min = 0.3, max = 1, desc = "Field of view while aiming (1 = no zoom).", advanced = true }
local gun_size = setting.number{ section = S, name = "Gun size", default = 1, min = 0.5, max = 2, desc = "Visual scale of the guns.", advanced = true }
local crosshair = setting.toggle{ section = S, name = "Crosshair", default = true, desc = "Show a crosshair while a gun is out.", advanced = true }
local pistol_key = setting.key{ name = "Glock", default = "Alpha1", desc = "Draw the pistol." }
local rifle_key = setting.key{ name = "AK-47", default = "Alpha2", desc = "Draw the rifle." }
local holster_key = setting.key{ name = "Holster", default = "Alpha3", desc = "Put the gun away." }
local reload_key = setting.key{ name = "Reload", default = "R", desc = "Reload." }

local sounds = audio.folder("sounds")
local guns = {
  { name = "Glock 17", file = "glock.glb", flip = true, length = 1.9, boost = 1.5, mag = 17, rpm = 900, auto = false, power = 150,
    spread = 0.6, kick = 2.4, reload = 1.6, shots = { sounds.pistol_shot1, sounds.pistol_shot2 }, reload_sound = sounds.pistol_reload,
    grip_z = 0.22, grip_y = 0.3, muzzle_y = 0.8, support_z = 0.24, two_hand = true, key = pistol_key },
  { name = "AK-47", file = "ak47.glb", flip = false, length = 8.8, boost = 1.1, mag = 30, rpm = 600, auto = true, power = 200,
    spread = 1.1, kick = 1.1, reload = 2.4, shots = { sounds.rifle_shot1, sounds.rifle_shot2, sounds.rifle_shot3 }, reload_sound = sounds.rifle_reload,
    grip_z = 0.36, grip_y = 0.28, muzzle_y = 0.72, support_z = 0.62, two_hand = false, key = rifle_key },
}

local flash_mat = mat.unlit(mat.blob(32, 0.5, 61), rgb(1, 0.85, 0.45))
local tracer_mat = mat.unlit(nil, rgb(1, 0.9, 0.6, 0.7))
local hole_mat = mat.unlit(mat.blob(32, 0.8, 62), rgb(0.06, 0.05, 0.05, 0.92))
local dust_mat = mat.unlit(mat.blob(32, 0.4, 63))
local brass_mat = mat.solid(rgb(0.8, 0.62, 0.25), 0.85, 0.8)

local held       -- { def, go, rb, model, muzzle, owner, joints, pos, rot, voice }
local aim_point = Vector3.zero
local aiming, aim_blend, bloom, kick, reload_until, next_shot, hit_marker, down_time = false, 0, 0, 0, 0, 0, 0, 0
local shots_queued = 0

-- template bounds: points along the gun, z in -0.5..0.5 from stock to muzzle, y as a fraction of the height
local function point(def, z, yf)
  local b = def.bounds
  local cx, cz = b.center.x, b.center.z
  if def.flip then cx, cz = -cx, -cz end
  return vec(cx, b.min.y + b.size.y * yf, z * b.size.z + cz) * held.scale
end

local function template(def)
  if not def.model then
    def.model = model.load(def.file)
    def.bounds = def.model.BodyBounds
  end
  return def.model
end

local function first_person() return camera.first_person() end

local function play(clip, v)
  if held and clip then audio.play(held.voice, clip, v * volume.value * 1.25, rand(0.96, 1.04)) end
end

local function compute_aim(me)
  local rig = camera.rig()
  local origin, dir
  if first_person() then
    local cam = camera.main().transform
    origin, dir = cam.position, cam.forward
  else
    origin = me.spine2.transform.position + Vector3.up * game.scale(me) * 0.2
    dir = euler(rig.pitch, rig.yaw, 0) * Vector3.forward
  end
  local p = origin + dir * 1500
  for _, h in ipairs(physics.raycast_all(origin, dir, 3000)) do
    local part = physics.part(h.collider)
    if not (part and part.ragdoll == me) then p = h.point break end
  end
  aim_point = p
  return p
end

local function clamp_aim(dir, facing)
  local flat = Vector3.ProjectOnPlane(dir, Vector3.up)
  local len = dir.magnitude
  if len < 1e-4 or flat.sqrMagnitude < 1e-6 then return facing end
  local f = Vector3.RotateTowards(facing, flat.normalized, 55 * Mathf.Deg2Rad, 0)
  return (f * flat.magnitude + Vector3.up * dir.y) / len
end

local function target_pose(me)
  local def = held.def
  local sc = game.scale(me)
  local aim = compute_aim(me)
  local up = Vector3.up
  local pos, rot
  if first_person() then
    local cam = camera.main().transform
    local hip = cam.position + cam.right * sc * 0.13 - cam.up * sc * 0.12 + cam.forward * sc * 0.36
    local ads = cam.position - cam.up * sc * (def.auto and 0.055 or 0.06) + cam.forward * sc * 0.3
    local grip = Vector3.Lerp(hip, ads, aim_blend)
    rot = Quaternion.LookRotation(aim - grip, cam.up)
    pos = grip - rot * point(def, -0.5 + def.grip_z, def.grip_y)
  else
    local chest = me.spine2.transform.position
    local facing = body.facing(me)
    local flat = clamp_aim(Vector3.ProjectOnPlane(aim - chest, up), facing)
    local right = Vector3.Cross(up, flat)
    if def.auto then
      local shoulder = chest + right * sc * 0.12 + up * sc * Mathf.Lerp(0.12, 0.3, aim_blend)
      rot = Quaternion.LookRotation(clamp_aim(aim - shoulder, facing), up)
      pos = shoulder - rot * point(def, -0.5, def.grip_y + 0.2)
    else
      local grip = chest + flat * sc * Mathf.Lerp(0.55, 0.62, aim_blend) + up * sc * Mathf.Lerp(0.12, 0.38, aim_blend) + right * sc * 0.03
      rot = Quaternion.LookRotation(clamp_aim(aim - grip, facing), up)
      pos = grip - rot * point(def, -0.5 + def.grip_z, def.grip_y)
    end
  end
  local bob = math.sin(Time.time * 9) * Mathf.Clamp01(me.velocity.magnitude / 60) * sc * 0.012 * (1 - aim_blend * 0.7)
  pos = pos + up * bob
  rot = rot * euler(-kick * (def.auto and 4 or 9), 0, 0)
  pos = pos - rot * Vector3.forward * kick * sc * (def.auto and 0.05 or 0.035)
  return pos, rot
end

local function set_control(me, on)
  body.set_hands_busy(me, on)
  body.arm_swing(me, not on)
  body.reach_items(me, not on)
  body.arm_strength(me, on and 0.04 or nil)
  if not on then body.heading(me, nil) end
end

local function holster()
  if not held then return end
  for _, j in ipairs(held.joints) do destroy(j) end
  if alive(held.owner) then set_control(held.owner, false) end
  destroy(held.go)
  held = nil
  aiming = false
  camera.shift(Vector3.zero)
  camera.fov(1)
end

local function drop()
  if not held then return end
  local h = held
  for _, j in ipairs(h.joints) do destroy(j) end
  local box = add(h.go, "BoxCollider")
  local b = h.def.bounds
  box.center, box.size = point(h.def, 0, 0.5), b.size * h.scale
  h.rb.isKinematic = false
  h.rb.mass = 1
  h.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic
  h.rb.velocity = alive(h.owner) and h.owner.spine1.rigidBody.velocity or Vector3.zero
  h.rb.angularVelocity = Random.insideUnitSphere * 6
  h.go.name = "DroppedGun"
  destroy(h.go, 30)
  if alive(h.owner) then set_control(h.owner, false) end
  held = nil
  camera.shift(Vector3.zero)
  camera.fov(1)
  toast(string.format("Dropped the %s (%s to draw again)", h.def.name, h.def.key.label))
end

local function equip(def)
  holster()
  local me = game.player()
  if not def or not me or game.seated(me) then return end
  local m = template(def)
  def.ammo = def.ammo or def.mag
  local sc = game.scale(me) / 9
  held = { def = def, owner = me, joints = {}, scale = def.length * def.boost * gun_size.value * sc }
  held.go = new_object("HeldGun")
  held.rb = add(held.go, "Rigidbody")
  held.rb.isKinematic = true
  held.rb.interpolation = RigidbodyInterpolation.Interpolate
  held.model = model.clone(m.Body, held.go.transform)
  if def.flip then held.model.transform.localRotation = euler(0, 180, 0) end
  held.model.transform.localScale = Vector3.one * held.scale
  held.muzzle = new_object("muzzle", held.go.transform).transform
  held.muzzle.localPosition = point(def, 0.5, def.muzzle_y)
  held.pos, held.rot = target_pose(me)
  held.go.transform:SetPositionAndRotation(held.pos, held.rot)
  held.rb.position, held.rb.rotation = held.pos, held.rot
  local grip = point(def, -0.5 + def.grip_z, def.grip_y)
  local support = def.two_hand and point(def, -0.5 + def.grip_z + 0.04, def.grip_y * 0.9) or point(def, -0.5 + def.support_z, def.grip_y * 1.25)
  for _, pair in ipairs({ { me.handRight, grip }, { me.handLeft, support } }) do
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
  local ps = fx.particles(go, { duration = 0.05, lifetime = 0.045, speed = { sc * 0.5, sc * 3 }, size = { sc * 0.12, sc * 0.3 }, angle = 12, material = flash_mat })
  fx.emit(ps, 6)
  local light = add(go, "Light")
  light.type = LightType.Point
  light.color, light.range, light.intensity = rgb(1, 0.75, 0.4), sc * 4, 3
  destroy(light, 0.04)
  destroy(go, 0.3)
end

local function tracer(a, b, sc)
  local go = new_object("Tracer")
  local lr = add(go, "LineRenderer")
  local d = b - a
  lr.positionCount = 2
  lr:SetPosition(0, a + d.normalized * math.min(d.magnitude * 0.1, sc * 2))
  lr:SetPosition(1, a + d.normalized * math.min(d.magnitude, sc * 25))
  lr.startWidth, lr.endWidth = sc * 0.018, sc * 0.006
  lr.sharedMaterial = tracer_mat
  destroy(go, 0.04)
end

local function shell(sc)
  local t = held.model.transform
  local go = primitive("Cylinder", nil, true)
  local d = sc * (held.def.auto and 0.012 or 0.01)
  go.transform.localScale = vec(d, d * 2.2, d)
  go.transform.position = t.position + t.up * sc * 0.06 + t.right * sc * 0.04
  go.transform.rotation = t.rotation * euler(0, 0, 90)
  go:GetComponent("Renderer").sharedMaterial = brass_mat
  local rb = add(go, "Rigidbody")
  rb.mass = 0.01
  rb.velocity = (t.right + t.up * 0.6 - t.forward * 0.2) * sc * rand(1.2, 1.8)
  rb.angularVelocity = Random.insideUnitSphere * 30
  destroy(go, 5)
end

local function impact_puff(p, n, sc, metal)
  local go = new_object("BulletImpact")
  go.transform:SetPositionAndRotation(p, Quaternion.LookRotation(n))
  local ps = fx.particles(go, metal and {
    duration = 0.1, lifetime = { 0.15, 0.4 }, speed = { sc * 0.5, sc * 4 }, size = { sc * 0.015, sc * 0.03 },
    color = { rgb(1, 0.85, 0.4), rgb(1, 0.6, 0.2) }, gravity = 0.4, angle = 35, material = dust_mat, stretch = 0.03,
  } or {
    duration = 0.1, lifetime = { 0.4, 0.9 }, speed = { sc * 0.5, sc * 1.5 }, size = { sc * 0.08, sc * 0.2 },
    color = rgb(0.55, 0.5, 0.42, 0.7), gravity = 0.05, angle = 35, material = dust_mat,
  })
  fx.emit(ps, metal and 12 or 8)
  destroy(go, 1.2)
end

local function shoot(me)
  local def, sc = held.def, game.scale(me)
  local from = held.muzzle.position
  local dir = (compute_aim(me) - from).normalized
  if Vector3.Angle(dir, held.muzzle.forward) > 15 then dir = held.muzzle.forward end
  local spread = def.spread * (aiming and 0.35 or 1) * (1 + bloom) * (me.velocity.magnitude > 20 and 1.6 or 1)
  dir = Quaternion.AngleAxis(rand(0, 360), dir) * (Quaternion.AngleAxis(rand(0, spread), Vector3.Cross(dir, Vector3.up).normalized) * dir)
  bloom = math.min(2, bloom + 0.35)

  local stop = from + dir * 1500
  for _, h in ipairs(physics.raycast_all(from, dir, 3000)) do
    local part = physics.part(h.collider)
    if not (part and part.ragdoll == me) then
      stop = h.point
      local power = def.power * damage.value
      if part and part.ragdoll then
        events.emit("bullet_hit", part, h.point, dir, power)
        if body.live(part) then part.rigidBody:AddForce(dir * (def.auto and 28 or 22), ForceMode.VelocityChange) end
        local core = part == part.ragdoll.head or part == part.ragdoll.spine1 or part == part.ragdoll.spine2
        if core or math.random() < 0.3 then game.unground(part.ragdoll, true) end
        hit_marker = Time.time + 0.15
      else
        if h.rigidbody and not h.rigidbody.isKinematic then
          h.rigidbody:AddForceAtPosition(dir * math.min(h.rigidbody.mass, 50) * 12, h.point, ForceMode.Impulse)
        end
        impact_puff(h.point, h.normal, sc, game.is_vehicle(h.rigidbody))
        if not h.rigidbody then
          local w = sc * rand(0.04, 0.06)
          fx.decal("BulletHole", 150, h.point, h.normal, Vector3.zero, w, w, hole_mat)
        end
      end
      break
    end
  end

  play(def.shots[randi(1, #def.shots + 1)], 1)
  muzzle_flash(sc)
  tracer(from, stop, sc)
  shell(sc)
  kick = 1
  local rig = camera.rig()
  local k = def.kick * recoil.value * (aiming and 0.6 or 1)
  rig.pitch = rig.pitch - k
  rig.yaw = rig.yaw + rand(-0.35, 0.35) * k
  camera.shake(0.15 * recoil.value, 8)
end

local function start_reload()
  reload_until = Time.time + held.def.reload
  play(held.def.reload_sound, 0.9)
  held.def.ammo = held.def.mag
end

-- callbacks

menu.button("Glock", function() equip(guns[1]) end)
menu.button("AK-47", function() equip(guns[2]) end)
menu.button("Holster", holster)

function on_disable() holster() end
function on_unload() holster() end
function on_round_start() held = nil end

function update(dt)
  local me = game.player()
  if not me or game.seated(me) or body.dead(me) then holster() return end
  for _, def in ipairs(guns) do
    if def.key.down then if held and held.def == def then holster() else equip(def) end end
  end
  if holster_key.down then holster() end
  if not held then return end
  if held.owner ~= me then holster() return end

  aiming = input.mouse(1)
  aim_blend = Mathf.MoveTowards(aim_blend, aiming and 1 or 0, dt * 6)
  bloom = Mathf.MoveTowards(bloom, 0, dt * 4)
  kick = Mathf.MoveTowards(kick, 0, dt * 6)
  local look = Vector3.ProjectOnPlane(camera.main().transform.forward, Vector3.up)
  if look.sqrMagnitude > 1e-4 then body.heading(me, look.normalized) end
  camera.fov(Mathf.Lerp(1, zoom.value, aim_blend))
  camera.shift(vec(0.5, 0.22, 0) * game.scale(me) * (1 + aim_blend))

  if game.counting_down() or Time.time < reload_until then return end
  local def = held.def
  if def.ammo <= 0 and not infinite.value and input.mouse_down(0) then play(sounds.dry_fire, 0.8) end
  if reload_key.down and def.ammo < def.mag and not infinite.value then start_reload() end
  local trigger = (def.auto and input.mouse(0) or input.mouse_down(0)) or shots_queued > 0
  if trigger and Time.time >= next_shot and (def.ammo > 0 or infinite.value) then
    next_shot = Time.time + 60 / def.rpm
    if shots_queued > 0 then shots_queued = shots_queued - 1 end
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
  local tilt = Vector3.Dot((me.head.transform.position - me.spine1.transform.position).normalized, Vector3.up)
  down_time = tilt < 0.5 and down_time + dt or 0
  if down_time > 0.5 then down_time = 0; drop() return end
  local pos, rot = target_pose(me)
  local k = 1 - math.exp(-dt * (first_person() and 40 or 22))
  local sc = game.scale(me)
  if (held.pos - pos).sqrMagnitude > sc * sc then held.pos = pos else held.pos = Vector3.Lerp(held.pos, pos, k) end
  held.rot = Quaternion.Slerp(held.rot, rot, k)
  held.rb:MovePosition(held.pos)
  held.rb:MoveRotation(held.rot)
end

function late_update()
  if held and first_person() then
    local pos, rot = target_pose(held.owner)
    held.go.transform:SetPositionAndRotation(pos, rot)
  end
end

function draw()
  if not held then return end
  local def = held.def
  local cx, cy = ui.width() / 2, ui.height() / 2
  if not first_person() then
    local p = camera.to_screen(aim_point)
    if p then cx, cy = p.x, p.y end
  end
  if crosshair.value then
    local gap = 6 + def.spread * (aiming and 0.35 or 1) * (1 + bloom) * 6
    local white = rgb(1, 1, 1, 0.85)
    ui.rect(cx - gap - 8, cy - 1, 8, 2, white)
    ui.rect(cx + gap, cy - 1, 8, 2, white)
    ui.rect(cx - 1, cy - gap - 8, 2, 8, white)
    ui.rect(cx - 1, cy + gap, 2, 8, white)
    if Time.time < hit_marker then
      for i = 0, 3 do
        local a = math.rad(45 + i * 90)
        ui.rect(cx + math.cos(a) * 10.5 - 4.5, cy + math.sin(a) * 10.5 - 1, 9, 2, rgb(1, 0.15, 0.1, 0.95), 45 + i * 90)
      end
    end
  end
  local ammo = infinite.value and "∞" or Time.time < reload_until and "Reloading…" or string.format("%d / %d", def.ammo, def.mag)
  ui.hud(def.name .. "   " .. ammo, string.format("LMB fire · RMB aim · %s reload · %s/%s switch · %s holster",
    reload_key.label, pistol_key.label, rifle_key.label, holster_key.label))
end

function test_fire(n) shots_queued = n end
