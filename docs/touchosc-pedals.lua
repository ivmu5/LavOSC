local throttle
local brake
local throttleFeedback
local brakeFeedback
local nextRefresh = 0
local lastThrottle
local lastBrake
local throttleAddress = '/pedals/throttle'
local brakeAddress = '/pedals/brake'

function init()
  throttle = self:findByName('Throttle', true)
  brake = self:findByName('Brake', true)
  throttleFeedback = self:findByName('ThrottleFeedback', true)
  brakeFeedback = self:findByName('BrakeFeedback', true)
end

local function sendPedal(fader, feedback, address, previous, refresh)
  if not fader then
    return
  end

  local value = fader.values.touch and fader.values.x or 0
  if fader.values.x ~= value then
    fader.values.x = value
  end
  if feedback and feedback.values.x ~= value then
    feedback.values.x = value
  end
  if refresh or value ~= previous then
    sendOSC(address, value, { true })
  end
  return value
end

function update()
  local now = getMillis()
  if not throttle and not brake then
    return
  end

  local refresh = now >= nextRefresh
  if refresh then
    sendOSC('/pedals/heartbeat', { true })
    nextRefresh = now + 100
  end
  lastThrottle = sendPedal(throttle, throttleFeedback, throttleAddress, lastThrottle, refresh)
  lastBrake = sendPedal(brake, brakeFeedback, brakeAddress, lastBrake, refresh)
end

function onReceiveOSC(message)
  local indicator
  if message[1] == throttleAddress then
    indicator = throttleFeedback
  elseif message[1] == brakeAddress then
    indicator = brakeFeedback
  end

  local argument = indicator and message[2][1]
  if argument and type(argument.value) == 'number' then
    indicator.values.x = argument.value
  end
end
