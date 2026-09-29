# Overriding TCAS and providing traffic information - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

With X-Plane 11.50, plugins that display traffic in X-Plane, whether auto-generated or from an online multiplayer network, have to use the [[XPLMInstance](/sdk/XPLMInstance/)](https://developer.x-plane.com/sdk/XPLMInstance/) API to draw these aircraft in X-Plane's world. Drawing with [XPLMInstance](/sdk/XPLMInstance/) places the 3D object in the world, but does not actually tell X-Plane or other interested third parties, such as EFBs, about the existence of another aircraft in the airspace around the user. Various techniques have been used by authors, which are commonly known as "TCAS hack" that involve taking control of X-Plane AI aircraft, but not drawing them, by inhibiting drawing from a 3D callback. Some plugins also get around this by providing "invisible" aircraft to be moved by X-Plane. With Vulkan and Metal, the 3D callback that was previously used to change the AI plane count for the "TCAS hack" no longer works. It is therefore necessary to adopt the new TCAS override, which provides an official and compatible way to notify both X-Plane and third-party plugins of traffic.
## override_TCAS: Existing datarefs made useful
The new supported and recommended variant of the "TCAS hack", now called "TCAS override", relies entirely on datarefs and existing XPLM functions, so it requires no SDK update. It uses the following, well known datarefs, simply expanded in array size:
    
    sim/operation/override/override_TCAS int
    sim/cockpit2/tcas/indicators/relative_bearing_degs float[64]
    sim/cockpit2/tcas/indicators/relative_distance_mtrs float[64]
    sim/cockpit2/tcas/indicators/relative_altitude_mtrs float[64]
Using the new TCAS override requires that the plugin does the following:
  1. Acquire the AI planes with [[XPLMAcquirePlanes](/sdk/XPLMAcquirePlanes/)](https://developer.x-plane.com/sdk/XPLMAcquirePlanes/) - This should be done with a null pointer for the desired model paths, so it is fast. You should **only** call this when you are ready to supply planes **right now**. That is, an online network plugin should not acquire planes before it is actually connected to the network! Since acquiring planes with no model load is fast, this should be deferred until the user is actually connected to a server.
  2. Activate TCAS override by setting the sim/operation/override/override_TCAS dataref to 1. Only the plugin that has successfully acquired the AI planes can set this dataref!
  3. Tell X-Plane how many planes you want to fill in with TCAS override (up to 63) by calling [[XPLMSetActiveAircraftCount](/sdk/XPLMSetActiveAircraftCount/)](https://developer.x-plane.com/sdk/XPLMSetActiveAircraftCount/).  
Note that you will get to update all 19 multiplayer planes then, even if the user has a lower AI aircraft count setting. It is not necessary for the user to increase AI aircraft count or provide special models to be loaded as AI. In fact, TCAS override works even if the user has 0 AI planes configured.
  4. Assign each target a unique 24bit number between 1 and 16777215 and set it to sim/cockpit2/tcas/targets/modeS_id[N]. This will be the mode S ID of the target and is used to track your target even if you move it between slots.
  5. Tell X-Plane about the traffic targets by writing to the existing relative_bearing_degs, relative_distance_mtrs and relative_altitude_mtrs datarefs or to the new sim/cockpit2/tcas/targets/position/double/ datarefs. Only the plugin who has acquired the AI planes can write these datarefs!
  6. Optional: Fill in more information than just the position into the new sim/cockpit2/tcas/targets/position/ datarefs. This way, you can keep X-Plane and other plugins updated about the state of up to 63 other planes.
  7. Optional: [Provide a map layer](https://developer.x-plane.com/sdk/XPLMMap/) to the X-Plane map with icons for the planes you are providing. If you are doing that, set sim/operation/override/override_multiplayer_map_layer to 1 to stop X-Plane from drawing its own icons.
  8. If you have fewer targets than anticipated, you can simply stop writing the positional data to a target slot. X-Plane will forget the target (and forget its unique ID). There is no need to set a lower [[XPLMSetActiveAircraftCount](/sdk/XPLMSetActiveAircraftCount/)](https://developer.x-plane.com/sdk/XPLMSetActiveAircraftCount/) because X-Plane will drop TCAS targets that haven't been updated in 10 frames.
  9. When you are done providing traffic (the user has disconnected from the online network) set the override_TCAS dataref to 0 and call [[XPLMReleasePlanes](/sdk/XPLMReleasePlanes/)](https://developer.x-plane.com/sdk/XPLMReleasePlanes/). This will re-enable X-Plane traffic or allow other plugins, like a real world traffic provider, to then take over traffic from you. This will also re-enable the X-Plane default map behavior, so you will want to tear down your custom map layer as well!

This will allow X-Plane to display your traffic on X-Plane's default navdisplays, the G1000, the map and IOS console and also disseminate the information via ADS-B output to external EFB apps like FlyQ and ForeFlight.
For full source code, check out the [code example](https://developer.x-plane.com/?post_type=code-sample&p=39865)!
## Plugins and cockpit displays using existing datarefs
Aircraft and plugins wishing to display traffic information, whether it is from AI planes or multiplayer planes (including third-party online networks like VATSIM) should use the new datarefs to display up to 64 contacts.
~~Existing aircraft most likely require no changes to pick up 19 traffic targets injected by TCAS override, since they are synced to the multiplayer datarefs.~~
~~Many third party aircraft use the absolute position datarefs to find out about traffic around them and show it on their navdisplay. These datarefs~~
    
    ~~sim/multiplayer/position/planeN_x double N = 1..20
    sim/multiplayer/position/planeN_y double N = 1..20
    sim/multiplayer/position/planeN_z double N = 1..20~~
~~will be updated by X-Plane, when a traffic plugin is using TCAS override, along with the derived velocity vector for ADS-B out.~~  
~~[[XPLMCountAircraft](/sdk/XPLMCountAircraft/)](https://developer.x-plane.com/sdk/XPLMCountAircraft/) will return the number of TCAS targets actively updated in the last 10 frames by the plugin owning the override, regardless of the user setting for AI aircraft. Therefore, all instruments can display all aircraft that are being written to, without requiring the user to change any settings.~~
~~The same number is also available from the new dataref~~
    
    ~~sim/cockpit2/tcas/indicators/tcas_num_acf int~~
~~Third-party aircraft are also free to adopt the relative position datarefs to display traffic around them, since they now will be filled in with useful information by the traffic provider.~~
## New datarefs
New plugins can use the new set of datarefs that is not limited to 19 planes, but instead supports 63 aircraft currently. It is advisable to query the array size of sim/cockpit2/tcas/targets/modeS_id to find out how many targets the current version of X-Plane supports. Note that index 0 is reserved for the user plane.
X-Plane does not infer any information from the mode-S ID, nor does it make any guarantees about the user plane's mode-S ID, other than that it is unique in the session, and that it fits in 24bits. Traffic providers can set the MSB of the 32bit integer (i.e. the_modeS_id | 0x80000000) to indicate their 24bit ID is a "real" ID that corresponds to the actual airframe they are providing. You would do this if you are replaying real-life ADS-B data. By default, the MSB should not be set: in that case you must **not** use the mode-S ID to look up aircraft type, airline, livery or other such information. The user airplane is simply assigned a unique ID at random, so it might well be that the user is flying a 172 with a mode-S code that in the real world belongs to a 747. Only if your plugin has reliable information about the mode-S code of its traffic (replaying real world ADS-B data), you can assign real IDs rather than randomized ones and set the MSB. You must not assume other plugins or X-Plane assign IDs matching real world aircraft databases when they don't set the MSB.
You can optionally also provide a 7-byte flight ID (tailnumber or flightnumber) for each target which is used to identify the target on the X-Plane map.
By default, the transponder mode (sim/cockpit2/tcas/targets/ssr_mode int[64]) for each target will be mode Charlie (3), so that they report altitude and can be participate in TCAS conflict resolution. You are encouraged to change this value as is appropriate for the situation of flight, e.g. when the plane is on the ground, parked or unpowered. Compliant TCAS displays will NOT display the altitude of the target if it is broadcasting mode A, and not display it at all if the transponder is turned off.
Which of the positional datarefs are writable depends on how you update your targets:
  * In relative mode (writing the relative bearing, distance and altitude) X-Plane will calculate the world position, derive the velocity, and estimate the body angles for you. This is useful if all you care for is a blip on the display with roughly accurate data. Do **not** write to the xyz positions in that case!
  * In absolute mode (writing the x/y/z coordinates) your plugin should also write the vx, vz, vy velocities and roll/pitch/yaw body angles, as X-Plane will not estimate them. X-Plane will then fill in the relative bearing and distance information for you, and derive hpath and vpath from your cartesian velocities. X-Plane will also fill in the lat/lon/elevation global coordinates. This is useful for applications that know a lot about the state of the other plane, such as when it comes from an online network. Do **not** write to the bearing/distance/relativealtitude datarefs in this case!

Relative vs absolute updating is a per-target property, so your plugin can update different targets in different modes if required. The last dataref you write wins.
## Coordinate transformation
X-Plane will do the conversion from Cartesian coordinates to latitude, longitude and elevation for you.
To convert from Cartesian x/y/z to relative bearings, use a polar coordinate transformation, with
    
    sim/flightmodel/position/local_x and local_z
as the origin and
    
    sim/flightmodel/position/true_psi
as the polar axis, with the relative bearing counting **clockwise** as is usual in all cockpit indicators. Note that we use elevation (sim/flightmodel/position/elevation and sim/multiplayer/position/planeN_el) and not local_y to calculate the altitude difference! This is important for targets that are far away from the coordinate origin, as the earth curves away under the local tangent plane and elevation will be higher than y.
## Map icons
X-Plane will update the map showing other planes. However, it cannot know what the appropriate icon for the plane type you are supplying would be. If you want to make meaningful icons, you can [supply your own map layer](https://developer.x-plane.com/sdk/XPLMMap/) and tell X-Plane to stop drawing generic icons by setting sim/operation/override/override_multiplayer_map_layer to 1. This override is automatically cancelled when you give up TCAS_override, so don't forget to remove your map layer when you're done providing traffic!
## Plugin coordination
Since only one plugin can be providing the other planes at a time, it is important that plugins play nice with each other when it comes to deciding who gets to control the aircraft in the world. This is facilitated by the new plugin message [XPLM_MSG_RELEASE_PLANES](/sdk/XPLM_MSG_RELEASE_PLANES/) that is sent to a plugin owning the planes when another plugin requests them.
Playing nice starts with choosing the right time to call [[XPLMAcquirePlanes](/sdk/XPLMAcquirePlanes/)](https://developer.x-plane.com/sdk/XPLMAcquirePlanes/):  
**Do not** call [[XPLMAcquirePlanes](/sdk/XPLMAcquirePlanes/)](https://developer.x-plane.com/sdk/XPLMAcquirePlanes/) because you might at some point in the future maybe connect to some network. **Do** call [[XPLMAcquirePlanes](/sdk/XPLMAcquirePlanes/)](https://developer.x-plane.com/sdk/XPLMAcquirePlanes/) only when the user actually decided to connect to the network.
**Do** register a callback with [[XPLMAcquirePlanes](/sdk/XPLMAcquirePlanes/)](https://developer.x-plane.com/sdk/XPLMAcquirePlanes/) so that X-Plane can call you when another plugin gives up control over the planes.
Playing nicely requires [receiving](https://developer.x-plane.com/article/developing-plugins/#XPluginReceiveMessage) the _[XPLM_MSG_RELEASE_PLANES](/sdk/XPLM_MSG_RELEASE_PLANES/)_ message and acting accordingly:  
**Do** give up the planes if you are providing synthetic traffic and an online network now wants the planes. Traffic flown by humans should always take precedence over generated traffic. **Do** call [[XPLMReleasePlanes](/sdk/XPLMReleasePlanes/)](https://developer.x-plane.com/sdk/XPLMReleasePlanes/) when appropriate.  
**Do not** give up the planes to a synthetic traffic provider if you are currently connected to online flying, obviously.
Finally, give up the planes as soon as you no longer have a source. That is, if the user disconnects from their online flying network, **do** call [[XPLMReleasePlanes](/sdk/XPLMReleasePlanes/)](https://developer.x-plane.com/sdk/XPLMReleasePlanes/) to give synthetic traffic providers a chance to provide AI traffic again.
For a full example, refer to the [example plugin](https://developer.x-plane.com/?post_type=code-sample&p=39865).
## List of datarefs
    
    sim/cockpit2/tcas/indicators/relative_bearing_degs	float[64]	y	degrees	Relative bearing of each other plane in degrees for TCAS - relative to sim/flightmodel/position/true_psi not ground track or anything else
    sim/cockpit2/tcas/indicators/relative_distance_mtrs	float[64]	y	meters	Distance to each other plane in meters for TCAS
    sim/cockpit2/tcas/indicators/relative_altitude_mtrs	float[64]	y	meters	Relative altitude (positive means above us) for TCAS
    sim/cockpit2/tcas/indicators/tcas_alert	int	y	boolean	True if a TCAS alert is currently issued.
    sim/cockpit2/tacos/indicators/tcas_num_acf	int	n	integer	If TCAS is not overriden by plgugin, returns the number of planes in X-Plane, which might be under plugin control or X-Plane control. If TCAS is overriden, returns how many targets are actually being written to with the override. These are not necessarily consecutive entries in the TCAS arrays. 
    sim/cockpit2/tcas/targets/modeS_id	int[64]	y	integer	24bit (0-16777215 or 0 - 0xFFFFFF) unique ID of the airframe. This is also known as the ADS-B "hexcode".
    sim/cockpit2/tcas/targets/modeC_code	int[64]	y	integer	Mode C transponder code 0000 to 7777. This is not really an integer, this is an octal number.
    sim/cockpit2/tcas/targets/flight_id	byte[512]	y	string	7 character Flight ID, terminated by 0 byte. ICAO flightplan item 7.
    sim/cockpit2/tcas/targets/icao_type	byte[512]	y	string	7 character ICAO code, terminated by 0 byte. C172, B738, etc... see https://www.icao.int/publications/DOC8643/Pages/Search.aspx
    sim/cockpit2/tcas/targets/ssr_mode      int[64]         y       enum    Transponder mode: off=0, stdby=1, on (mode A)=2, alt (mode C)=3, test=4, GND (mode S)=5, ta_only (mode S)=6, ta/ra=7
    sim/cockpit2/tcas/targets/position/x	float[64]	y	meter	local X coordinate, meter. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/y	float[64]	y	meter	local Y coordinate, meter. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/z	float[64]	y	meter	local Z coordinate, meter. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/lat	float[64]	n	degrees	global coordinate, degrees.
    sim/cockpit2/tcas/targets/position/lon	float[64]	n	degrees	global coordinate, degrees.
    sim/cockpit2/tcas/targets/position/ele	float[64]	n	meter	global coordinate, meter.
    sim/cockpit2/tcas/targets/position/vx	float[64]	y	meter/s	local X velocity. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/vy	float[64]	y	meter/s	local Y velocity. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/vz	float[64]	y	meter/s	local Z velocity. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/vertical_speed	float[64]	y	feet/min	absolute vertical speed feet per minute. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/hpath	float[64]	n	degrees	ground track in true degrees. Derived from velocity vector.
    sim/cockpit2/tcas/targets/position/vpath	float[64]	n	degrees	flight path angle in degrees. Derived from velocity vector.
    sim/cockpit2/tcas/targets/position/V_msc	float[64]	n	meter/s	total true speed, norm of local velocity vector. That means it includes vertical speed!
    sim/cockpit2/tcas/targets/position/psi	float[64]	y	degrees	true heading orientation. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/the	float[64]	y	degrees	pitch angle. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/phi	float[64]	y	degrees	bank angle. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/weight_on_wheels	int[64]	y	boolean	ground/flight logic. Writeable only when override_TCAS is set.
    sim/cockpit2/tcas/targets/position/gear_deploy	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_gear_deploy[0]
    sim/cockpit2/tcas/targets/position/flap_ratio	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_flap_ratio
    sim/cockpit2/tcas/targets/position/flap_ratio2	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_flap_ratio2
    sim/cockpit2/tcas/targets/position/speedbrake_ratio	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_speedbrake_ratio
    sim/cockpit2/tcas/targets/position/slat_ratio	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_slat_ratio
    sim/cockpit2/tcas/targets/position/wing_sweep	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_wing_sweep
    sim/cockpit2/tcas/targets/position/throttle	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_throttle[0]
    sim/cockpit2/tcas/targets/position/yolk_pitch	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_yolk_pitch 
    sim/cockpit2/tcas/targets/position/yolk_roll	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_yolk_roll
    sim/cockpit2/tcas/targets/position/yolk_yaw	float[64]	y	ratio	mirror of sim/multiplayer/position/planeN_yolk_yaw
    sim/cockpit2/tcas/targets/position/lights	int[64]	y	bitfield	beacon=1, land=2, nav=4, strobe=8, taxi=16
    sim/cockpit2/tcas/targets/position/double/plane1_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane1_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane1_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane2_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane2_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane2_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane3_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane3_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane3_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane4_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane4_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane4_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane5_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane5_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane5_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane6_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane6_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane6_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane7_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane7_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane7_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane8_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane8_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane8_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane9_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane9_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane9_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane10_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane10_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane10_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane11_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane11_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane11_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane12_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane12_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane12_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane13_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane13_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane13_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane14_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane14_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane14_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane15_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane15_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane15_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane16_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane16_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane16_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane17_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane17_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane17_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane18_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane18_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane18_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane19_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane19_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane19_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane20_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane20_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane20_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane21_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane21_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane21_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane22_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane22_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane22_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane23_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane23_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane23_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane24_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane24_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane24_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane25_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane25_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane25_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane26_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane26_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane26_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane27_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane27_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane27_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane28_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane28_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane28_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane29_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane29_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane29_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane30_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane30_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane30_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane31_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane31_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane31_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane32_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane32_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane32_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane33_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane33_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane33_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane34_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane34_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane34_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane35_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane35_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane35_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane36_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane36_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane36_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane37_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane37_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane37_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane38_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane38_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane38_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane39_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane39_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane39_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane40_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane40_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane40_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane41_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane41_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane41_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane42_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane42_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane42_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane43_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane43_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane43_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane44_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane44_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane44_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane45_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane45_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane45_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane46_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane46_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane46_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane47_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane47_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane47_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane48_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane48_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane48_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane49_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane49_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane49_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane50_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane50_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane50_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane51_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane51_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane51_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane52_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane52_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane52_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane53_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane53_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane53_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane54_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane54_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane54_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane55_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane55_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane55_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane56_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane56_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane56_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane57_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane57_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane57_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane58_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane58_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane58_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane59_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane59_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane59_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane60_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane60_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane60_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane61_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane61_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane61_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane62_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane62_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane62_ele	double	n	meter	global coordinate, meter. 
    sim/cockpit2/tcas/targets/position/double/plane63_lat	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane63_lon	double	n	degrees	global coordinate, degrees
    sim/cockpit2/tcas/targets/position/double/plane63_ele	double	n	meter	global coordinate, meter.
