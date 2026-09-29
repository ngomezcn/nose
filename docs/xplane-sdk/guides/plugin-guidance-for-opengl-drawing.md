# Plugin Guidance for OpenGL Drawing - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

This document provides guidelines for using OpenGL to draw from X-Plane plugins running inside X-Plane’s process.
While X-Plane can use OpenGL, Vulkan, or Metal drivers to render to a graphics card, plugin-drawing is supported only via OpenGL. OpenGL is not the fastest or most modern API, but it does provide a robust way to draw in 3-d without exposing complicated and error prone implementation details like memory management, resource barriers, or concurrency, making it an appropriate choice for custom user interface and custom aircraft glass displays.
## General principles
In our work with third party add-ons, we see three kinds of add-on behavior that cause almost all of the compatibility problems, bugs, performance loss, and crashes. Here are three guiding principles for interacting with X-Plane.
### Do Not Lie to X-Plane
If your add-on uses plugin APIs for purposes other than their intended uses, you may have performance or compatibility problems later. Avoid things like using drawing callbacks for non-drawing purposes, using 3-d drawing callbacks for 2-d drawing, or non-window callbacks for UI.
If your add-on lies to X-Plane about your intentions, X-Plane no longer has optimal situational awareness and can no longer run plugins in an optimal way. Achieving the best possible performance is only possible if everyone plays fair and by the rules.
### Minimize XPLM and OpenGL Use
X-Plane users care deeply about performance; minimize your use of OpenGL and the XPLM to minimize your add-on’s negative impact on framerate. Only perform operations that are necessary, and cache values retrieved through XPLM API, except for OpenGL resources, to be reused later if possible. When drawing, avoid repeated OpenGL state changes and adopt modern OpenGL techniques where feasible. Using shaders and vertex buffer objects for static geometry is much faster than using pre OpenGL 2.0 immediate mode rendering.
### Do Not Use Undocumented Behavior
Do not use the SDK in ways that are not covered by the documentation; undocumented use of the SDK can appear to work but destabilize the sim or interfere with other add-ons, or can break in a future X-Plane update. If you are not sure from the documentation whether something is legal, ask us!
## Drawing Callbacks
Plugins receive an opportunity to draw inside X-Plane via drawing callbacks; the following section provides guidelines for when and how to use drawing callbacks correctly.
Register only the drawing callbacks that you need at the time you need them to do actual drawing! Dispatching any drawing callback requires a not-insignificant CPU time overhead and requires heavy synchronization in the case of Vulkan and Metal, both of which are completely wasted if no drawing actually happens.
### Use Drawing Callbacks Only for Drawing
Use drawing callbacks only for drawing, not for updating simulation state, bookkeeping, network IO, etc. Drawing callbacks can be called multiple or zero times per frame based on the user’s monitor configuration, so add-ons that use drawing callbacks for other purposes both hurt performance and may not work correctly in some situations.
### Use Real Windows for UI
Using real windows and the XPLM 3.x window API ([XPLMCreateWindowEx](/sdk/XPLMCreateWindowEx/), etc.) is highly recommended over using legacy draw callbacks. This provides the sim with the most situational awareness to improve dispatching and compositing of plugin windows. The real window API also provides correct UI interaction for floating windows and focus.
### Use Drawing Callbacks for Panels
The panel and gauge drawing callbacks let you draw under or on top of the 2-d and 3-d panel. Be sure to check the draw-type datarefs for the callbacks - see [Datarefs for Panel Drawing](https://developer.x-plane.com/article/plugin-guidance-for-opengl-drawing/#Datarefs_for_Panel_Drawing) below. Your panel draw callback can be called for both the albedo layer and the emissive layer of the panel separately; make sure your code draws the appropriate content for each.
### Use a Flight Loop Callback for Off-Screen FBOs
Use a flight loop callback to draw into your offscreen framebuffer. When you do this, X-Plane will not have set an FBO up for you to draw into, so you will bypass the overhead of synchronizing with our drawing on the GPU.
### Use [XPLMInstance](/sdk/XPLMInstance/) and Particles for 3-d Drawing Where Possible
Avoid using the 3D drawing callbacks unless absolutely necessary. Dispatching 3D drawing callbacks comes with potentially negative performance impacts and isn’t necessary most of the time.
The best interface for drawing “stuff” in the 3-d world is the XPLM instancing API to draw objects into the world or tie particle effects to your custom datarefs in order to create custom effects. The instancing API is straightforward to use, available since X-Plane 11.0, and replaces XPLMDrawObject and [XPLMDrawAircraft](/sdk/XPLMDrawAircraft/).
See also the [Plugin Compatibility Guide for X-Plane 11.50](https://developer.x-plane.com/article/plugin-compatibility-guide-for-x-plane-11-50/).
### Use a 2-d Callback for “Coach Marks”
When drawing overlays or other kinds of scene annotations, a 2D drawing callback is much better suited than a 3D drawing callback. You can use the projection matrices provided by X-Plane to efficiently transform 3D coordinates to 2D screen coordinates.
A sample of this can be found here: <https://developer.x-plane.com/code-sample/coachmarks/>
See also the [Plugin Compatibility Guide for X-Plane 11.50](https://developer.x-plane.com/article/plugin-compatibility-guide-for-x-plane-11-50/).
### Use a Panel Texture to Draw “on” the Aircraft
If you need to draw onto the aircraft, e.g. to simulate damage to the fuselage, rain on a wind-screen, or a livery change, you can map a panel texture to that section of the aircraft and then use a panel drawing callback for drawing. This can be used for dynamic decals or other effects, for example, that would otherwise require complicated use of the 3D drawing callbacks.
Panel texturing is available for all parts of the aircraft, not just the cockpit object, but is more expensive than using disk-based textures - use it only where the dynamic effect is absolutely necessary.
If your add-on needs more panel texture space, you can use panel regions to get access to four separate dynamic textures.
### Use the New 3-d Callback on Windows Only When Necessary
Using the new 3D drawing callback under Vulkan is not recommended unless absolutely necessary! It comes with a lot of gotchas and caveats and is intended only for add ons that truly need to do custom scenery drawing in 3D, like weather add ons. Before using the 3D drawing callback, evaluate all alternatives to see if there isn’t a better solution for what you are trying to achieve.
The new 3-d drawing callback works with OpenGL and Vulkan but not Metal; when running under Vulkan, a number of aspects of the drawing environment will be different:
  * The depth buffer will be in reverse-float-Z, not standard integer format; the clip control will be DirectX style and the depth compare function will be inverted.
  * The origin of the frame buffer may be inverted.

See the appendix for datarefs that let a plugin dynamically detect these conditions.
See also: [Plugin Compatibility Guide for X-Plane 11.50](https://developer.x-plane.com/article/plugin-compatibility-guide-for-x-plane-11-50/).
## OpenGL State Management
Your plugin shares an OpenGL context with other plugins and sometimes with X-Plane itself; this section provides guidance on how to change OpenGL state correctly.
### Don’t Call glGetXXXX
Do not use glGet to retrieve OpenGL state. This usually results in some kind of stall for the driver and has detrimental effects on performance. For custom state management it is usually much better to potentially set OpenGL state again and then cache that known value for future state changes.
X-Plane provides access to the transform stack (upon entrance to your call stack) and viewports via datarefs, as well as the currently bound FBO (although ideally this should not be changed).
If you need a state that isn’t covered here, please contact us - there may be a different way to approach your rendering code.
### Call glGetError in Debug Builds Only
Please call glGetError in debug builds and don’t ship code that breaks the OpenGL state machine. Your plugin should never execute code that leaves an OpenGL error in place.
When shipping your plugin, **do not** call glGetError as it can hurt performance. A simple macro that wraps glGetError() calls and makes it easy to toggle usually works best and makes it easy to ship the version of code you want.
If your add-on creates framebuffers, please do check for framebuffer completeness once when you create the FBO; your add-on should not create and destroy FBOs on a regular basis.
### Use the XPLM APIs to Manage State Where Possible
The XPLM APIs provide a number of calls to manage state; where these functions exist, please use them; they will avoid setting state multiple times.
  * [XPLMSetGraphicsState](/sdk/XPLMSetGraphicsState/) controls blend enable, fixed function alpha test enable, depth mask and depth test enable, fixed function fog and lighting, and fixed function texture enabling.
  * [XPLMBindTexture2d](/sdk/XPLMBindTexture2d/) controls the 2-d texture binding point.
  * XPLMGenerateTextureIDs wraps texture object generation.

### Restore all Other State Except Attributes When Done
Any OpenGL state other than vertex attributes that is not covered by the APIs above must be restored to its initial state when your callback completes. This includes:
  * Unbinding any bound shaders, VAOs, VBOs and non-2d textures to 0.
  * Restoring the drawing array state (only the fixed function vertex array should be enabled).

You do not need to reset the client array _pointer_ state, nor should you assume upon entrance to a callback that it contains anything sane.
### Restore the Current FBO By Dataref
If you must render to an offscreen FBO in a draw callback for some reason, you must restore the currently active FBO before returning from it. Don’t call glGet() to retrieve it, instead use the _sim/graphics/view/current_gl_fbo_ dataref to figure out what FBO to restore.
### [XPLMDrawString](/sdk/XPLMDrawString/) and Friends May Change State
[XPLMDrawString](/sdk/XPLMDrawString/) and the other API drawing calls can change all of the states mentioned above; while you do not need to clean up after them, do not assume your own state setup is correct after they have been called.
## Illegal OpenGL Usage
The following techniques are not allowed from plugins running inside X-Plane.
### Do Not Draw from Non-Drawing Callbacks
Non-drawing callbacks aren’t necessarily dispatched in a way that supports drawing. Drawing in a non drawing callback will lead to undefined behavior with regards to your own rendering and the effects on the rest of the sim.
### Do Not Mutate X-Plane’s OpenGL Resources
Resources obtained through the XPLM API should be seen as immutable, with the exception of the _contents_ of the active FBO during draw callbacks. Attempting to change any internal X-Plane resources can lead to heavily decreased performance or have other unexpected side effects, like crashing.
### Do Not Locate and Use Private X-Plane OpenGL Resources
Resources not exposed through the XPLM API are not fair game for use. These resources aren’t guaranteed to remain stable between or even within X-Plane runs or future versions. X-Plane is assumed to be the only consumer of these resources and will not attempt synchronizing with plugins when modifying them.
### Do Not Hold on to X-Plane OpenGL Resources
While it might be tempting to hold on to OpenGL resources obtained in previous draw callbacks and then reuse them in other callbacks, doing so can have disastrous consequences. This is especially true under Vulkan and Metal, where resources require special synchronization. If missing, this can lead to driver or system crashes and desktop freezes.
### Do Not Change State to Affect X-Plane
Attempting to change OpenGL state to trick X-Plane into rendering differently is heavily discouraged. This can lead to X-Plane doing improper bookkeeping of its own state, which can lead to heavily impacted performance. This is also not guaranteed to work between versions or even between runs and can break at any time.
## Performance
This section contains performance guidelines for OpenGL plugins.
### Pre-Initialize Your Add-on
Perform heavy initialization at aircraft load or flight start time. At runtime, a plugin should be able to just work without lazily initializing any state or computing expensive look up tables. The goal should be to never add unpredictable latency to the frame time.
### Do Less
Avoid unnecessary work; preload resources and precompute expensive results.
Follow standard OpenGL performance practices:
  * Move computing work from fragment to vertex shaders.
  * Reduce API calls that change state.
  * Reduce the number of draw calls by merging them where possible.

### Do More but Consistently
Attempting to improve framerate by adopting a flip-flop mechanism where one frame doesn’t do heavy work and the other does is highly discouraged. Schemes like this help nothing with the perceived performance of the sim, on the contrary, the very uneven frame times will have a noticeable impact on the user experience. It is much better to instead do the work every frame and provide a slightly lower but much more consistent framerate. Consistently drawing frames on the screen makes the sim appear much smoother to the user.
### Consider Using Multicore
If your add-on needs to compute values per frame that are expensive (e.g. take several milliseconds to compute) and you cannot optimize the computation, consider doing the work on a separate thread.
Moving work to a separate thread in a plugin is quite complex because:
  * The SDK provides no built-in facilities to help with this.
  * No SDK APIs may be called from worker threads you create.
  * You do not have access to the SDK-provided OpenGL context from a worker thread.
  * You cannot block waiting for data completion from an SDK callback.

Typically to make this work you will need to launch a worker thread once and have it block on a semaphore to pick up tasks that can be computed independently without XPLM calls and then returned to the main thread via some kind of thread-safe queue.
## Appendix A - DataRefs
This section covers datarefs that can be used to get more information about the rendering environment or interact with X-Plane during drawing callbacks. All callbacks should be resolved via XPLMFindDataref at plugin startup but should be read from inside the drawing callback to get information contextual to a particular draw callback. Read results may be undefined outside of or in the wrong draw callback.
### Datarefs for Panel Drawing
These drawing callbacks are appropriate from panel drawing callbacks (before/after panel and gauges).
sim/graphics/view/panel_render_type (int, read-only)
This dataref indicates the type of panel rendering being done:
  * 0 render the entire panel (day time and lit elements with full lighting for 2-d panels)
  * 1 albedo only (render solid elements without lighting)
  * 2 emissive only (render lit elements only)

sim/graphics/view/panel_render_new_blending (int, read-only)
This dataref indicates whether the blending mode for the 3-d panel for the current aircraft is the deprecated, legacy “max” alpha blending mode or the modern preferred alpha blending with correct alpha treatment.
  * 0 blending mode is legacy max mode
  * 1 blending mode is modern

The blending equation for legacy max mode is:
glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
glBlendEquationSeparate(GL_FUNC_ADD, GL_MAX);
The blending equation for modern blending mode is:
glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA,
GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
glBlendEquation(GL_FUNC_ADD);
Modern blending mode will correctly composite multiple translucent elements over a translucent or clear panel; legacy max mode gives incorrect but often acceptable results.
**Warning:** legacy max mode is deprecated and will be dropped from X-Plane in the future. At that point all aircraft will render using blend mode regardless of the content of their art assets.
#### Datarefs for Panel Clicking
When the mouse is being used over a 3-d panel (via a region mapped with ATTR_cockpit), the following datarefs provide information about the click location.
sim/graphics/view/click_3d_x (float, read-only)
sim/graphics/view/click_3d_y (float, read-only)
This is the location of the mouse in the UV map coordinates of the underlying object.
sim/graphics/view/click_3d_x_pixels (float, read-only
sim/graphics/view/click_3d_y_pixels (float, read-only)
This is the location of the mouse in panel pixel coordinates, or -1 if the mouse is not over the panel.
For all of these datarefs, they are only valid during draw callbacks for the monitor that the mouse is over. Therefore if your plugin is multi-monitor aware, you need to read these from the draw callback for the window that you are using to capture clicks.
(We do not recommend using these datarefs to implement mouse interactions; to capture clicks on a 2-d screen, use a generic instrument on the panel, or consider a real 3-d manipulator on the object itself.)
#### Datarefs for Panel Location and Scrolling
The location of the panel is provided via two sets of rectangles during panel and gauge drawing callbacks:
sim/graphics/view/panel_total_pnl_l (float, read-only)
sim/graphics/view/panel_total_pnl_b (float, read-only)
sim/graphics/view/panel_total_pnl_r (float, read-only)
sim/graphics/view/panel_total_pnl_t (float, read-only)
These four datarefs form the total bounds of the panel in the modelview coordinate system of the panel drawing callback. You can use these datarefs from inside a panel drawing callback to “calibrate” the location of your drawing. Due to scrolling of panels, the origin of the panel bitmap is not guaranteed to be in any one location on the panel.
sim/graphics/view/panel_visible_pnl_l (float, read-only)
sim/graphics/view/panel_visible_pnl_b (float, read-only)
sim/graphics/view/panel_visible_pnl_r (float, read-only)
sim/graphics/view/panel_visible_pnl_t (float, read-only)
These four datarefs provide the rectangle within the panel that is visible within the current OpenGL viewport. If the panel does not completely fill the viewport, some values could be outside the total bounds of the panel.
These datarefs can be used to identify which panel region is being rendered, for aircraft that use cockpit panel regions. They can also be used to cull gauge drawing that is not going to be visible.
Note that these datarefs are not the same as the current viewport - they are specified in modelview coordinates - that is, the current transform in effect for OpenGL drawing.
sim/graphics/view/panel_total_win_l (float, read-only)
sim/graphics/view/panel_total_win_b (float, read-only)
sim/graphics/view/panel_total_win_r (float, read-only)
sim/graphics/view/panel_total_win_t (float, read-only)
For 2-d panels that are visible on-screen, these datarefs provide the location of the total panel in modelview coordinates during UI drawing callbacks (e.g. post-window draw callbacks and [XPLMDisplay](/sdk/XPLMDisplay/) window draw callbacks). These datarefs can be used to calibrate the panel location to user interface elements.
sim/graphics/view/panel_visible_win_l (float, read-only)
sim/graphics/view/panel_visible_win_b (float, read-only)
sim/graphics/view/panel_visible_win_r (float, read-only)
sim/graphics/view/panel_visible_win_t (float, read-only)
These datarefs provide the visible extent of the panel in current modelview coordinates during UI callbacks; they can be used for culling gauges that are drawn in UI layers but must match the panel’s location.
### Datarefs for 3-d Drawing Callbacks
These datarefs can be read during 3-d drawing callbacks to find details about 3-d drawing.
sim/graphics/view/world_render_type (int, read-only)
This dataref indicates the type of rendering that X-Plane is trying to accomplish with its 3-d callback. The following render passes are visible to plugins:
  * 0 Regular 3-d rendering pass
  * 1 Prep source imagery for water reflections
  * 3 Render depth for shadow maps
  * 6 Prep source imagery for environment cube maps

3-d draw callbacks may be called more than once, so opting out of drawing based on this enumeration can have performance benefits.
**Note** : by default only regular 3-d render passes are dispatched to plugins; you must enable the capability “XPLM_WANTS_REFLECTIONS“ to receive other types of 3-d drawing callbacks.
**Note** : the modern 3-d callback does not dispatch shadows; use XPLM object instances for shadow-casting solid geometry.
sim/graphics/view/plane_render_type (int, read-only)
Before X-Plane 11.40, the aircraft drawing phases were the only ones that correctly separated solid and blended drawing; during an aircraft-phase drawing callback this dataref contains:
  * 1 Solid geometry is being drawn for the aircraft. If HDR is enabled and the drawing pass is normal, drawing is going into the G-Buffer.
  * 2 Blended geometry is being drawn for the aircraft. If HDR is enabled, this will be after the G-Buffer is resolved.

For plugins using the modern 3-d drawing callback, the one callback that a plugin receives is equivalent to a post-aircraft drawing callback for blending. Solid drawing is not available - use instancing to add models to the X-Plane world.
sim/graphics/view/draw_call_type (int, read-only)
For 3-d drawing callbacks, this dataref indicates the type of drawing being done for VR multi-eye rendering:
  * 1 Mono (regular) drawing is in progress
  * 3 Stereo drawing is in effect and this call is to render the left eye
  * 4 Stereo drawing is in effect and this call is to render the right eye.

When the left and right eye are dispatched, the transform stack will have been modified to take into account per-eye offsets.
sim/graphics/settings/HDR_on (int, read-only)
This boolean dataref tells whether the HDR deferred renderer is enabled.
  * 0 HDR is not enabled
  * 1 HDR is enabled

sim/graphics/settings/scattering_on (int, read-only)
This boolean dataref tells whether atmospheric scattering is enabled; since atmospheric scattering is always on in X-Plane 11, it will always have a value of 1.
### Datarefs for OpenGL State and Modes
All drawing callbacks provide datarefs that provide access to the transform stack at the time your drawing callback is called.
sim/graphics/view/projection_matrix (float[16], read-only)
sim/graphics/view/modelview_matrix (float[16], read-only)
sim/graphics/view/viewport (int[4], read-only)
These three datarefs provide complete access to the modelview, projection and viewport transformations, allowing you to map between modelview and OS window coordinates.
sim/graphics/view/world_matrix (float[16], read-only)
sim/graphics/view/acf_matrix (float[16], read-only)
sim/graphics/view/projection_matrix_3d (float[16], read-only)
These three datarefs can be read during a 2-d drawing callback or window drawing callback to gain access to the transform stack that was used to render the 3-d world, if there was a 3-d render. The world matrix provides the model-view matrix for standard OpenGL world coordinates (e.g. for [XPLMWorldToLocal](/sdk/XPLMWorldToLocal/)).
The acf matrix provides a modelveiw matrix whose origin is the aircraft’s nominal CG and whose axes are aligned with the aircraft. This is equivalent to starting with the world matrix, translating to the aircraft location and rotating by the aircraft Eulers, but may provide better precision.
sim/graphics/view/is_reverse_float_z (int, read-only)
This dataref indicates whether the depth buffer for the current drawing callback is using regular linear depth encoding or reverse-float-Z conventions.
ValueDepth FormatDepth FunctionDepth Range
0GL_DEPTH_COMPONENT_24GL_LEQUAL-1..1
1GL_DEPTH_COMPONENT_32FGL_GEQUAL0..1
The depth buffer will have an attached 8 bit stencil buffer.
sim/graphics/view/is_reverse_y (int, read-only)
This dataref indicates whether the current rendering pass uses OpenGL conventions for the framebuffer (origin in lower left, Y axis points up) or DirectX/Metal Conventions (origin in upper right, Y axis points down). Even when X-Plane is running in reverse Y mode, the transform stack will be set up such that the model view matrix has the same input coordinate system, e.g. Y is up in 3-d, and Z is south.
### Legacy Datarefs for Object Animation
When datarefs are read from a plugin to compute an object’s animation pose or to calculate the setup of its lights, the following datarefs are available to read to learn which object is being rendered.
sim/graphics/animation/draw_object_x (float, read-only)
sim/graphics/animation/draw_object_y (float, read-only)
sim/graphics/animation/draw_object_z (float, read-only)
sim/graphics/animation/draw_object_psi (float, read-only)
This technique for determining which object is being animated is deprecated and is made obsolete by XPLMInstancing.
