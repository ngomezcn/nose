# OpenGL State - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

## OpenGL State and X-Plane
There are three catagories of OpenGL state in X-Plane:
  1. Disposable State.
  2. Managed State.
  3. Unmanaged State.

This note covers the differences between these kinds of state and how to handle them in your plugin.
### Disposable State
Disposable state is OpenGL state that X-Plane changes explicitly every time it makes an OpenGL call, and that you should assume is undefined at all times. The disposable state in X-Plane consists of the current vertex attributes:
  * The current color (glColor4f)
  * The current texture coordinates, for every texturing unit (glTexCoord2f).
  * The current normal vector (glNormal3f).
  * Any generic attributes (e.g. for shaders).

### Managed State
X-Plane tracks some state internally to avoid thrashing OpenGL. To set this state, call the appropriate XPLM calls instead of calling OpenGL directly. The managed state is:
  * Fogging - glEnable(GL_FOG)
  * The number of 2d texture units - glEnable(GL_TEXTURE_2D) per unit.
  * Lighting - glEnable(GL_LIGHTING)
  * Alpha testing - glEnable(GL_ALPHA_TEST)
  * Alpha blending - glEnable(GL_BLEND)
  * Z-Buffer reading - glEnable(GL_DEPTH_TEST)
  * Z-Buffer writing - glDepthMask(GL_TRUE)
  * The currently bound texture - glBindTexture(GL_TEXTURE_2D, …)

This state is set using _[[XPLMSetGraphicsState](/sdk/XPLMSetGraphicsState/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMSetGraphicsState)_ and _[[XPLMBindTexture2d](/sdk/XPLMBindTexture2d/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMBindTexture2d)_. Key points:
  * Only set managed state using XPLM calls, not OpenGL calls.
  * Do not assume the value of managed state when your plugin starts.

### Unmanaged State
All other OpenGL states that are not listed are unmanaged - X-Plane sets it on an ad-hoc basis, sometimes relying on previous values. Make sure you leave all of this state as you found it.
Unmanaged state with unreliable/random values (you must set these - X-Plane will just leave random stuff around):
  * All vertex pointers/vertex formats.
  * Client array enables (except for GL_VERTEX_ARRAY, which is always enabled).
  * The current texture binding.

Unmanaged state with known base values (these are things you can ignore unless you need something special):
  * The currently bound VBO for arrays and array-elements will be 0.
  * The current transform stack will apply a coordinate based on the current drawing callback.
  * Blending function will be GL_SRC_ALPHA,GL_ONE_MINUS_SRC_ALPHA, and the blend equation will be GL_FUNC_ADD.
  * The depth test will be GL_LEQUAL
  * The alpha test will be GL_GREATER,0
  * Stenciling will be off. The current surface may or may not have a stencil or depth buffer.
  * GL_VERTEX_ARRAY will be enabled in client state.
  * The front face will be defined as GL_CW, and GL_CULL_FACE will be enabled. (This is unusual for GL apps.)
  * The GL_UNPACK_ALIGNMENT and GL_PACK_ALIGNMENT pixel transfer alignments will be set to 1.

If any of the unmanaged state values change (in their initial input to your plugin) from these published values, please report a bug.
## Correct Plugin Behavior
### Correct Entering of a Draw Callback
When you enter a drawing callback, do not assume any OpenGL state is as you expect, except for unmanaged state with known default values.
### Exiting of a Draw Callback
When you leave a drawing callback, make sure:
  * You have only set managed state via the above XPLM routines.
  * You have restored any unmanaged state you have set.

### Calling Other Code From a Draw Callback
Do not call other plugins (by sending an interplugin message, etc.) from your draw callback. If you need to do this, please contact Sandy and Ben for a design-review…such behavior would be at the far edge of the plugin system's design limitations, and would risk OpenGL breakage in future X-Plane versions.
When you call X-Plane SDK routines, you must assume that:
  * The disposable state will be destroyed when the routine returns.
  * The managed state will have changed and needs to be explicitly reset.
  * You may need to restore unmanaged state depending on the routine. Unmanaged state will be unperturbed across the SDK call from your perspective.

In other words, calling into the XPLM is like leaving your routine (albeit temporarily).
In particular, _[[XPLMDrawString](/sdk/XPLMDrawString/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMDrawString)_ will almost always completely change the OpenGL managed state. XPLMDrawPlane is very dependent on graphics state and is recommended for advanced use only.
As of this time, only routines in _[[XPLMGraphics](/sdk/XPLMGraphics/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMGraphics)_ , _[[XPLMPlanes](/sdk/XPLMPlanes/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMPlanes)_ , and the widgets lib affect OpenGL state, but to be safe, code defensively. Do all of your drawing in atomic blocks. This will not only protect you from OpenGL state breakage, but probably improve your code performance by avoiding thrash.
### Pitfalls
There was a bug introduced in X-Plane 802, fixed in X-Plane 810: the state relating to VBOs had junk values that had to be reset. So, if you use any kind of client-array or VBO drawing and your users may have X-Plane 802-806, make sure to explicitly reset this stuff before drawing. (Users with these builds should probably update X-Plane.) This is a workaround, not the normal procedure for unmanaged state.
### Why is it like that?
The rest of this note discusses why the SDK OpenGL state works the way it does. Simply put, the design comes from X-Plane.
The OpenGL spec has always implied that:
  * State change is expensive.
  * The drivers won't catch and optimize "stupid" state change (binding the same texture over and over).
  * Therefore applications should optimize state change.

This second point is because: the driver would waste CPU cycles detecting duplicate state when an application might be in a position to remove duplicate state changes at compile time or when precomputing a series of calls that will be made repeatedly.
(In fact, application writers historically haven't been very good about optimization, so modern drivers from most game-type OGL cards do some anti-state-thrash optimization anyway.)
With that in mind, X-Plane categorizes state into the three categories based on performance:
  * When state is inexpensive to change (e.g. the current color via glColor) we simply set it ever time we need it and spend no cycles optimizing performance. OpenGL is relatively cheap for certain types of immediate state, particularly the per-vertex attributes.

  * When state is very rarely changed, we use a "set it, set it back" policy, with the default state being the one used most commonly in the code. For example, since virtually all X-Plane code uses GL_FILL for the polygon drawing mode, we simply leave it that way. The rare code that needs to do a wire-frame can set it and set it back. This means no state management code (for this type of state) for most of the app.

  * When state is both expensive and changes a lot, we use some inlined wrapper functions around the state that detect and skip duplicate state change. This is the "managed" state, and the XPLM routines wrap the internal X-Plane functions, assuring that our internal tracking is not out of sync with OpenGL.

(It should be noted that these managing routines do not call glIsEnabled - we have shadow variables. Generally querying state from the GL is a no-no and should be avoided in release code.)
## Coping With OpenGL Errors
OpenGL provides an asynchronous sticky error system - the GL error is set to a non-zero value when an error is generated, and cleared when checked.
X-Plane will _not_ check the GL error in a release build, and neither should you. Our suggestion is:
  * Set up your code to check the GL error after every block of GL operations (and certainly between any GL code and a return from your plugin's routine back to the XPLM) in debug mode only. Use a macro to remove these gl error checks in a release build.
  * Thoroughly test your debug build in a debugger to catch GL errors.
