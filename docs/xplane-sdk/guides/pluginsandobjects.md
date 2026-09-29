# Plugins and Objects - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

This tech note describes how plugins can interact with objects.
## Datarefs for Animation
Plugins can control the animation of an object using datarefs. The process is relatively simple:
  * The plugin defines a new dataref using _[[XPLMRegisterDataAccessor](/sdk/XPLMRegisterDataAccessor/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMRegisterDataAccessor)_. The dataref type should be one of: float, int, or double for non-array types, or float-array or int-array for array types.
  * The object references that dataref by name in an ANIM_ command. Dataref items are referenced using brackers, e.g. /myplugin/animation/bridge_length[3] would access item 3 of the dataref /myplugin/adnimation/bridge_length.

When the object is drawn (which is generally only when it is on screen), your dataref will be read to determine the current animation position.
Tip: pick dataref return values where 0.0 is a good default. If your plugin is not installed (and thus the dataref is not available) the animation code will use 0.0 as its value in calculating animation positions and rotations.
## Commands for Animation
Starting with X-Plane 9 and the 2.0 SDK, X-Plane's internal command-dispatching system is part of the plugin SDK and can be used to build animation functionality. In particular, in place of a dataref name in an object animation, you can instead use a command-name, e.g.
    
    ANIM_trans 0 0 0   0 5 0    0 1   CMND=sim/engines/engage_starter_1
    
This uses the current state of a command (sim/engines/engage_starter_1) to be interpretted as an integer value, 0 if off and 1 if being pressed. (In this example, we translate up 5 meters in the Y direction when the engine 1 starter command is being held down by any source.)
This functionality is part of the OBJ and panel interpreters inside X-Plane, not part of the X-Plane plugin APIs. Basically it givse authors access to command state. Plugins looking to determine the status of a command should register a handler for the command, pass the command through, and note the command state change in their callbacks. (This means plugin code will run only when a command is pressed, not per-frame.)
Plugins should avoid registering datarefs that start with CMND=, because these datarefs will not be accessable in the panel and object subsystems.
## Per-Object Variance
Some built-in datarefs that provide animation parameters vary their output based on which object is using them. For example, the particular heading of the crane dataref will be different for every object that uses it. This is done to keep scenery from moving in complete lock step.
The datarefs that have this behavior use the draw_object_x/y/z datarefs to create per-object variance -- see the next section to use this technique in your own datarefs.
## Determining Which Object Instance is Being Drawn
There may be many instances of an object in a scenery package. You do not have to provide unique OBJ files with different dataref names for each one; you can determine to some extent whch object is being drawn when your accessor is called.
The following float-type datarefs are available to tell you the location of the object that is being drawn at the instant your dataref is called:
sim/graphics/animation/draw_object_x  
sim/graphics/animation/draw_object_y  
sim/graphics/animation/draw_object_z  
sim/graphics/animation/draw_object_psi
The units are meters in local coordinates for XYZ and heading from true north in degrees for psi. These values let you tell the various instances apart. These datarefs are invalid outside the process of drawing an object.
Some caveats:
  * When scenery is reloaded, object XYZ will change. I recommend watching the local reference point (sim/flightmodel/position/lat_ref and sim/flightmodel/position/lon_ref) to determine when scenery has shifted, then reset state. You can attempt to determine the object's new position by converting from XYZ to lat/lon before a shift, then converting back, but be sure to use an epsilon-check, because the values will not be perfectly equal due to rounding errors.
  * Do not write code that assumes anything about the draw order of objects! When your dataref is called, do a fast hash of the draw_object datarefs to find your object and go from there. Do not assume that objects are drawn only once (we could have multi-pass rendering) and do not second guess our culling, which may not be perfectly efficient.
  * Do not expect exact matches of lat/lon/el after a scenery shift - the math has rounding error.

The code flow would look something like this:
  1. X-Plane's culling code decides an object needs to be drawn, given the current camera location.
  2. X-Plane temporarily sets the values of draw_object_XXXX to the location of the current object.
  3. X-Plane's object drawing loop starts.
  4. When X-Plane's object drawing loop hits an animation, it asks the SDK to fetch the animation value, which results in a call to a dataref published by your plugin.
  5. Now your plugin's GetDataf callback is being called.
  6. Your plugin then calls _[[XPLMGetDataf](/sdk/XPLMGetDataf/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMGetDataf)_ on the draw_object_XXXX datarefs to determinine WHICH instance of an object is being drawn.
  7. Your plugin then returns from its GetDataf callback a dataref animation value that is unique to this INSTANCE of the particular OBJ.
  8. X-Plane then animates that particular instance of an OBJ with the returned value.

The simplest form of this is to use the object location as a "seed" value for periodic or random motion, so that each object has a different seed value. (Please note that this will produce a "jump" at scenery load time - for consistent behavior across scenery shifts, consider using a quantized version of latitude and longitude.)
## Object Sequence Numbers
Object Sequence Numbers are not part of the X-Plane scenery SDK yet - this section is included primarily to explain some of the behaviors of the animation datarefs.
When an OBJ is placed in X-Plane, X-Plane tracks a few variables:
  * The location of the object (in XYZ cartesian space) - this is usually generated by "dropping" the object straight down until it hits terrain.
  * The heading of the object as a rotation from true north.
  * Which OBJ file is used to draw the object.
  * The object's sequence number.

The sequence number is an integer attached to each placement.
  * Most objects have 0 for a sequence number, that is, most of the time this feature is simply not used.
  * There is no plugin access to sequence numbers, nor can they be set using DSF files.
  * However, some animation datarefs will vary their behavior based on the sequence number.

When an apt.dat file is built into an airport in the sim, the airport code places objects from the library based on apt.dat data. (For example, given a runway's approach lighting system type, a sequence of objects are placed to form the lighting structure.)
A number of airport lighting elements require sequenced or coordinated behavior between multiple objects -- for example, the rabbit must flash in succession. To achieve this, the airport code will put a sequence number on each object that is part of the approach light structure (as an example).
The lighting datarefs then examine this sequence number and produce flashes at the right coordinated time.
_What this means for plugins and modelers_ : sequence numbers are only available in certain contexts. What this means for authors is that if you use a light animation dataref for a purpose other than its indended one, the sequence number may not be set correctly, producing incorrect behavior. For example, if you use the rabbit light dataref for anything other than approach lights that have a rabbit, the rabbit will not flash in a coordinated manner because the objects must be placed with sequence numbers.
In the future we may provide an interface for accessing sequence numbers or instance data, but for now consider this a warning that some of the "special purpose" lights in the dataref list really are special purpose!!
(You can create the equivalent of sequence numbers by examining the object draw x/y/z datarefs to understand which object is being drawn, but your plugin would have to translate this 3-d info into some form of organization.)
## Datarefs for Custom Lights
A custom light in an OBJ file can use a dataref - here's how it works:
  * The OBJ file contains 9 parameters for the custom light and an optional dataref
  * When the object is drawn, the dataref (of type float-array) is called. The array passed in contains the defaults from the OBJ file.
  * The dataref can then CHANGE or REPLACE all, some, or none of the 9 parameters.
  * The modified values are used to draw.

Thus a dataref can:
  * Generate light values.
  * Customize light values.
  * Use the default light values as "input" parameters to configure the light.

In the case of our own datarefs, often we use the RGBA color of the light to specify its properties, and the dataref replaces the RGBA values with white and some level of alpha (to turn the light on and off).
The nine parameters are:
  1. color (red) from 0 to 1.
  2. color (green) from 0 to 1.
  3. color (blue) from 0 to 1.
  4. alpha level from 0 to 1. Use alpha 0 to hide the light.
  5. light size. This is in, um, "light size units", that is, the size corresponds only to some internal sim code, not any real world unit. Light billboards are not sized in real-world meters - their size doesn't change at the same rate as objects as you zoom out.
  6. texture coordinate - left, from 0 to 1.
  7. texture coordinate - bottom, from 0 to 1.
  8. texture coordinate - right, from 0 to 1.
  9. texture coordinate - top, from 0 to 1.

Thus the dataref can change the color, the texture, even the size of a light.
In a typical design that we use, we leave the texture and size coordinates unchanged (so the OBJ can decide this) and use the RGBA as 4 input parameters, calculating whether the light is on (and how bright), writing this to alpha, and setting RGB back to 1 so the light is not tinted. (The texture of the light can provide any "color" needed.)
### Parameter Order for Custom Spill Lights
Custom spill lights in an OBJ, created via LIGHT_SPILL_CUSTOM, also work via a 9-float writable dataref, and again the params are in the array when your plugin is called and you can modify them. The parameter order is:
  1. color (red) from 0 to 1.
  2. color (green) from 0 to 1.
  3. color (blue) from 0 to 1.
  4. alpha level from 0 to 1. Use alpha 0 to hide the light.
  5. light size in meters. This is the radius from the center of the light to complete extinction of the lighting effect.
  6. cone radius. This is stored as the cosine half of the cone width, or use 1.0 for omni-directional lights.
  7. cone direction (X)
  8. cone direction (Y)
  9. cone direction (Z)

The last 3 numbers -must- form a -normalized- vector, that is, X*X+Y*Y+Z*Z == 1.0.
Authors Warning: X-Plane will normalize your dx,dy,dz when reading your OBJ, so you cannot pass -arbitrary- data through these params. If your OBJ8 values are meant to 'feed' the plugin, pass the data in RGBA.
## Light Equations for Directional Lights
Some lights use a 4-part vector to specify direction in RGBA. In this case, RGB form a vector (R = X, G = Y, B = Z) and A forms a constant term that is added. Note that the vector formed by RGB does NOT have to be normalized…if it is not, the light may be overly bright or dark.
Some lights use a 3-part vector to identify direction in RGB. In this case, the behavior is the same as a above, except a constant term is picked by X-Plane to guarantee that the light's maximum brightness is 1.0. Example: if you use the light vector (0,2,0) where length = 2, x-plane picks a constant term of -1.0. Thus the light is equivalent to the 4-part light (0,2,0,-1).
When a light uses two components (RG) then the direction is pre-determined (usually 0,0,1). The red component scales the dot product and the green component is added -- in other words, this is the same as having a non-normalized RGB in the above example. Example: the 2-component light (2,-1) has the implicit direction (0,0,1), so it is the same as the 4-component light (0,0,2,-1).
In both cases, the total value is clamped to 0…1, so:
  * If the scaling parameter is large and the offset is negative, the light will be more tightly focused, and dark (below 0.0) for a while.
  * If the scaling parameter is small and the offset is positive, the light will be less tightly focused.
  * If the scaling parameter is really small or 0 and the offset is positive, a light can be omnidirectional, or have some element that is bright all the time.
