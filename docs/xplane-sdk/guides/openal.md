# OpenAL - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

This tech note explains how to use OpenAL in an X-Plane Plugin.
## An Overview of OpenAL Issues
OpenAL is a multichannel audio API targeted at games, modeled after the OpenGL API. OpenAL can be hardware accelerated, although often the user's implementation will be software. OpenAL makes it relatively easy to:
  * Play multiple sounds at once.
  * Control the pitch and volume of those sounds.
  * Loop or concatenate sounds.
  * Manipulate the sound in 3-d; OpenAL will calculate stereo and attenuation effects.

OpenAL also supports extensions; some implementations may have support for filters and effects.
For the purpose of understanding how OpenAL interacts with X-Plane and the plugin system, OpenAL supports the following opaque objects:
  * A "device" is a connection to a renderer (and possibly hardware) that will play audio.
  * A "context" provides a container for all OpenAL sounds (and other objects), associated with one device. In theory, a device may have more than one context. (This is discussed below.) A context is similar to an OpenGL context.
  * Each context has one "listener" object that represents where the listener is positioned in 3-d space.
  * Each context has one or more "source" objects that represent audio being created in 3-d space.
  * The context has some number of buffer objects that contain actual audio data. (One or more buffers are played through a source.)

## OpenAL use by X-Plane
X-Plane's use of OpenAL varies by version.
  * X-Plane 9 uses OpenAL for sound production on Linux and OS X.
  * X-Plane 10 will use OpenAL on all three platforms.

OpenAL will never be set up on a platform not supported (e.g. Windows + X-Plane 9).
If X-Plane does use OpenAL for sound on a given platform, there is still no guarantee that OpenAL will be present; X-Plane is capable of running even if the sound system is unavailable. OpenAL may be unavailable due to a local machine configuration problem, or the user intentionally disabling it with the -no_sound configuration.
While there is no guarantee of OpenAL on the platforms where X-Plane uses it, it is reasonable to skip sound code if X-Plane's sound is not functioning; as this happens when there is a configuration problem or user preference for no sound.
When X-Plane does use OpenAL, it will:
  * Open one device and create one context.
  * Create several sources and buffers for sound playback in that context.
  * Manage those sources.

X-Plane will leave the listener at 0,0,0 and not alter global sound settings.
## Using OpenAL via a Private Context
One way to use OpenAL is by creating your own context. You would:
  * Create your own device and context.
  * Set your context to current any time you must make OpenAL calls.
  * Reset the old context when done.

There are some advantages of this technique:
  * Since the context is yours, you can do anything to it, no restrictions. You can manipulate the listener, change global context properties, or add context-wide effects without affecting anything else.
  * Since you create the context, you can select a specific device.

There is one major limitation:
  * Some implementations of OpenAL on Windows do not support multiple contexts, so this technique may fail if X-Plane uses OpenAL (X-Plane 10) or any other plugin uses OpenAL.

This technique is generally safe to use on Mac and Linux, and has been the recommended technique for using OpenAL.
For an example, see: _[OpenAL Example](https://developer.x-plane.com/code-sample/openal-example/)_
## Using OpenAL via Context Sharing
You can also use OpenAL by sharing X-Plane. or any other plugin's context. This technique has a few advantages:
  * On platforms where X-Plane uses OpenAL (X-Plane 9 Mac/Lin, X-Plane 10) you can ignore context setup entirely and simply use X-Plane's context.
  * This technique will work even if the OpenAL implementation only one context.

There are some disadvantages:
  * You can't manipulate context globals or the listener; all 3-d sound must be done with source-relative positioning.
  * If the context has resource limits, you share these with X-Plane and all other plugins.
  * If X-Plane has opened the hardware, it may not have chosen the OpenAL device you want.

When sharing a context, there are two approaches to context sharing:
  1. Use the existing context if it exists.
  2. Create a context if none exists.

For an example of this second technique, see: _[OpenAL Shared Example](http://developer.x-plane.com/code-sample/openal-shared-example/)_.
The general context sharing rules are:
  * If the current context is null, there is no context to recycle.
  * If there is a current non-null context, you may use it, but do not change globals.
  * If you make a context and do not want anyone else to use it (e.g. you will use a custom listener position), always restore the current context to NULL.
  * If you want to share your context, leave it current.
  * Do not ever change the context from not-null to another context without putting it back. (That is, if someone else made a sharable context, it is too late for you to do so.)

## OpenAL on Windows, X-Plane 10
On Windows, X-Plane does not require that OpenAL be installed system-wide, nor does it use the system-wide OpenAL installation. Instead, X-Plane ships with OpenAL-Soft and uses that implementation exclusively.
Starting with X-Plane 10.20, X-Plane will load OpenAL-soft before loading _any_ plugins. This means that, starting on X-Plane 10.20, OpenAL is always available on Windows, and will always have a context to share, even for plugins that are installed globally.
For X-Plane 10.00 - 10.11, globally installed plugins load before X-Plane and may load a different copy of OpenAL than the system one. This has the potential to cause serious audio conflicts between X-Plane and globally installed plugins.
Our advice: if you need to use OpenAL on a globally installed plugin on Windows, target X-Plane 10.20 as your base version once it goes final.
If you must use OpenAL and target older versions of X-Plane 10 with a globally installed plugin, install your preferred OpenAL DLL in your fat plugin's folder, so that it is loaded. This ensures that you will only have to test this one configuration, and that your plugin will not be affected by what runtime the user has installed.
