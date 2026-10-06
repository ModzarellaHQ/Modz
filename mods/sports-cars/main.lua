local power = setting.number{ section = "Car", name = "Engine power", default = 1, min = 0.1, max = 5, desc = "Acceleration multiplier." }
local top_speed = setting.number{ section = "Car", name = "Top speed", default = 160, min = 30, max = 600, desc = "Engine speed limit (gravity can exceed it)." }
local grip = setting.number{ section = "Car", name = "Tyre grip", default = 1, min = 0.05, max = 3, desc = "Sideways grip. Lower drifts more." }
local cars = {
  ["BMW M2"] = { file = "m2.glb", length = 4.3 },
  ["Toyota AE86"] = { file = "ae86.glb", length = 4.0 },
  ["Nissan Skyline R34"] = { file = "r34.glb", length = 4.3 },
}
local pick = setting.choice{ section = "Car", name = "Car", default = "BMW M2", options = { "BMW M2", "Toyota AE86", "Nissan Skyline R34" }, desc = "Which car spawns next." }
local paint = setting.choice{ section = "Car", name = "Paint", default = "Original", options = { "Original", "Estoril Blue", "Alpine White", "Hellrot", "Brilliant Red", "Black Sapphire" }, desc = "Paint colour for the next car." }
local volume = setting.number{ section = "Car", name = "Engine volume", default = 0.5, min = 0, max = 1, desc = "Car sounds, on top of the game's volume." }
local steering = setting.number{ section = "Car", name = "Steering", default = 1, min = 0.2, max = 3, desc = "Steering multiplier.", advanced = true }
local nitro_power = setting.number{ section = "Car", name = "Nitro (Shift)", default = 2.2, min = 1, max = 8, desc = "Acceleration multiplier while holding Shift.", advanced = true }
local ram = setting.toggle{ section = "Car", name = "Ram launches bots", default = true, desc = "Hitting a bot sends it flying.", advanced = true }
local enter_key = setting.key{ name = "Enter / exit car", default = "E", desc = "Spawn the car if needed and get in, or get out." }
local flip_key = setting.key{ name = "Flip car", default = "R", desc = "While driving: back onto the wheels." }
local reset_key = setting.key{ name = "Reset car", default = "Backspace", desc = "Put the car next to you, upright and stopped." }

local sounds = audio.folder("sounds")
local fx_mat = mat.unlit(mat.blob(32, 0, 21))
local paints = {
  ["Estoril Blue"] = rgb(0.1, 0.22, 0.6), ["Alpine White"] = rgb(0.93, 0.93, 0.91), ["Hellrot"] = rgb(0.75, 0.04, 0.04),
  ["Brilliant Red"] = rgb(0.85, 0.08, 0.05), ["Black Sapphire"] = rgb(0.04, 0.045, 0.06),
}

local car

local function v() return volume.value * audio.sfx() end
local function once(name, vol) if car and sounds[name] then audio.play(car.voice, sounds[name], vol * volume.value) end end

local function facing(r)
  local f = Vector3.Cross(r.upperLegRight.transform.position - r.upperLegLeft.transform.position, Vector3.up)
  f = vec(f.x, 0, f.z)
  return f.sqrMagnitude > 1e-4 and f.normalized or Vector3.forward
end

local function loop(clip) return audio.source(car.go, { clip = clip, loop = true, spatial = 0.75, volume = 0 }) end

local function build(r)
  local def = cars[pick.value] or cars["BMW M2"]
  local m = model.load(def.file)
  local s = game.scale(r)
  local c = { s = s, wheels = {}, spin = { 0, 0, 0, 0 }, comp = { 0, 0, 0, 0 }, steer = 0, throttle = 0, upside = 0,
              last_hit = 0, scrape = 0, slip = 0, last_gear = 0, cam_vel = Vector3.zero }
  car = c
  c.L = def.length * s
  c.model_name = pick.value
  c.W = m.BodyBounds.size.x * c.L
  local model_h = m.BodyBounds.size.y * c.L
  c.H = model_h * 0.5
  c.R = m.WheelRadius > 0 and m.WheelRadius * c.L or 0.34 * s
  c.rest = math.max(0.3 * s, c.R * 1.2)

  c.go = new_object("SportsCar")
  local mass = 0
  for _, p in ipairs(body.parts(r)) do if p.rigidBody then mass = mass + p.rigidBody.mass end end
  local M = math.max(1, mass * 6)
  local g = math.abs(Physics.gravity.y)
  local rb = add(c.go, "Rigidbody")
  c.rb = rb
  rb.mass, rb.drag, rb.angularDrag, rb.maxAngularVelocity = M, 0.02, 1.5, 12
  rb.interpolation = RigidbodyInterpolation.Interpolate
  rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic
  c.k = M * g / 4 / (0.45 * c.rest)
  c.damp = 2 * 0.55 * math.sqrt(c.k * M / 4)

  local box = add(c.go, "BoxCollider")
  box.center = vec(0, c.R * 0.9 + (c.H - c.R * 0.9) * 0.5, 0)
  box.size = vec(c.W * 0.96, c.H - c.R * 0.9, c.L * 0.97)
  local cabin = add(c.go, "BoxCollider")
  cabin.center = vec(0, c.H + model_h * 0.22, -c.L * 0.05)
  cabin.size = vec(c.W * 0.85, model_h * 0.42, c.L * 0.45)
  c.cols = { box, cabin }
  rb.centerOfMass = vec(0, -0.25 * s, 0)

  c.body = model.clone(m.Body, c.go.transform)
  c.body.name = "model"
  c.body.transform.localScale = Vector3.one * c.L
  local tint = paints[paint.value]
  if tint then
    for _, rend in ipairs(children(c.body, "Renderer")) do
      for _, mt in ipairs(list(rend.materials)) do
        local n = string.lower(mt.name)
        if n:find("paint") or n:find("colou?red") or n:find("body") then mt.color = tint end
      end
    end
  end
  local centers, wheel_models = list(m.WheelCenters), list(m.Wheels)
  for i = 1, 4 do
    local center = centers[i] * c.L
    local pivot = new_object("wheel" .. (i - 1), c.go.transform).transform
    pivot.localPosition = center
    local w = model.clone(wheel_models[i], pivot)
    w.transform.localScale = Vector3.one * c.L
    c.wheels[i] = { pivot = pivot, center = center, rest_pos = vec(center.x, center.y + c.rest * 0.55, center.z), front = i <= 2 }
  end

  local pivots = {}
  for _, w in ipairs(c.wheels) do table.insert(pivots, w.pivot) end
  game.add_vehicle(rb, pivots)

  c.seat = new_object("seat", c.go.transform).transform
  c.seat.localPosition = vec(-c.W * 0.2, c.H * 0.42, -c.L * 0.07)
  c.hands = new_object("steeringWheel", c.go.transform).transform
  c.hands.localPosition = c.seat.localPosition + vec(0, s * 0.45, s * 0.75)
  c.cam = new_object("CarCamera", c.go.transform).transform
  c.kseat = body.seat(c.go, c.seat, "reclined", c.hands)


  c.low, c.high, c.turbo = loop(sounds.engine_low), loop(sounds.engine_high), loop(sounds.turbo)
  c.squeal, c.scraper = loop(sounds.squeal), loop(sounds.scrape)
  c.voice = audio.source(c.go, { spatial = 0.75 })
  c.horn = audio.source(c.go, { clip = sounds.horn, spatial = 0.7 })
  c.horn.loop = true

  physics.on_hit(c.go, function(col) car_hit(c, col) end)
  physics.on_touch(c.go, function(col) car_touch(c, col) end)
  return c
end

local function teleport(c, r)
  local f = facing(r)
  local pos = r:GetRootPosition() + Vector3.Cross(Vector3.up, f) * (c.W * 1.1) + Vector3.up * c.s
  local up = Vector3.up
  local hit = physics.raycast(pos + Vector3.up * 5 * c.s, Vector3.down, 30 * c.s, physics.ground)
  if hit then up = hit.normal; pos = hit.point + up * (c.rest * 0.6 + c.R * 0.5) end
  c.go.transform:SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.ProjectOnPlane(f, up), up))
  c.rb.position, c.rb.rotation = c.go.transform.position, c.go.transform.rotation
  c.rb.velocity, c.rb.angularVelocity = Vector3.zero, Vector3.zero
end

local function enter(c, r)
  if c.driver then exit_car(c) end
  body.ignore(r, c.cols, true)
  c.driver = r
  game.set_driver(r, c.rb)
  c.kseat:Sit(r, c.rb.velocity)
  once("door", 0.8)
  once("engine_start", 0.9)
  if game.is_local(r) then camera.target(c.cam) end
end

function exit_car(c)
  local r = c.driver
  if r then once("engine_stop", 0.7); once("door", 0.8) end
  c.driver = nil
  c.kseat:Stand(c.rb.velocity + Vector3.up * c.s * 6 + c.go.transform.right * c.s * 3)
  game.set_driver(nil, nil)
  if not alive(r) then return end
  if game.is_local(r) then camera.target(nil) end
  game.unground(r, true)
  after(0.8, function() if alive(r) and c.driver ~= r then body.ignore(r, c.cols, false) end end)
end

local function flip(c)
  local f = Vector3.ProjectOnPlane(c.go.transform.forward, Vector3.up)
  if f.sqrMagnitude < 0.01 then f = Vector3.forward end
  c.rb.position = c.rb.position + Vector3.up * c.s * 1.5
  c.rb.rotation = Quaternion.LookRotation(f.normalized, Vector3.up)
  c.rb.angularVelocity = Vector3.zero
  c.rb.velocity = c.rb.velocity * 0.3
end

-- crashes

local function sparks(c, point, normal, str)
  local go = new_object("CarSparks")
  go.transform:SetPositionAndRotation(point, Quaternion.LookRotation(normal))
  local s = c.s
  local ps = fx.particles(go, { duration = 0.2, lifetime = { 0.2, 0.6 }, speed = { s * 2, s * 7 }, size = { s * 0.02, s * 0.05 },
    color = { rgb(1, 0.9, 0.4), rgb(1, 0.5, 0.1) }, gravity = 0.5, angle = 70, radius = s * 0.05, material = fx_mat, stretch = 0.03, length = 2 })
  fx.emit(ps, math.floor(20 + 70 * str))
  destroy(go, 1)
end

function car_hit(c, col)
  if col.contactCount == 0 then return end
  local part = physics.part(col.collider)
  if part then
    local victim = part.ragdoll
    local rel = col.relativeVelocity.magnitude
    if not ram.value or not victim or victim == c.driver or rel < 30 then return end
    game.unground(victim, true)
    local launch = c.rb.velocity * 0.7 + Vector3.up * math.min(rel, 200) * 0.6
    for _, p in ipairs(body.parts(victim)) do
      if body.live(p) then p.rigidBody.velocity = launch + Random.insideUnitSphere * 10 end
    end
    if game.is_local(victim) then camera.shake(1, 2) end
    return
  end
  if col.collider.name:find("^Gib") then return end
  local contact = col:GetContact(0)
  local impact = math.abs(Vector3.Dot(col.relativeVelocity, contact.normal))
  local terrain = col.gameObject.layer == LayerMask.NameToLayer("Ground")
  if impact < (terrain and 110 or 45) or Time.time - c.last_hit < 0.12 then return end
  c.last_hit = Time.time
  local str = Mathf.Clamp01((impact - 45) / 220)
  local crunches = { sounds.crunch1, sounds.crunch2, sounds.crunch3 }
  audio.play(c.voice, crunches[randi(1, 4)], Mathf.Lerp(0.35, 1, str) * volume.value, rand(0.85, 1.1))
  sparks(c, contact.point, contact.normal, str)
  if c.driver and game.is_local(c.driver) then camera.shake(0.4 + str * 1.6, 2.5) end
end

function car_touch(c, col)
  if col.rigidbody or physics.part(col.collider) or col.contactCount == 0 then return end
  local tangential = Vector3.ProjectOnPlane(col.relativeVelocity, col:GetContact(0).normal).magnitude
  if tangential > 12 then c.scrape = math.max(c.scrape, Mathf.Clamp01(tangential / 120)) end
  if tangential > 40 and math.random() < 0.15 then sparks(c, col:GetContact(0).point, col:GetContact(0).normal, 0.05) end
end

-- driving

local function toggle_drive()
  local me = game.player()
  if not me then return end
  if car and car.driver == me then exit_car(car) return end
  if game.seated(me) then toast("Get up first.") return end
  if not car or not alive(car.go) then teleport(build(me), me)
  elseif Vector3.Distance(car.go.transform.position, me:GetRootPosition()) > game.scale(me) * 6 then teleport(car, me) end
  enter(car, me)
end

local function reset_car()
  local me = game.player()
  if not me then return end
  if not car or not alive(car.go) then teleport(build(me), me) return end
  local d = car.driver
  if d then exit_car(car) end
  if d ~= me then teleport(car, me) else
    local f = Vector3.ProjectOnPlane(car.go.transform.forward, Vector3.up)
    if f.sqrMagnitude < 0.01 then f = Vector3.forward end
    car.rb.position = car.rb.position + Vector3.up * car.s * 1.2
    car.rb.rotation = Quaternion.LookRotation(f.normalized, Vector3.up)
    car.rb.velocity, car.rb.angularVelocity = Vector3.zero, Vector3.zero
  end
  if d then enter(car, d) end
  toast("Car reset")
end

local function despawn()
  if net.ready() then net.send("gone", {}, true) end
  if not car then return end
  if car.driver then exit_car(car) end
  destroy(car.go)
  car = nil
end

menu.button("Drive / exit", toggle_drive)
menu.button("Reset", reset_car)
menu.button("New car", function() despawn(); local me = game.player(); if me then teleport(build(me), me) end end)
menu.button("Despawn", despawn)

-- online: other players see your car as a ghost that follows your updates

local net = net or { ready = function() return false end, send = function() end, on = function() end }
local ghosts, next_send = {}, 0

local function send_car(dt)
  if not net.ready() or not car or not alive(car.go) then return end
  if Time.time < next_send then return end
  local moving = car.rb.velocity.sqrMagnitude > 1
  next_send = Time.time + (moving and 1 / 15 or 0.5)
  local t, v = car.go.transform, car.rb.velocity
  net.send("car", { m = car.model_name, L = car.L, p = { t.position.x, t.position.y, t.position.z },
    r = { t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w }, v = { v.x, v.y, v.z } })
end

local function ghost_for(sender, d)
  local g = ghosts[sender]
  if g and alive(g.go) and g.m == d.m then return g end
  if g and alive(g.go) then destroy(g.go) end
  local def = cars[d.m]
  if not def then return nil end
  local m = model.load(def.file)
  g = { m = d.m, go = new_object("GhostCar") }
  local body = model.clone(m.Body, g.go.transform)
  body.transform.localScale = Vector3.one * d.L
  local centers, wheels = list(m.WheelCenters), list(m.Wheels)
  for i = 1, #centers do
    local pivot = new_object("wheel", g.go.transform).transform
    pivot.localPosition = centers[i] * d.L
    model.clone(wheels[i], pivot).transform.localScale = Vector3.one * d.L
  end
  ghosts[sender] = g
  return g
end

local function on_car(sender, d)
  local g = ghost_for(sender, d)
  if not g then return end
  local p = vec(d.p[1], d.p[2], d.p[3])
  g.target, g.rot, g.vel, g.seen = p, Quaternion.__new(d.r[1], d.r[2], d.r[3], d.r[4]), vec(d.v[1], d.v[2], d.v[3]), Time.time
  if not g.placed then g.go.transform:SetPositionAndRotation(p, g.rot) g.placed = true end
end
net.on("car", on_car)

net.on("gone", function(sender)
  local g = ghosts[sender]
  if g and alive(g.go) then destroy(g.go) end
  ghosts[sender] = nil
end)

local function move_ghosts(dt)
  for sender, g in pairs(ghosts) do
    if not alive(g.go) or Time.time - g.seen > 3 then
      if alive(g.go) then destroy(g.go) end
      ghosts[sender] = nil
    else
      local predicted = g.target + g.vel * (Time.time - g.seen)
      local t = g.go.transform
      local k = 1 - math.exp(-dt * 12)
      t:SetPositionAndRotation(Vector3.Lerp(t.position, predicted, k), Quaternion.Slerp(t.rotation, g.rot, k))
    end
  end
end

function on_round_start()
  car = nil; game.set_driver(nil, nil)
  for _, g in pairs(ghosts) do if alive(g.go) then destroy(g.go) end end
  ghosts = {}
end
function on_unload() despawn() end

local test = nil
function test_drive(throttle, steer, nitro, brake) test = throttle and { throttle, steer or 0, nitro, brake } or nil end

function update(dt)
  send_car(dt)
  move_ghosts(dt)
  if enter_key.down then toggle_drive() end
  if reset_key.down then reset_car() end
  local c = car
  if not c or not alive(c.go) then return end
  if c.driver and game.is_local(c.driver) and flip_key.down then flip(c) end

  local spd = math.abs(Vector3.Dot(c.rb.velocity, c.go.transform.forward))
  local x = Mathf.Clamp01(spd / (top_speed.value * 1.25)) * 6
  local gear = math.min(5, math.floor(x))
  local rpm = spd < 4 and 0.12 + 0.3 * c.throttle or Mathf.Lerp(0.35 + gear * 0.04, 1, x - gear)
  local shifted = gear > c.last_gear and c.throttle > 0.5
  c.last_gear = gear
  local vv = v()
  if c.driver then
    local load = Mathf.Lerp(0.55, 1, c.throttle)
    c.low.pitch, c.high.pitch = Mathf.Lerp(0.7, 1.25, rpm), Mathf.Lerp(0.38, 0.8, rpm)
    c.low.volume = Mathf.Clamp01(1 - (rpm - 0.15) / 0.35) * load * vv
    c.high.volume = Mathf.Clamp01((rpm - 0.2) / 0.35) * load * vv * 0.9
    c.turbo.pitch, c.turbo.volume = Mathf.Lerp(0.6, 1.5, rpm), c.throttle * rpm * 0.05 * vv
    if (c.last_throttle or 0) > 0.6 and c.throttle < 0.2 and rpm > 0.45 then once("blowoff", 0.5) end
    if c.throttle < 0.1 and rpm > 0.5 and Time.time > (c.next_pop or 0) then
      c.next_pop = Time.time + rand(0.08, 0.5)
      if math.random() < 0.45 then audio.play(c.voice, sounds.pop, 0.6 * volume.value, rand(0.8, 1.3)) end
    end
    if shifted then once("pop", 0.35) end
  else
    c.low.volume, c.high.volume, c.turbo.volume = 0, 0, 0
  end
  c.last_throttle = c.throttle
  c.squeal.volume = Mathf.Lerp(c.squeal.volume, c.slip * 0.55 * vv, dt * 12)
  c.squeal.pitch = 0.8 + c.slip * 0.15
  c.scraper.volume = Mathf.Lerp(c.scraper.volume, c.scrape * 0.6 * vv, dt * 15)
  c.scrape = Mathf.MoveTowards(c.scrape, 0, dt * 4)

  local honk = c.driver and game.is_local(c.driver) and input.key("h")
  c.horn.volume = vv
  if honk and not c.horn.isPlaying then c.horn:Play() elseif not honk and c.horn.isPlaying then c.horn:Stop() end
end

function late_update(dt)
  local c = car
  if not c or not alive(c.go) then return end
  local want = c.go.transform.position + Vector3.up * 2.6 * c.s
  if (c.cam.position - want).sqrMagnitude > c.s * c.s * 25 then c.cam.position = want
  else c.cam.position = Vector3.Lerp(c.cam.position, want, math.min(1, dt * 25)) end
end

function fixed_update(dt)
  local c = car
  if not c or not alive(c.go) then return end
  local rb, tr = c.rb, c.go.transform
  if c.driver and not alive(c.driver) then c.driver = nil; game.set_driver(nil, nil) end

  local throttle, steer, brake, nitro = 0, 0, false, false
  if c.driver and not game.counting_down() and input.allowed() then
    if input.key("w") or input.key("upArrow") then throttle = throttle + 1 end
    if input.key("s") or input.key("downArrow") then throttle = throttle - 1 end
    if input.key("d") or input.key("rightArrow") then steer = steer + 1 end
    if input.key("a") or input.key("leftArrow") then steer = steer - 1 end
    brake = input.key("space")
    nitro = input.key("leftShift") or input.key("rightShift")
  end
  if c.driver and test then throttle, steer, nitro, brake = test[1], test[2], test[3], test[4] end
  c.steer = Mathf.MoveTowards(c.steer, steer, dt * 5)
  c.throttle = Mathf.MoveTowards(c.throttle, math.abs(throttle), dt * 4)

  local g = math.abs(Physics.gravity.y)
  local M = rb.mass
  local up, fwd = tr.up, tr.forward
  local speed_fwd = Vector3.Dot(rb.velocity, fwd)
  local angle = c.steer * 34 * steering.value / (1 + math.abs(speed_fwd) / 140)
  local grounded, normal_sum, max_slip = 0, Vector3.zero, 0
  local demand = Mathf.Clamp01(math.abs(throttle) * (nitro and 0.7 or 0.4) + (brake and 0.6 or 0))

  for i, w in ipairs(c.wheels) do
    local origin = tr:TransformPoint(w.rest_pos)
    local hit = physics.raycast(origin, -up, c.rest + c.R, physics.ground, rb, true)
    local len = hit and Mathf.Clamp(hit.distance - c.R, 0, c.rest) or c.rest
    local lift = Mathf.Clamp(c.rest * 0.55 - len, -c.rest * 0.45, c.R * 0.12)
    w.pivot.localPosition = w.center + Vector3.up * lift
    if hit then
      grounded = grounded + 1
      normal_sum = normal_sum + hit.normal
      local comp = c.rest - len
      local load = math.max(0, c.k * comp + c.damp * (comp - c.comp[i]) / dt)
      c.comp[i] = comp
      rb:AddForceAtPosition(up * load, origin)
      local wheel_fwd = Quaternion.AngleAxis(w.front and angle or 0, up) * fwd
      local wheel_right = Vector3.Cross(up, wheel_fwd).normalized
      local pv = rb:GetPointVelocity(hit.point)
      local slip = Vector3.Dot(pv, wheel_right)
      max_slip = math.max(max_slip, math.abs(slip))
      local limit = 1.3 * grip.value * math.max(load, M * g * 0.15) * math.sqrt(1 - demand * demand * 0.6)
      if brake and not w.front and c.driver then limit = limit * 0.35 end
      rb:AddForceAtPosition(wheel_right * Mathf.Clamp(-slip * (M / 4) / dt, -limit, limit), hit.point)
      c.spin[i] = c.spin[i] + Vector3.Dot(pv, wheel_fwd) / c.R * Mathf.Rad2Deg * dt
    else
      c.comp[i] = 0
    end
    w.pivot.localRotation = euler(0, w.front and angle or 0, 0) * euler(c.spin[i], 0, 0)
  end

  local long_slip = (brake or throttle < 0) and math.abs(speed_fwd) > 30 and 0.5 or (throttle > 0.9 and math.abs(speed_fwd) < 25 and grounded > 0 and 0.4 or 0)
  c.slip = grounded > 0 and Mathf.Clamp01(math.max((max_slip - 12) / 45, long_slip)) or 0

  local anti_roll = c.k * 0.5
  for axle = 0, 1 do
    local a, b = axle * 2 + 1, axle * 2 + 2
    local diff = c.comp[a] - c.comp[b]
    if c.comp[a] > 0 then rb:AddForceAtPosition(-up * diff * anti_roll, tr:TransformPoint(c.wheels[a].rest_pos)) end
    if c.comp[b] > 0 then rb:AddForceAtPosition(up * diff * anti_roll, tr:TransformPoint(c.wheels[b].rest_pos)) end
  end

  local top0 = math.max(30, top_speed.value)
  if grounded > 0 then
    rb:AddForce(-up * (0.5 * M * g * math.min(speed_fwd * speed_fwd / (top0 * top0), 1.5)))
    if math.abs(steer) < 0.05 then rb:AddTorque(-up * Vector3.Dot(rb.angularVelocity, up) * M * 0.6 * c.s * c.s * 0.05) end
  end
  c.upside = (up.y < 0.25 and rb.velocity.magnitude < 15) and c.upside + dt or 0
  if c.upside > 2.5 then c.upside = 0; flip(c) end

  if grounded > 0 then
    local gn = normal_sum.normalized
    local f = Vector3.ProjectOnPlane(fwd, gn).normalized
    local share = grounded / 4
    local frac = Mathf.Clamp01(math.abs(speed_fwd) / top0)
    local accel = 0.9 * g * power.value * (nitro and nitro_power.value or 1) * Mathf.Lerp(1.35, 0.65, frac)
    local top = top_speed.value * (nitro and 1.5 or 1)
    if throttle > 0 and speed_fwd < top then rb:AddForce(f * accel * throttle * M * share)
    elseif throttle < 0 then
      if speed_fwd > 5 then rb:AddForce(-f * 1.4 * g * M * share)
      elseif speed_fwd > -top * 0.35 then rb:AddForce(f * accel * 0.6 * throttle * M * share) end
    end
    if not c.driver then brake = true end
    if brake then
      local sign = speed_fwd >= 0 and 1 or -1
      rb:AddForce(-f * sign * math.min((c.driver and 1.1 or 3) * g, math.abs(speed_fwd) / dt) * M * share)
    elseif throttle == 0 then
      rb:AddForce(-f * speed_fwd * 0.15 * M * share)
    end
  elseif c.driver then
    rb:AddRelativeTorque(vec(throttle * 2.5, steer * 2, -steer * 1.5), ForceMode.Acceleration)
  end
end

function draw()
  local c = car
  if not c or not c.driver or not game.is_local(c.driver) then return end
  ui.hud(string.format("%d km/h", math.floor(c.rb.velocity.magnitude * 0.36)),
    string.format("WASD drive · Space brake · Shift nitro · H horn · %s flip · %s reset · %s exit", flip_key.label, reset_key.label, enter_key.label))
end

