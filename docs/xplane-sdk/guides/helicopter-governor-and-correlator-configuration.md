# Helicopter governor and correlator configuration - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

X-Plane 12 revises the interaction of collective and throttle control in helicopters. Existing helicopters retain the default behavior of X-Plane 11 until modified in Plane Maker 12 to opt into one of the new governor systems. The joystick control assignments for collective and throttle don't change, but there's a new joystick curve available for Robinson-style throttle control.
## Governor types
In Plane Maker 12, you can choose the type of throttle governor you can equip your helicopter with:
  1. No governor - this corresponds to leaving the governor checkbox unchecked in Plane Maker 11. The throttle will be fully manual. This is helpful if you want to code your own governor in a plugin.
  2. X-Plane 11 governor - this corresponds to checking the governor checkbox in Plane Maker 11 and preserves the behavior for legacy aircraft.
  3. Robinson-style piston correlator and governor - This adds a correlator to the throttle, and adds the "detent" to the throttle range surpassing the correlator. The governor will only engage at 80% or more rotor RPM
  4. Turbine governor - This changes the throttle to work like a condition lever in normal operation. It has a range below the idle release button where the engine is shut off. Above, you roll the throttle all the way on to enter governing range.

## Correlator
The correlator is a linear collective to throttle linkage that automatically opens the throttle as collective input increases. In Plane Maker, you can set two control points to define the linear connection. The correlator is added after the throttle, and does not change the twist of the throttle grip. The correlator input is added to the throttle twist grip input whenever the throttle is out of detent, which by default is 5% throttle, but can be customized to your throttle hardware with a joystick control curve. By holding the throttle in detent (rolling it below 5% or the customized point on your hardware) it is possible to practice auto-rotations with the engine running, as the then the correlator will not kick in during the flare.
## Piston Governor
The piston governor adds onto the throttle grip, so unlike the correlator the effect of the governor can be seen on the grip as the throttle will be twisted. When switched on, the governor will kick in once the throttle has been increased enough to go over 80% RPM. Because the governor adds to the correlator, the change in throttle grip caused by the governor will be relatively small, as big power changes are already absorbed by the correlator.
In case of governor failure, the governor fails in the current position, leaving the correlator intact, so it will require small adjustments by the pilot.
## Turbine Governor
Unlike the piston governor, the turbine governor governs maximum RPM. The throttle twist grip works more like a condition lever, if the governor is operating normally. The throttle has an OFF range (fuel shut off), then ground idle and then flight range. The "shutoff" range is guarded by the Idle Release lock, preventing accidental engine shutdowns. By twisting the throttle towards flight mode, maximum fuel flow will be enabled, but the actual fuel flow will be adjusted by the governor. The governor works without actually twisting the handle itself, so the throttle handle will show no feedback that the governor is operating, unlike in the piston variant. By blipping the rotor RPM trim, the governed RPM can be adjusted slightly.
In case of governor failure, the fuel flow restriction is removed, so the throttle in flight mode will most likely cause the rotor to overspeed. The pilot will need to reduce the throttle to prevent that, and will then need to manually control the throttle to keep the rotor RPM in the desired range. If configured in Plane Maker, a correlator input will be added to the throttle in case of governor failure, to limit the need for large excursions with the throttle.
The Off-range of the throttle on the turbine helicopter corresponds to the "detent" on the piston helicopter. That is, if you set your hardware up with a response curve for detent, that detent will act as the shut-off position (beyond idle release) on the turbine throttle grip. The commands
`sim/engines/collective_idle_release`
(for collective twist throttle) and
`sim/engines/collective_idle_release_1` to `sim/engines/collective_idle_release_8`
(for individual overhead levers)
need to be held while retarding the throttle into the off-range, in order to prevent accidental shut-downs.
If you don't have a hardware throttle, the X-Plane default throttle commands prevent you from accidentally killing the engine by rolling off the throttle. To turn the engine off, you need to hold the command to fully roll off the throttle, release it, and then push it again to actually roll the throttle over the detent and shut the engine off.
