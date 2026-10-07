local M = {}

local THROES = 2.5

local brains = {}
local hold, step = 0, 0

local function align(a, b, dir, k, d) body.align(a, b, dir, k, d, hold) end
local function reach(hand, lower, upper, target, k, d)
  if body.hands_busy(upper.ragdoll) then return end
  body.reach(hand, lower, upper, target, k, d, hold)
end
local vehicles = {}

local function brain(r)
  local id = r:GetInstanceID()
  local b = brains[id]
  if not b then
    b = { off_feet = 0, death_time = -1, seed = math.random() * 100 }
    brains[id] = b
  end
  return b
end

local function legs_to(r, dir, k, d)
  align(r.upperLegLeft, r.lowerLegLeft, dir, k, d)
  align(r.upperLegRight, r.lowerLegRight, dir, k, d)
end

local function curl(r, k, d)
  local fwd = r.spine2.transform.forward
  legs_to(r, fwd + Vector3.up * 0.2, k, d)
  align(r.lowerLegLeft, r.footLeft, -fwd, k * 0.6, d)
  align(r.lowerLegRight, r.footRight, -fwd, k * 0.6, d)
  align(r.spine2, r.head, r.spine2.transform.up + fwd * 0.6, k * 0.5, d)
end

local function pedal(r, t, k, d)
  local fwd = Vector3.ProjectOnPlane(r.spine2.transform.forward, Vector3.up).normalized
  local side = Vector3.Cross(Vector3.up, fwd)
  local swing = fwd * (math.sin(t) * 0.8)
  align(r.upperLegLeft, r.lowerLegLeft, Vector3.down + swing - side * 0.1, k, d)
  align(r.upperLegRight, r.lowerLegRight, Vector3.down - swing + side * 0.1, k, d)
  align(r.lowerLegLeft, r.footLeft, Vector3.down - fwd * (math.cos(t) * 0.6), k * 0.6, d)
  align(r.lowerLegRight, r.footRight, Vector3.down + fwd * (math.cos(t) * 0.6), k * 0.6, d)
end

local function both_hands(r, left, right, k, d)
  reach(r.handLeft, r.lowerArmLeft, r.upperArmLeft, left, k, d)
  reach(r.handRight, r.lowerArmRight, r.upperArmRight, right, k, d)
end

local function car_incoming(r, k, d, sc)
  if game.seated(r) then return end
  for _, v in ipairs(vehicles) do
    local car = v.body
    if not alive(car) then break end
    local to = r:GetRootPosition() - car.position
    local closing = Vector3.Dot(car.velocity, to.normalized)
    if closing > 50 and to.magnitude < closing * 0.8 then
      local face = r.head.transform.position + (car.position - r.head.transform.position).normalized * sc * 0.4
      both_hands(r, face - Vector3.up * sc * 0.05, face + Vector3.up * sc * 0.05, k * 1.4, d)
      return
    end
  end
end

local function tick(r)
  if not body.live(r.spine1) or game.seated(r) then return end
  local b = brain(r)
  local k, d = 240 * M.strength.value, 18
  local sc = game.scale(r)
  local root = r.spine1.rigidBody
  local dead = body.dead(r)
  local hit = body.last_hit(r)
  local since = hit and Time.time - hit.time or 99

  if dead then
    if b.death_time < 0 then b.death_time = Time.time end
    local t = Time.time - b.death_time
    if t < THROES then
      local fade = 1 - t / THROES
      for _, p in ipairs(body.parts(r)) do
        if math.random() < 0.06 * fade and body.live(p) then
          p.rigidBody:AddTorque(Random.insideUnitSphere * 25 * fade, ForceMode.VelocityChange)
        end
      end
      if hit and body.live(hit.part) then
        reach(r.handRight, r.lowerArmRight, r.upperArmRight, hit.part.transform.position, k * 0.4 * fade, d)
      end
    end
    return
  end
  b.death_time = -1

  if game.grounded(r) then
    b.off_feet = 0
    if since < 0.6 and body.live(hit.part) then
      local f = 1 - since / 0.6
      align(r.spine2, r.head, Vector3.up + hit.dir * 0.6, k * 0.6 * f, d)
      reach(r.handLeft, r.lowerArmLeft, r.upperArmLeft, hit.part.transform.position, k * f, d)
    end
    car_incoming(r, k, d, sc)
    return
  end
  b.off_feet = b.off_feet + Time.fixedDeltaTime

  local vel = root.velocity
  if not body.touching(r) then
    local fall = vel.sqrMagnitude > 1 and (vel.normalized + Vector3.down * 0.8).normalized or Vector3.down
    local hit = physics.raycast(root.position, fall, math.max(sc, vel.magnitude * 0.45), physics.ground)
    if root.angularVelocity.magnitude > 7 then
      local right = r.head.transform.right * sc * 0.06
      both_hands(r, r.head.transform.position - right, r.head.transform.position + right, k, d)
      curl(r, k * 0.8, d)
    elseif hit then
      local p = hit.point + Vector3.up * sc * 0.1
      local side = Vector3.Cross(Vector3.up, fall).normalized * sc * 0.25
      both_hands(r, p - side, p + side, k * 1.2, d)
      align(r.spine2, r.head, Vector3.up * 0.8 - fall * 0.4, k * 0.5, d)
      legs_to(r, -fall + Vector3.down, k * 0.6, d)
    else
      local t = Time.time * 7 + b.seed
      pedal(r, t, k * 0.7, d)
      if not body.hands_busy(r) then
        local fwd = Vector3.ProjectOnPlane(r.spine2.transform.forward, Vector3.up).normalized
        local axis = Vector3.Cross(Vector3.up, fwd)
        align(r.upperArmLeft, r.lowerArmLeft, Quaternion.AngleAxis(t * 60, axis) * Vector3.up, k * 0.6, d)
        align(r.upperArmRight, r.lowerArmRight, Quaternion.AngleAxis(t * 60 + 180, axis) * Vector3.up, k * 0.6, d)
      end
    end
    car_incoming(r, k, d, sc)
    return
  end

  local wound = since < 4 and body.live(hit.part) and hit.part.transform.position or body.wound(r, 30)
  if wound then
    both_hands(r, wound, wound + Vector3.up * sc * 0.05, k, d)
    local t = Time.time * 2.2 + b.seed
    local fwd = r.spine2.transform.forward
    align(r.upperLegLeft, r.lowerLegLeft, fwd * (0.6 + 0.4 * math.sin(t)) - Vector3.up * 0.2, k * 0.4, d)
    align(r.upperLegRight, r.lowerLegRight, fwd * (0.6 - 0.4 * math.sin(t)) - Vector3.up * 0.2, k * 0.4, d)
    root:AddTorque(r.spine2.transform.up * math.sin(t * 0.7) * 6 * M.strength.value, ForceMode.Acceleration)
    align(r.spine2, r.head, Vector3.up + fwd * 0.4, k * 0.3, d)
  elseif b.off_feet > 0.4 then
    local under = root.position + Vector3.down * sc * 0.6
    local right = r.spine2.transform.right * sc * 0.25
    both_hands(r, under - right, under + right, k, d)
    align(r.spine2, r.head, Vector3.up, k * 0.6, d)
    legs_to(r, Vector3.down + r.spine2.transform.forward * 0.4, k * 0.5, d)
  end
  car_incoming(r, k, d, sc)
end

function M.reset()
  brains = {}
end

local next_scan = 0

function M.fixed_update()
  if Time.time > next_scan then
    next_scan = Time.time + 0.5
    vehicles = game.vehicles()
  end
  step = step + 1
  hold = Time.fixedDeltaTime * 4.5
  for i, r in ipairs(game.ragdolls()) do
    local mine = game.is_local(r)
    if (step + i) % 4 == 0 and (not mine or M.on_you.value) then tick(r) end
  end
end

return M
