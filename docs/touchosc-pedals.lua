local throttle
local brake
local nextSend = 0

function init()
  throttle = self:findByName('Throttle', true)
  brake = self:findByName('Brake', true)
end

local function pedalValue(fader)
  if fader.values.touch then
    return fader.values.x
  end

  fader.values.x = 0
  return 0
end

function update()
  local now = getMillis()
  if not throttle or not brake or now < nextSend then
    return
  end

  nextSend = now + 40
  sendOSC('/pedals/state', pedalValue(throttle), pedalValue(brake), { true })
end
