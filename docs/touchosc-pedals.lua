local throttle
local brake
local nextRefresh = 0
local lastThrottle
local lastBrake
local throttleAddress = '/pedals/throttle'
local brakeAddress = '/pedals/brake'

function init()
  throttle = self:findByName('Throttle', true)
  brake = self:findByName('Brake', true)
end

local function sendPedal(fader, address, previous, refresh)
  if not fader then
    return
  end

  local value = fader.values.touch and fader.values.x or 0
  if fader.values.x ~= value then
    fader.values.x = value
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
  lastThrottle = sendPedal(throttle, throttleAddress, lastThrottle, refresh)
  lastBrake = sendPedal(brake, brakeAddress, lastBrake, refresh)
end
