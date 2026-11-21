local throttle
local brake
local throttleFeedback
local brakeFeedback
local nextSend = 0
local throttleAddress = '/pedals/throttle'
local brakeAddress = '/pedals/brake'

function init()
  throttle = self:findByName('Throttle', true)
  brake = self:findByName('Brake', true)
  throttleFeedback = self:findByName('ThrottleFeedback', true)
  brakeFeedback = self:findByName('BrakeFeedback', true)
end

local function sendPedal(fader, feedback, address)
  if not fader then
    return
  end

  local value = fader.values.touch and fader.values.x or 0
  fader.values.x = value
  if feedback then
    feedback.values.x = value
  end
  sendOSC(address, value, { true })
end

function update()
  local now = getMillis()
  if now < nextSend then
    return
  end

  nextSend = now + 40
  sendPedal(throttle, throttleFeedback, throttleAddress)
  sendPedal(brake, brakeFeedback, brakeAddress)
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
