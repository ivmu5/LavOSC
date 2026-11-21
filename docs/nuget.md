# LavOSC libraries

LavOSC routes Open Sound Control messages from a single UDP port to independent modules. The libraries target .NET 10.

`Laveyana.LavOSC.Core` provides `OscUdpReceiver`, `OscRouter` and the `IOscModule` contract. A module can receive private commands such as `/panel/preset` and subscribe to shared messages such as `/accxyz` through `OscModuleRegistration`.

Modules can implement `IOscFeedbackSource` to provide one `OscFeedback` per independent value. LavOSC compares arguments per client and address and sends deltas from a background thread. Use `OscFeedbackOrigin` to attribute a change to an accepted input and suppress its echo. `InitialArguments` specifies the baseline assumed for new recipients.

`Laveyana.LavOSC.Modules.Wheel` depends on Core. Its `WheelSession` uses `/accxyz` for steering and exposes `/wheel/calibrate`. Implement `ISteeringOutput` to connect it to your own output device. The LavOSC Windows application provides the vJoy adapter.

`Laveyana.LavOSC.Modules.Pedals` depends on Core. Its `PedalSession` receives and reports `/pedals/throttle` and `/pedals/brake`, smooths both inputs and resets stale channels. Implement `IPedalOutput` for your own output device.

See the [LavOSC repository](https://github.com/ivmu5/LavOSC) for a module example, registration in the Windows application and instructions for running your own host.
