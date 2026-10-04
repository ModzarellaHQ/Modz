local S = "Euphoria"
local strength = setting.number{ section = S, name = "Muscle strength", default = 1, min = 0.2, max = 3, desc = "How hard bodies fight to stay up and protect themselves." }
local blood_amount = setting.number{ section = S, name = "Blood", default = 3, min = 0, max = 8, desc = "How much blood everything sprays. 0 turns blood off." }
local dismember = setting.toggle{ section = S, name = "Limbs come off", default = true, desc = "Hard hits tear limbs off." }
local deaths = setting.toggle{ section = S, name = "Deaths", default = true, desc = "Bodies can bleed out or be killed. When you die, Enter starts the next round." }
local on_you = setting.toggle{ section = S, name = "Affects you", default = true, desc = "Your own body reacts and gets hurt too." }
local screen_blood = setting.toggle{ section = S, name = "Blood on screen", default = true, desc = "Blood splashes on the screen when it happens right next to the camera." }
local volume = setting.number{ section = S, name = "Volume", default = 0.8, min = 0, max = 1, desc = "Squelches, cracks and splats." }
local toughness = setting.number{ section = S, name = "Toughness", default = 1, min = 0.3, max = 3, desc = "Higher means bodies take harder hits before bleeding or losing limbs.", advanced = true }

local BLEED_OUT, STUMP_SECONDS, GIB_LIFE = 30, 25, 45
local function bleed_impact() return 75 * toughness.value end
local function sever_impact() return 150 * toughness.value end

local euphoria = require("reactions")
euphoria.strength, euphoria.on_you = strength, on_you
local sounds = audio.folder("sounds")

local blood_mat = mat.unlit(mat.blob(32, 0, 5))
local decal_tex = mat.blob(128, 0.9, 11)
local tints = {}
for i = 0, 3 do tints[i + 1] = mat.unlit(decal_tex, Color.Lerp(rgb(0.45, 0, 0.02, 0.92), rgb(0.25, 0, 0.01, 0.95), i / 3)) end
local track_mats = {}
for i = 0, 3 do track_mats[i + 1] = mat.unlit(decal_tex, rgb(0.4, 0, 0.02, 0.25 + 0.22 * i)) end
local splat_tex = { mat.blob(128, 0.45, 40), mat.blob(128, 0.45, 41), mat.blob(128, 0.45, 42) }
local vignette = mat.vignette()

local states, bleeders, splats, severs, explodes = {}, {}, {}, {}, {}
local heart, next_screen_blood = nil, 0

local function vol(v) return v * volume.value end
local function sound(name, at, v) audio.at(sounds[name], at, vol(v), rand(0.85, 1.15)) end

-- blood effects

local function blood_system(go, scale, continuous)
  return fx.particles(go, {
    loop = continuous, duration = continuous and 1 or 0.2, lifetime = { 0.6, 1.4 }, speed = { scale, scale * 3 },
    size = { scale * 0.09, scale * 0.26 }, color = { rgb(0.55, 0, 0.02), rgb(0.3, 0, 0.01) }, gravity = 0.35,
    max = continuous and 600 or 500, angle = continuous and 18 or 60, radius = scale * 0.03, collide = not continuous,
    grow = { 1, 0.4 }, material = blood_mat, stretch = 0.025, length = 1.2,
  })
end

local function screen_splat(pos, scale, amount)
  if not screen_blood.value or Time.time < next_screen_blood then return end
  local cam = camera.main()
  local dist = Vector3.Distance(cam.transform.position, pos)
  local reach = scale * 1.8 * math.sqrt(amount)
  if dist > reach then return end
  local p = camera.to_screen(pos)
  if not p then return end
  next_screen_blood = Time.time + 0.4
  local n = math.min(3, math.floor(amount * (1 - dist / reach)) + 1)
  for _ = 1, n do
    if #splats >= 12 then break end
    local size = ui.height() * rand(0.05, 0.16) * (1 - dist / reach * 0.6)
    table.insert(splats, {
      x = p.x + rand(-0.3, 0.3) * ui.width() - size / 2, y = p.y + rand(-0.3, 0.3) * ui.height() - size / 2,
      w = size, h = size * rand(0.7, 1.2), born = Time.time, life = rand(2.5, 4.5), rot = rand(0, 360), tex = splat_tex[randi(1, 4)],
    })
  end
end

local function ground_decal(from, size)
  local hit = physics.raycast(from + Vector3.up * size, Vector3.down, size * 30, physics.ground)
  if not hit or hit.rigidbody then return end
  fx.decal("Blood", 400, hit.point, hit.normal, Vector3.zero, size, size * rand(0.7, 1.3), tints[randi(1, 5)])
end

local function burst(pos, dir, scale, amount)
  screen_splat(pos, scale, amount)
  if blood_amount.value <= 0 then return end
  local go = new_object("BloodBurst")
  go.transform.position = pos
  if dir.sqrMagnitude > 1e-4 then go.transform.rotation = Quaternion.LookRotation(dir + Vector3.up * 0.5) end
  local ps = blood_system(go, scale, false)
  fx.speed(ps, scale * 2, scale * 7 * math.sqrt(amount))
  fx.emit(ps, math.min(450, math.floor(40 * amount * blood_amount.value)))
  if amount >= 2 then
    for _ = 1, math.floor(6 * amount) do
      fx.emit_one(ps, pos + Random.insideUnitSphere * scale * 0.4, Random.insideUnitSphere * scale * 0.8 + Vector3.up * scale * 0.3, scale * 0.6, 1.2, rgb(0.5, 0.02, 0.02, 0.35))
    end
  end
  destroy(go, 2.5)
  for _ = 1, math.ceil(amount * 3) do ground_decal(pos + Random.insideUnitSphere * scale * 0.6, scale * rand(0.5, 1.1) * math.sqrt(amount)) end
  local d0 = dir.sqrMagnitude > 1e-4 and dir.normalized or Vector3.up
  for _ = 1, math.min(14, 3 + math.floor(amount * 2.5 * math.sqrt(blood_amount.value))) do
    local rd = (d0 + Random.insideUnitSphere * 1.1).normalized
    local h = physics.raycast(pos, rd, scale * (2 + amount), physics.ground + 1)
    if h and not h.rigidbody and not physics.part(h.collider) then
      local size = scale * rand(0.25, 0.6) * math.sqrt(amount)
      local stretch = 1 + Mathf.Clamp01(1 - math.abs(Vector3.Dot(rd, h.normal))) * 1.5
      fx.decal("Blood", 400, h.point, h.normal, rd, size, size * stretch, tints[randi(1, 5)])
    end
  end
end

local function splatter_car(car, point, scale)
  local out = point - car.worldCenterOfMass
  out = out.sqrMagnitude > 1e-4 and out.normalized or car.transform.forward
  for _, col in ipairs(components(car, "Collider")) do
    local ok, h = col:Raycast(Ray.__new(point + out * scale * 3, -out), scale * 6)
    if ok then
      for _ = 1, 2 do
        local jitter = Vector3.ProjectOnPlane(Random.insideUnitSphere, h.normal) * scale * 0.25
        local size = scale * rand(0.2, 0.5)
        fx.decal("CarBlood", 70, h.point + jitter, h.normal, Vector3.zero, size, size * rand(0.7, 1.3), tints[randi(1, 5)], car.transform)
      end
      return
    end
  end
end

-- bleeding

local function attach_bleeder(parent, at, scale, stump, owner, rb)
  local go = new_object(stump and "BloodStump" or "Wound", parent)
  go.transform.position = at
  local b = { go = go, rb = rb, ps = blood_system(go, scale, true), until_t = 0, intensity = 0, scale = scale, stump = stump, owner = owner, next_decal = 0, pool_size = 0 }
  table.insert(bleeders, b)
  if owner then body.mark_wound(owner.r, at) end
  return b
end

local function refresh(b, seconds, intensity)
  b.until_t = math.max(b.until_t, Time.time + seconds)
  b.intensity = math.max(b.intensity, intensity)
end

-- per-ragdoll state

local function lose_blood(g, amount)
  if g.dead then return end
  g.lost = g.lost + amount
  if g.lost >= BLEED_OUT then g.die("bled out") end
end

local function state(r)
  local id = r:GetInstanceID()
  local g = states[id]
  if g then return g end
  g = { r = r, damage = {}, severed = {}, bleeders = {}, lost = 0, dead = false, reason = "", wounds = {}, stain = 0, last_sever = 0 }
  states[id] = g

  function g.die(why)
    if g.dead or not alive(r) then return end
    if not deaths.value then return end
    g.reason = why
    if game.seated(r) then return end
    g.dead = true
    body.set_dead(r, true)
    sound("crack", r:GetRootPosition(), 0.6)
    if game.is_local(r) then toast("You died (" .. why .. ")") end
  end

  function g.stain_body(add)
    if not g.mats then
      g.mats = {}
      for _, rend in ipairs(children(r, "SkinnedMeshRenderer")) do
        if rend.enabled then
          for _, m in ipairs(list(rend.materials)) do
            if m:HasProperty("_Color") then table.insert(g.mats, { m = m, base = m.color }) end
          end
        end
      end
    end
    g.stain = Mathf.Clamp01(g.stain + add)
    for _, e in ipairs(g.mats) do
      e.m.color = Color.Lerp(e.base, e.base * rgb(0.72, 0.03, 0.03), g.stain * 0.85)
    end
  end

  function g.wound(part, point, dir)
    local sc = game.scale(r)
    local out = point - part.transform.position
    out = out.sqrMagnitude > 1e-4 and out.normalized or -dir.normalized
    local size = sc * rand(0.12, 0.22)
    local q = fx.decal("BodyWound", 60, point, out, Vector3.zero, size, size * rand(0.7, 1.2), tints[4], part.transform)
    q.name = "Wound"
    body.mark_wound(r, point)
  end

  function g.bleed(part, seconds, intensity)
    local id = part:GetInstanceID()
    local b = g.bleeders[id]
    if not b or not alive(b.go) then
      b = attach_bleeder(part.transform, part.transform.position, game.scale(r), false, g, part.rigidBody)
      g.bleeders[id] = b
    end
    refresh(b, seconds, intensity)
  end

  return g
end

local function severable(r, p)
  return dismember.value and p ~= r.spine1 and p ~= r.spine2
end

local function hit(g, part, impact, point, rel)
  local r = g.r
  if g.severed[part:GetInstanceID()] then return end
  local bleed, sever = bleed_impact(), math.max(sever_impact(), bleed_impact() + 1)
  local dmg = (impact - bleed) / (sever - bleed) * 0.8
  local id = part:GetInstanceID()
  local total = (g.damage[id] or 0) + dmg
  g.damage[id] = total

  if dismember.value and impact >= sever * 3 then table.insert(explodes, { g = g, at = point, vel = rel }) return end
  burst(point, -rel.normalized, game.scale(r), Mathf.Clamp(0.5 + dmg * 1.5, 0.5, 3))
  if dmg > 0.3 then g.wound(part, point, rel) end
  lose_blood(g, Mathf.Clamp(dmg, 0, 2) * BLEED_OUT * 0.04)
  g.stain_body(0.04 + dmg * 0.12)
  sound(dmg > 0.5 and "crack" or "splat", point, Mathf.Clamp(0.3 + dmg, 0.3, 1))
  g.bleed(part, Mathf.Clamp(2 + dmg * 4, 2, 7), Mathf.Clamp01(0.3 + dmg))

  if Time.time - g.last_sever < 0.12 then return end
  if part == r.spine2 and dismember.value and impact >= sever * 2.2 then
    g.last_sever = Time.time
    table.insert(severs, { g = g, part = part, vel = rel })
  elseif severable(r, part) and (impact >= sever or total >= 1) then
    g.last_sever = Time.time
    table.insert(severs, { g = g, part = part, vel = rel })
    if impact >= sever * 1.6 then
      if part == r.head then g.head_burst = true end
      local pool = {}
      for _, p in ipairs(body.parts(r)) do
        if p ~= part and p ~= r.head and not g.severed[p:GetInstanceID()] and severable(r, p) then table.insert(pool, p) end
      end
      if #pool > 0 then table.insert(severs, { g = g, part = pool[randi(1, #pool + 1)], vel = rel }) end
    end
  end
end

local function squish(gib, scale, big)
  local born = Time.time
  physics.on_hit(gib, function(c)
    if Time.time - born < 0.3 or not game.is_vehicle(c.rigidbody) or c.relativeVelocity.magnitude < 35 then return end
    local p = c.contactCount > 0 and c:GetContact(0).point or gib.transform.position
    burst(p, Vector3.up, scale, big and 3 or 1.2)
    splatter_car(c.rigidbody, p, scale)
    events.emit("wheels_bloody", c.rigidbody, p)
    destroy(gib)
  end)
end

local function do_sever(g, part, rel)
  local r = g.r
  local parent = body.parent(part)
  if not parent or g.severed[part:GetInstanceID()] then return end
  local scale = game.scale(r)
  local sub = body.below(part)
  local vel = part.rigidBody and part.rigidBody.velocity or Vector3.zero
  local spin = part.rigidBody and part.rigidBody.angularVelocity or Vector3.zero
  local at = part.transform.position
  if r.handLeft then r.handLeft:BreakHold() end
  if r.handRight then r.handRight:BreakHold() end

  local head_burst = g.head_burst and part == r.head
  g.head_burst = false
  if head_burst then burst(at, Vector3.up, scale, 5) else
    local gib = body.gib(r, part, sub)
    if gib then
      local rb = get(gib, "Rigidbody")
      rb.velocity = vel + (Random.onUnitSphere + Vector3.up).normalized * scale * 2.5
      rb.angularVelocity = spin + Random.insideUnitSphere * 12
      squish(gib, scale, true)
      refresh(attach_bleeder(gib.transform, at, scale * 0.7, true, nil, rb), STUMP_SECONDS * 0.5, 0.5)
      destroy(gib, GIB_LIFE)
    end
  end

  for _, p in ipairs(sub) do
    g.severed[p:GetInstanceID()] = true
    if p.joint then destroy_now(p.joint) end
    for _, c in ipairs(components(p, "Collider")) do c.enabled = false end
    if p.rigidBody and not p.rigidBody.isKinematic then
      p.rigidBody.velocity = Vector3.zero
      p.rigidBody.isKinematic = true
      p.rigidBody.detectCollisions = false
    end
    p.touchingGround = false
    local b = g.bleeders[p:GetInstanceID()]
    if b then destroy(b.go) end
  end
  part.transform.localScale = Vector3.one * 0.001

  sound("squelch", at, 1)
  sound("crack", at, 0.8)
  g.stain_body(0.2)
  lose_blood(g, BLEED_OUT * (part == r.head and 1 or 0.18))
  if part == r.head then g.die("decapitated") end
  if part == r.spine2 then g.die("torn in half") end
  refresh(attach_bleeder(parent.transform, at, scale, true, g, parent.rigidBody), STUMP_SECONDS, 1)
  burst(at, Random.onUnitSphere, scale, 3.5)
  log((game.is_local(r) and "You" or r.name) .. " lost " .. body.name(r, part))
  if game.is_local(r) then camera.shake(1.2, 2) end
end

local function explode(g, at, vel)
  if g.exploded or not alive(g.r) then return end
  g.exploded = true
  local r, sc = g.r, game.scale(g.r)
  burst(at, Vector3.up, sc, 6)
  burst(at, vel.normalized, sc, 4)
  sound("squelch", at, 1)
  sound("crack", at, 1)
  g.head_burst = true
  for _, p in ipairs({ r.head, r.upperArmLeft, r.upperArmRight, r.upperLegLeft, r.upperLegRight, r.spine2 }) do
    table.insert(severs, { g = g, part = p, vel = vel + Random.onUnitSphere * sc * 4 })
  end
  g.die("blown to pieces")
end

local function allowed(r)
  return not game.is_local(r) or on_you.value
end

-- events

events.on("bullet_hit", function(part, point, dir, power)
  local r = part.ragdoll
  if not allowed(r) then return end
  local g = state(r)
  if g.severed[part:GetInstanceID()] then return end
  local sc = game.scale(r)
  burst(point, -dir, sc, 0.1)
  burst(point + dir * sc * 0.15, dir, sc, 0.25 + power / 1200)
  g.wound(part, point, dir)
  g.bleed(part, 30, 0.9)
  lose_blood(g, BLEED_OUT * (part == r.head and 0.5 or (part == r.spine1 or part == r.spine2) and 0.14 or 0.06))
  sound("splat", point, 0.8)
  if part == r.head and power > 120 and dismember.value then
    g.head_burst = true
    table.insert(severs, { g = g, part = part, vel = dir * power })
    return
  end
  hit(g, part, power * 0.9, point, dir * power)
end)

local function force_hit(part, impact, point, dir)
  local r = part.ragdoll
  if not allowed(r) or impact < bleed_impact() then return end
  hit(state(r), part, impact, point, dir)
end

function on_part_hit(part, c)
  local r = part.ragdoll
  if not r or not r.active or not allowed(r) or c.contactCount == 0 then return end
  local car = game.is_vehicle(c.rigidbody)
  if car and game.seated(r) then return end
  local layer = c.gameObject.layer
  if not car and layer ~= LayerMask.NameToLayer("Ground") and layer ~= LayerMask.NameToLayer("Obstacle") then return end
  if game.counting_down() or game.round_age() < 1.5 then return end
  local contact = c:GetContact(0)
  local impact = math.abs(Vector3.Dot(c.relativeVelocity, contact.normal))
  local extremity = part == r.footLeft or part == r.footRight or part == r.handLeft or part == r.handRight or part == r.lowerLegLeft or part == r.lowerLegRight
  if extremity and not car then impact = impact / 1.6 end
  if impact < bleed_impact() then return end
  if car then
    impact = impact * 1.4
    splatter_car(c.rigidbody, contact.point, game.scale(r))
  end
  hit(state(r), part, impact, contact.point, c.relativeVelocity)
end

-- vehicles

local wet, last_track, crushed = {}, {}, {}

events.on("wheels_bloody", function(rb, near)
  for _, v in ipairs(game.vehicles()) do
    for _, w in ipairs(v.wheels) do
      if Vector3.Distance(w.position, near) < game.scale(nil) * 2.5 then wet[w:GetInstanceID()] = 1 end
    end
  end
end)

local function tyres()
  local scale = game.scale(nil)
  local driver = game.driver()
  for _, v in ipairs(game.vehicles()) do
    local rb = v.body
    if not alive(rb) then break end
    local speed = rb.velocity.magnitude
    for _, w in ipairs(v.wheels) do
      local id = w:GetInstanceID()
      local ground = physics.raycast(w.position, -rb.transform.up, scale * 2, physics.ground)
      if ground then
        if speed > 20 then
          for _, col in ipairs(physics.overlap(ground.point + ground.normal * scale * 0.25, scale * 0.35)) do
            local part = physics.part(col)
            if part and part.ragdoll and part.ragdoll ~= driver then
              local pid = part:GetInstanceID()
              if not crushed[pid] or Time.time > crushed[pid] then
                crushed[pid] = Time.time + 0.35
                force_hit(part, speed * 1.6 + 60, ground.point, rb.velocity)
                if body.live(part) then part.rigidBody:AddForce(rb.velocity * 0.3 + ground.normal * speed * 0.2, ForceMode.VelocityChange) end
                wet[id] = 1
              end
            end
          end
        end
        local step = scale * 0.7
        local wetness = wet[id] or 0
        if wetness > 0.02 and speed > 6 then
          if not last_track[id] or (ground.point - last_track[id]).sqrMagnitude > step * step then
            fx.decal("BloodTrack", 200, ground.point, ground.normal, rb.velocity, scale * 0.32, step * 1.15, track_mats[Mathf.Clamp(math.floor(wetness * 4), 0, 3) + 1])
            last_track[id] = ground.point
            wet[id] = wetness - 0.035
          end
        else
          last_track[id] = ground.point
        end
      end
    end
  end
end

-- callbacks

function on_round_start()
  states, bleeders, splats, severs, explodes, crushed, wet, last_track = {}, {}, {}, {}, {}, {}, {}, {}
  heart = nil
  euphoria.reset()
end

function fixed_update(dt)
  local ex = explodes
  explodes = {}
  for _, e in ipairs(ex) do explode(e.g, e.at, e.vel) end
  local batch = severs
  severs = {}
  for _, s in ipairs(batch) do if alive(s.part) then do_sever(s.g, s.part, s.vel) end end
  tyres()
  euphoria.fixed_update(dt)
end

local function grow_pool(b, fade)
  if not b.stump and b.intensity < 0.5 then return end
  if alive(b.rb) and b.rb.velocity.sqrMagnitude > 16 then b.pool = nil return end
  if not alive(b.pool) then
    local hit = physics.raycast(b.go.transform.position + Vector3.up * b.scale * 0.5, Vector3.down, b.scale * 4, physics.ground)
    if not hit or hit.rigidbody then return end
    b.pool_size = b.scale * 0.2
    b.pool = fx.decal("BloodPool", 40, hit.point, hit.normal, Vector3.zero, b.pool_size, b.pool_size, tints[4])
    destroy(b.pool, 60)
  end
  b.pool_size = math.min(b.scale * (b.stump and 2 or 1), b.pool_size + b.scale * 0.3 * fade * Time.deltaTime)
  b.pool.transform.localScale = vec(b.pool_size, b.pool_size * 0.85, 1)
end

function update(dt)
  for i = #bleeders, 1, -1 do
    local b = bleeders[i]
    if not alive(b.go) then table.remove(bleeders, i) else
      local left = b.until_t - Time.time
      if left <= 0 then
        fx.rate(b.ps, 0)
        if b.ps.particleCount == 0 then destroy(b.go); table.remove(bleeders, i) end
      else
        local fade = Mathf.Clamp01(left / 2)
        if b.owner then lose_blood(b.owner, (b.stump and 1 or 0.25) * b.intensity * fade * dt) end
        grow_pool(b, fade)
        local beat = b.stump and math.max(0, math.sin(Time.time * 7.5)) ^ 4 or 0.5
        fx.rate(b.ps, (b.stump and 170 * beat + 18 or 30) * b.intensity * fade * blood_amount.value)
        local speed = b.stump and b.scale * Mathf.Lerp(2, 7.5, beat) or b.scale
        fx.speed(b.ps, speed * 0.6, speed)
        local parent = b.go.transform.parent
        if b.stump and parent then
          local out = b.go.transform.position - parent.position
          if out.sqrMagnitude > 1e-4 then b.go.transform.rotation = Quaternion.LookRotation(out) end
        end
        if Time.time > b.next_decal then
          b.next_decal = Time.time + (b.stump and 0.3 or 0.7) / math.max(0.2, b.intensity)
          ground_decal(b.go.transform.position, b.scale * rand(0.25, b.stump and 0.7 or 0.45))
        end
      end
    end
  end

  local me = game.player()
  local g = me and states[me:GetInstanceID()]
  if not alive(heart) then heart = audio.source(new_object("Heartbeat"), { clip = sounds.heartbeat, loop = true, spatial = 0, volume = 0 }) end
  local lost = g and Mathf.Clamp01(g.lost / BLEED_OUT) or 0
  heart.volume = (g and not g.dead and lost > 0.35) and Mathf.InverseLerp(0.35, 1, lost) * volume.value * audio.sfx() * 1.5 or 0
  heart.pitch = 0.9 + lost * 0.7
  if g and g.dead and input.key_down("enter") then game.next_round() end

  for _, s in pairs(states) do
    if alive(s.r) and not s.dead and not game.is_local(s.r) then
      local f = s.lost / BLEED_OUT
      if f > 0.5 and math.random() < dt * f * 0.8 then game.unground(s.r, true) end
    end
  end
end

local reasons = { decapitated = "You lost your head", ["torn in half"] = "You were torn in half", ["blown to pieces"] = "You were blown to pieces" }

function draw()
  for i = #splats, 1, -1 do
    local s = splats[i]
    local t = (Time.time - s.born) / s.life
    if t >= 1 then table.remove(splats, i) else
      ui.texture(s.tex, s.x, s.y + t * s.h * 0.4, s.w, s.h, rgb(0.45, 0, 0.01, 0.6 * (1 - t * t)), s.rot)
    end
  end
  local me = game.player()
  local g = me and states[me:GetInstanceID()]
  if not g then return end
  local lost = Mathf.Clamp01(g.lost / BLEED_OUT)
  local w, h = ui.width(), ui.height()
  if lost > 0.05 or g.dead then
    local pulse = lost > 0.5 and 0.1 * math.max(0, math.sin(Time.time * (4 + lost * 6))) or 0
    ui.texture(vignette, 0, 0, w, h, rgb(0.5, 0, 0, Mathf.Clamp01(lost * 0.55 + pulse + (g.dead and 0.5 or 0))))
    if not g.dead then
      ui.text(lost > 0.6 and "Bleeding out" or "Blood", 24, h - 64, 240, 20, { size = 13, bold = true, color = rgb(1, 0.85, 0.8) })
      ui.bar(24, h - 42, 240, 12, 1 - lost, Color.Lerp(rgb(0.9, 0.1, 0.1), rgb(0.35, 0, 0), lost))
    end
  end
  if g.dead then
    ui.text(reasons[g.reason] or "You bled out", 0, h * 0.36, w, 70, { size = 52, bold = true, align = "center", color = rgb(0.92, 0.12, 0.1) })
    ui.text("Press Enter for the next round", 0, h * 0.36 + 72, w, 30, { size = 18, align = "center" })
  end
end

function test_sever(bot_index, limb)
  local r = game.ragdolls()[bot_index]
  for _, p in ipairs(body.parts(r)) do
    if body.name(r, p) == limb then table.insert(severs, { g = state(r), part = p, vel = Vector3.up * 50 }) end
  end
end
