local S = "Toilet"
local power = setting.number{ section = S, name = "Thrust power", default = 1.8, min = 0.5, max = 6, desc = "Thrust in multiples of gravity (1 = hover)." }
local climb = setting.number{ section = S, name = "Max climb speed", default = 100, min = 30, max = 400, desc = "Top speed going up (10 = 1 m/s)." }
local amount = setting.number{ section = S, name = "Poop amount", default = 1, min = 0, max = 4, desc = "Particles and chunks." }
local volume = setting.number{ section = S, name = "Volume", default = 0.7, min = 0, max = 1, desc = "On top of the game's volume." }
local control = setting.number{ section = S, name = "Air control", default = 1, min = 0, max = 3, desc = "How hard WASD tilts the toilet.", advanced = true }
local eject = setting.number{ section = S, name = "Eject impact speed", default = 200, min = 60, max = 600, desc = "Crash speed that throws you off.", advanced = true }
local sit_key = setting.key{ name = "Sit / stand up", default = "T", desc = "Spawn the toilet if needed and sit on it, or stand up." }
local thrust_key = setting.key{ name = "Poop thrust (hold)", default = "Space", desc = "Hold while seated to fly. Ctrl hovers." }

local sounds = audio.folder("sounds")
local farts = { sounds.fart1, sounds.fart2, sounds.fart3 }
local poop_mat = mat.unlit(mat.blob(32, 0.6, 77))
local stain_mat = mat.unlit(mat.blob(96, 1, 78), rgb(0.3, 0.18, 0.06, 0.95))
local chunk_mat = mat.solid(rgb(0.22, 0.12, 0.04), 0.6)

local toilet
local forced_thrust = false
local cam_vel = Vector3.zero

local function vol() return volume.value * audio.sfx() end

local function spawn_chunk(t)
  local s = t.s
  local go = primitive("Capsule", nil, true)
  go.name = "PoopChunk"
  local sz = s * rand(0.08, 0.15)
  go.transform.position = t.nozzle.position
  go.transform.rotation = Random.rotation
  go.transform.localScale = vec(sz, sz * 1.6, sz)
  go:GetComponent("Renderer").sharedMaterial = chunk_mat
  local rb = add(go, "Rigidbody")
  rb.mass = 0.1
  rb.velocity = t.rb.velocity - t.go.transform.up * s * rand(4, 8) + Random.insideUnitSphere * s
  rb.angularVelocity = Random.insideUnitSphere * 10
  local born = Time.time
  physics.on_hit(go, function(c)
    if Time.time - born < 0.15 or c.contactCount == 0 or game.is_vehicle(c.rigidbody) then return end
    local p = c:GetContact(0)
    local w = s * rand(0.3, 0.6)
    fx.decal("PoopStain", 150, p.point, p.normal, Vector3.zero, w, w * rand(0.6, 1.2), stain_mat)
    if math.random() < 0.4 then audio.at(sounds.plop, p.point, vol() * 0.5) end
    destroy(go)
  end)
  destroy(go, 8)
end

local function place_near(t, r)
  local face = Vector3.Cross(r.upperLegRight.transform.position - r.upperLegLeft.transform.position, Vector3.up)
  face = vec(face.x, 0, face.z)
  face = face.sqrMagnitude > 1e-4 and face.normalized or Vector3.forward
  local pos = r:GetRootPosition() + face * t.s * 0.9 + Vector3.up * t.s
  local up = Vector3.up
  local hit = physics.raycast(pos + Vector3.up * 3 * t.s, Vector3.down, 20 * t.s, physics.ground)
  if hit then pos, up = hit.point + hit.normal * 0.05, hit.normal end
  t.go.transform:SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.ProjectOnPlane(-face, up), up))
  t.rb.position, t.rb.rotation = t.go.transform.position, t.go.transform.rotation
  t.rb.velocity, t.rb.angularVelocity = Vector3.zero, Vector3.zero
end

local function spawn(r)
  local t = { s = game.scale(r), altitude = 0, thrusting = false, prev_vel = Vector3.zero, next_chunk = 0 }
  t.go = new_object("Rocket Toilet")
  local mass = 0
  for _, p in ipairs(body.parts(r)) do if p.rigidBody then mass = mass + p.rigidBody.mass end end
  t.rb = add(t.go, "Rigidbody")
  t.rb.mass = math.max(1, mass * 1.5)
  t.rb.drag, t.rb.angularDrag = 0.05, 2
  t.rb.interpolation = RigidbodyInterpolation.Interpolate
  t.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic
  game.add_vehicle(t.rb)

  local depth = 0.85 * t.s
  local m = model.load("toilet.glb")
  local vis = model.clone(m.Body, t.go.transform)
  vis.transform.localScale = Vector3.one * depth
  local center, extent = m.BodyBounds.center * depth, m.BodyBounds.size * depth
  t.height = extent.y

  local box = add(t.go, "BoxCollider")
  box.center, box.size = center, extent * 0.95
  t.cols = { box }
  t.rb.centerOfMass = vec(0, t.height * 0.3, 0)

  t.seat = new_object("seat", t.go.transform).transform
  t.seat.localPosition = vec(0, t.height * 0.5, center.z + extent.z * 0.12)
  t.cam = new_object("ToiletCamera", t.go.transform).transform
  t.nozzle = new_object("nozzle", t.go.transform).transform
  t.nozzle.localPosition = vec(0, 0.02 * t.s, center.z + extent.z * 0.12)
  t.nozzle.localRotation = euler(90, 0, 0)
  t.kseat = body.seat(t.go, t.seat, "toilet")

  local s = t.s
  t.poop = fx.particles(t.nozzle.gameObject, {
    loop = true, lifetime = { 0.6, 1.4 }, speed = { s * 3, s * 7 }, size = { s * 0.08, s * 0.22 },
    color = { rgb(0.2, 0.11, 0.03), rgb(0.13, 0.07, 0.02) }, gravity = 0.4, max = 600, angle = 14, radius = s * 0.06,
    collide = true, material = poop_mat,
  })
  t.voice = audio.source(t.go, { spatial = 0.7 })
  t.engine = audio.source(t.go, { clip = sounds.thrust, loop = true, spatial = 0.6, volume = 0 })

  physics.on_hit(t.go, function(c)
    if not t.rider or c.contactCount == 0 or physics.part(c.collider) then return end
    local impact = math.abs(Vector3.Dot(c.relativeVelocity, c:GetContact(0).normal))
    if impact > s * 6 then audio.play(t.voice, sounds.plop, math.min(1, impact / 200) * volume.value) end
    if impact > eject.value then
      local v = t.prev_vel
      t.stand(v * 0.9 + Vector3.up * v.magnitude * 0.2)
      toast("Flushed off the throne!")
    end
  end)

  function t.sit(r)
    if t.rider then t.stand() end
    body.ignore(r, t.cols, true)
    t.rider = r
    t.kseat:Sit(r, t.rb.velocity)
    if game.is_local(r) then camera.target(t.cam) end
    audio.play(t.voice, sounds.plop, 0.6 * volume.value)
  end

  function t.stand(launch)
    local r = t.rider
    t.rider, t.thrusting = nil, false
    if not alive(r) then t.kseat:Stand(Vector3.zero) return end
    if game.is_local(r) then camera.target(nil) end
    t.kseat:Stand(launch or (t.rb.velocity + t.go.transform.forward * s * 2 + Vector3.up * s * 3))
    game.unground(r, true)
    after(0.8, function() if alive(r) and t.rider ~= r then body.ignore(r, t.cols, false) end end)
  end

  place_near(t, r)
  return t
end

local function despawn()
  if not toilet then return end
  if toilet.rider then toilet.stand() end
  destroy(toilet.go)
  toilet = nil
end

local function toggle_sit()
  local me = game.player()
  if not me then return end
  if toilet and toilet.rider == me then toilet.stand() return end
  if game.seated(me) then toast("Get out of the car first.") return end
  if not toilet or not alive(toilet.go) then toilet = spawn(me)
  elseif Vector3.Distance(toilet.go.transform.position, me:GetRootPosition()) > game.scale(me) * 5 then place_near(toilet, me) end
  toilet.sit(me)
end

menu.button("Sit / stand", toggle_sit)
menu.button("New toilet here", function() despawn(); local me = game.player(); if me then toilet = spawn(me) end end)
menu.button("Despawn", despawn)

function on_round_start() toilet = nil end
function on_disable() despawn() end
function on_unload() despawn() end

function update()
  if sit_key.down then toggle_sit() end
  if not toilet or not alive(toilet.go) then return end
  local t = toilet
  local want = t.thrusting and (t.hold and 1 or 0.55) * vol() or 0
  t.engine.volume = t.engine.volume + (want - t.engine.volume) * math.min(1, Time.deltaTime * 8)
  if t.thrusting and not t.was then audio.play(t.voice, farts[randi(1, 4)], 0.8 * volume.value) end
  t.was = t.thrusting
  if t.thrusting and t.rider and game.is_local(t.rider) then camera.shake(0.25, 4) end
end

function fixed_update(dt)
  local t = toilet
  if not t or not alive(t.go) then return end
  local rb, tr, s = t.rb, t.go.transform, t.s
  t.prev_vel = rb.velocity
  if t.rider and not alive(t.rider) then t.rider = nil; t.kseat:Stand(Vector3.zero) end
  local mine = t.rider and game.is_local(t.rider)
  local keys = mine and not game.counting_down() and input.allowed()
  t.hold = keys and (thrust_key.held or input.key("g")) or (mine and forced_thrust) or false
  local hover = keys and (input.key("leftCtrl") or input.key("rightCtrl"))
  t.thrusting = t.hold or (hover and t.altitude > s * 0.3)

  local g = math.abs(Physics.gravity.y)
  local vy = rb.velocity.y
  local up_dot = math.max(0.3, tr.up.y)
  if t.thrusting then
    local top = climb.value
    local full = Mathf.Lerp(power.value * g, g, Mathf.Clamp01((vy - top * 0.7) / (top * 0.3))) - math.max(0, vy - top) * 3
    local accel = t.hold and full or (g - vy * 1.5) / up_dot
    if t.hold and hover then accel = Mathf.Lerp(accel, (g - vy * 1.5) / up_dot, 0.6) end
    rb:AddForce(tr.up * accel * rb.mass)
  end

  if t.rider then
    local mx, my = 0, 0
    if keys then
      if input.key("w") then my = my + 1 end
      if input.key("s") then my = my - 1 end
      if input.key("d") then mx = mx + 1 end
      if input.key("a") then mx = mx - 1 end
    end
    local airborne = t.altitude > s * 0.3 or t.thrusting
    local yaw = airborne and camera.rig().yaw or tr.eulerAngles.y + mx * 40
    local heading = euler(0, yaw, 0) * Vector3.forward
    local right = Vector3.Cross(Vector3.up, heading)
    local tilt = airborne and (heading * my + right * mx) * (0.5 * control.value) or Vector3.zero
    local up_err = Vector3.Cross(tr.up, (Vector3.up + tilt).normalized)
    local yaw_err = Vector3.SignedAngle(Vector3.ProjectOnPlane(tr.forward, Vector3.up), heading, Vector3.up) * Mathf.Deg2Rad
    local torque = up_err * 60 + Vector3.up * yaw_err * (airborne and 12 or 6) - rb.angularVelocity * 8
    rb:AddTorque(torque * (airborne and 1 or 0.5), ForceMode.Acceleration)
    if airborne and mx == 0 and my == 0 then
      rb:AddForce(-Vector3.ProjectOnPlane(rb.velocity, Vector3.up) * 0.8 * rb.mass)
    end
  end

  local hit = physics.raycast(tr.position + Vector3.up * 0.5, Vector3.down, 3000, physics.ground)
  t.altitude = hit and math.max(0, hit.distance - 0.5) or 3000

  fx.rate(t.poop, t.thrusting and 160 * amount.value or 0)
  if t.thrusting and amount.value > 0 and Time.time > t.next_chunk then
    t.next_chunk = Time.time + 0.12 / amount.value
    spawn_chunk(t)
  end
end

function late_update()
  local t = toilet
  if not t or not alive(t.go) then return end
  local want = t.go.transform.position + Vector3.up * (t.height + t.s * 0.6)
  local cur = t.cam.position
  if (cur - want).sqrMagnitude > t.s * t.s * 25 then t.cam.position = want
  else t.cam.position = Vector3.Lerp(cur, want, math.min(1, Time.deltaTime * 20)) end
end

function draw()
  local t = toilet
  if not t or not t.rider or not game.is_local(t.rider) then return end
  ui.hud(string.format("%s   %d up", t.thrusting and "Blasting off" or "On the throne", math.floor(t.altitude)),
    string.format("Hold %s to fly · Ctrl hover · WASD steer · %s stand up", thrust_key.label, sit_key.label))
end

function test_thrust(on) forced_thrust = on end
