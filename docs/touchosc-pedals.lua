local throttle
local brake
local throttleFeedback
local brakeFeedback
local nextSend = 0

function init()
  throttle = self:findByName('Throttle', true)
  brake = self:findByName('Brake', true)
  throttleFeedback = self:findByName('ThrottleFeedback', true)
  brakeFeedback = self:findByName('BrakeFeedback', true)
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

function onReceiveOSC(message)
  if message[1] ~= '/pedals/feedback/state' then
    return
  end

  local arguments = message[2]
  local gas = arguments[1] and arguments[1].value
  local braking = arguments[2] and arguments[2].value
  if throttleFeedback and type(gas) == 'number' then
    throttleFeedback.values.x = gas
  end
  if brakeFeedback and type(braking) == 'number' then
    brakeFeedback.values.x = braking
  end
end
