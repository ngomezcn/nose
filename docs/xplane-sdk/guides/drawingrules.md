# Drawing Rules - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

## The Four Basic Drawing Environments
X-Plane has four fundamental drawing environments:
  * The 3-d world
  * The 2-d cockpit
  * The 3-d cockpit
  * The 2-d user interface

A warning about these environments: there isn't a clear correlation between the current view and what drawing phases are called. There is also not a clear correlation between the current view and whether the 3-d cockpit will be drawn. Rather these things are a function of the specific view and specific plane. Your best bet is to draw in response to certain drawing phases.
## General Rules
Some basic rules to follow for safe drawing in X-Plane. Follow these rules to increase the likelihood of your plugin working in future sim versions.
OpenGL state:
  * Make no assumptions about OpenGL state when you enter your plugin.
  * Unless you change OpenGL state with an XPLM routine, leave OpenGL state the way you found it.
  * OpenGL state may change when calling XPLM routines, including reading data-refs!
  * Try to do drawing in large blocks to avoid having to deal with the above state problems frequently.
  * Lighting and fog are only trustworthy in 3-d world viewing.

Drawing callbacks:
  * Do not assume a correlation between drawing callback and sim frame-rate.
  * Use the elapsed time to determine animation.
  * If you have expensive calculations, try to move them to a processing callback, or do them "on demand" (e.g. once the first time they are required per sim frame). Otherwise multiple calls to your drawing callback may thrash your code.
  * Try to structure drawing to not depend on the sim's current view; some views may be composites of several view-like states.
  * Read datarefs that affect drawing from within the drawing callback. Do not cache dataref values. Some view-related datarefs are only valid from within a view callback.
  * Do not use the "first" and "last" callbacks (either for 3-d or the panel). See specifics on drawing phases later in this technote.

## Drawing to the 3-d world
The drawing-hook API was designed for use with X-Plane 6; a decade later, X-Plane's underlying drawing has changed radically. These guidelines describe how to cope with modern (X-Plane 9, 10) versions of the sim.
To reliably draw in 3-d in X-Plane 9 or 10, follow the following guidelines:
  * Do not use the 'before' feature of drawing hooks to try to bypass or cancel drawing; always draw after the sim.
  * Use the aircraft phase to draw in 3-d (and draw after the x-plane aircraft).
  * For normal aircraft drawing hook will be called twice per frame. The first time will be to draw solid geometry. Use this time only if you intend to call XPLMObject. The second time will be for non-solid geometry - do any custom drawing yourself in this second call. (When X-Plane 10.30 comes out, a new dataref sim/graphics/view/plane_render_type will identify whether we are in the solid phase (1) or blended (2).
  * You can identify drawing calls made to compute shadows by looking at sim/graphics/view/world_render_type - this will be 0 for normal rendering, 1 for reflections (called only if your plugin enables the capability for reflection drawing) and 3 for shadows. When being called for shadows, you only need to output depth.

You can see an example of code that handles these cases correctly here:
_<https://github.com/wadesworld2112/libxplanemp/blob/nextgen_csl/src/XPMPMultiplayer.cpp>_
## Drawing in the 2-d cockpit
Use the [xplm_Phase_Gauges](/sdk/xplm_Phase_Gauges/) phase to add elements to the 2-d panel. Try to avoid using the [xplm_Phase_Panel](/sdk/xplm_Phase_Panel/) phase (which draws the background) - replacing the background is probably not a good idea.
Note that the [xplm_Phase_Gauges](/sdk/xplm_Phase_Gauges/) phase is called one or more times to prep the 3-d panel texture, or one or more times to draw the 2-d panel.
Use the view rect datarefs from the gauges callback to determine the right location to draw. Do not cache these - they can change per callback!
The [xplm_Phase_Gauges](/sdk/xplm_Phase_Gauges/) drawing phase is a relatively safe one to use - it is only called when 2-d instruments are needed for some purpose, and the view rects will indicate which ones. (Gauges can be needed for the 2-d panel, interior 3-d panel, or even 3-d panel visible from outside through the cockpit windows. You can use the view_is_external dataref to simplify drawing if you need to reduce the framerate cost of drawing your panel.)
## Drawing in the 3-d cockpit
Right now there is no good way to draw in the 3-d cockpit. We recommend either:
  * Draw to the 2-d panel's opaque area (from [xplm_Phase_Gauges](/sdk/xplm_Phase_Gauges/)) and use the panel texture. (You cannot draw into transparent areas.)
  * Use an animated 3-d cockpit object and drive animation datarefs from your plugin.

## Drawing to the 2-d user interface
Use an XPLMWindow where possible. Use the before/after [xplm_Phase_Window](/sdk/xplm_Phase_Window/) as needed.
## Specifics on drawing phases:
[xplm_Phase_FirstScene](/sdk/xplm_Phase_FirstScene/) Do not use  
[xplm_Phase_Terrain](/sdk/xplm_Phase_Terrain/) Use to disable 3-d drawing mostly for framerate  
[xplm_Phase_Airports](/sdk/xplm_Phase_Airports/) Cosider using "objects" phase instead  
[xplm_Phase_Vectors](/sdk/xplm_Phase_Vectors/) DEPRECATED - this phase is not called in X-Plane 8.  
[xplm_Phase_Objects](/sdk/xplm_Phase_Objects/) Use this phase for drawing 3-d clutter  
[xplm_Phase_Airplanes](/sdk/xplm_Phase_Airplanes/) Use this phase to hide all airplanes or draw onto the airplanes  
[xplm_Phase_LastScene](/sdk/xplm_Phase_LastScene/) Do not use  
[xplm_Phase_FirstCockpit](/sdk/xplm_Phase_FirstCockpit/) Do not use  
[xplm_Phase_Panel](/sdk/xplm_Phase_Panel/) Use with caution - consider gauges phase instead  
[xplm_Phase_Gauges](/sdk/xplm_Phase_Gauges/) Use to modify 2-d or 3-d panel  
[xplm_Phase_Window](/sdk/xplm_Phase_Window/) Use for UI when XPLMWindow isn't adequate  
[xplm_Phase_LastCockpit](/sdk/xplm_Phase_LastCockpit/) Do not use
