# Developing Plugins - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

A plugin is executable code that runs inside X-Plane, extending what X-Plane does. Plugins are modular, allowing developers to extend the simulator without having to have the source code to the simulator. This article describes the basics of what a plugin is, how it works, and describes how we write one.
If you are an experienced programmer, you may already be familiar with some of the material presented here. If you have not done modular programming using DLLs, we introduce the concepts and also provide step-by-step examples of writing a very basic plugin.
## What a Plugin is and What Plugins Can Do
Before discussing how to write a plugin, we will describe to some extent what it can do. You may not need a plugin for what you are trying to accomplish, and a plugin may not be able to accomplish what you are trying to do.
Plugins are executable code that are run inside X-Plane. Plugins allow you to extend the flight simulator's capabilities or to gain access to the simulator's data. Plugins are different from conventional programs in that they are not complete programs in themselves, but rather program fragments that get added to X-Plane while it runs. Because plugins run "inside" the simulator, they can accomplish things that a standalone program might not be able to. But since plugins are not full programs, they have limitations that normal programs do not.
Plugins can:
  * Run code from inside the simulator, either continually (for example, once per flight cycle) or in response to events inside the simulator. For example, a plugin can run constantly inside the simulator and log data to a file.
  * Read data from the simulator. For example, a plugin can continually read the values of the flight instruments and send them over the network.
  * Modify data in the simulator, changing its actions or behavior. For example, a plugin can change the aircraft's position, or replace the simulator's flight model entirely.
  * Create user interface inside the sim. For example, a plugin can create popup windows with instruments or dialog boxes.
  * Control various subsystems of the simulator. (These APIs are discussed in more detail below.)

Some parts of the simulator are controlled simply by writing data to the simulator. For example, to set the elevation of the cloud bases, we simply set that variable in the sim. Other subsystems require more complex interaction and have specific APIs. For example, there is an API to program the simulator's Flight Management System.
## Plugins and DLLs
Plugins are DLLs; an understanding of DLLs is necessary to understand how plugins work.
### About DLLs
A _dynamically linked library_ (DLL) is a set of compiled functions or procedures and their associated variables. DLLs are different from normal programs in a few key ways. A DLL does not contain a "main" function that is run once to run the entire program. Instead the DLL contains lots of functions, and another program _uses_ the DLL by calling these functions.
A DLL contains a directory of the functions inside. Other applications can read that directory to find the function they are looking for. The functions in this directory are known as the _exported_ functions because the DLL is said to "export" them to other programs. Not all of the functions in the DLL are exported; functions that are not exported are said to be _internal_. A program can only directly call the exported functions in a DLL, because it must find these functions through the directory. But the exported functions in the DLL can then call the internal functions.
DLLs are powerful because the program that uses the DLL finds and runs the code in the DLL when it is executed, not when it is compiled. Consider the case of a set of math functions for a calculator. If we simply compile the math functions into our program, then when we change the math programs (for example, to make them faster or more accurate), we have to recompile our whole program. If we know that the math functions are likely to change, we can put them into a DLL and have the calculator program find these functions when it runs. Since the calculator finds the math functions every time it runs, we can create a new DLL with the same function names but better implementations and substitute it for the old DLL. The calculator function will not care as long as the functions have the same names and take the same inputs. This allows us to upgrade or change the behavior of our calculator program without recompiling it.
The example above is a bit contrived, but what if one company makes the calculator program and another company makes the math functions? The company that makes the math functions might not have the code for the calculator and might not be able to recompile it, so building a single big application would not be acceptable. Furthermore, the company that makes the math functions might not want the company that makes the calculator to have their source code. DLLs address both of these problems. The company that makes the math functions can provide new math functions to the calculator company without needing to recompile the calculator. The company that makes the calculator can use the math functions without having access to their code.
A program uses a DLL by _linking_ to it. This happens when the program is executed. When a program links to a DLL, it examines the DLLs directory and finds all of the exported functions it needs. There are two kinds of linking: _hard-linking_ (also known as strong linking) and _weak_ _-linking_ (also known as soft linking). When a program is hard-linked to a DLL, the program must find every function it needs in the DLLs directory to run. If the DLL is missing functions, the program will not execute. When a program is weak-linked to a DLL, the program can run even if it does not find all of the functions it needs.
DLLs can also link to other DLLs. In this way a chain of DLLs can be created. For example, the company that makes the math functions might use yet another DLL by another company that just does some very basic calculations.
DLLs are not full programs; they do not run at the same time as programs, nor do they have their own memory. They are simply libraries of code. That code may share data with the host program if desired. For example, the calculator program may pass the math library the address of an internal buffer and then the math program may fill the buffer with numbers.
If multiple DLLs or programs link to one DLL, that one DLL that is being linked to is only loaded once, and may only have one set of global variables. This allows DLLs to communicate with each other. For example, several DLLs that the calculator program uses might all use one date-computing DLL that can figure out things like what day of the week it was three years ago. If that date-computing DLL has an internal variable for the current day of the week, all DLLs linking to it will see it. If one DLL changes that variable, the variable will be changed for all DLLs. In this way DLLs can communicate with each other by sharing the same data.
### Plugins and the Plugin Manager are DLLs
The X-Plane plugin system is based on DLLs. The central component of this system is the _X-Plane Plugin Manager_ (or XPLM). The XPLM is a library of code (compiled as a DLL!) that manages plugins. X-Plane links to the XPLM and all plugins link to the XPLM. The XPLM then serves as the central hub in the plugin system.
Plugins contain the code that will run inside the simulator. They export the functions that the XPLM can call, and the XPLM searches the directory of the plugin DLL to find them.
Likewise, the XPLM contains the functions that the _plugin_ needs to change the sim, read data, create user interface, etc. The XPLM exports these functions into its DLL directory so the plugin can find them and call them.
Plugins never communicate directly with X-Plane; they always goes through the XPLM. For all practical purposes, the XPLM _is_ the simulator to the plugin. Plugins also never talk to other plugins directly; instead, they sends messages via the XPLM. Communicating with other plugins is covered in the article "Working with Other Plugins." The details of how the XPLM interacts with the simulator are covered in the appendix "XPLM Architecture." We do not need to know how the XPLM and X-Plane interact to write a plugin.
 
## Plugin Concepts
### The Parts of the Plugin System
There are four main parts to the plug-in system:
  1. **X-Plane**. X-Plane is the host application. All plugins run within the memory space of X-Plane.
  2. **The X-Plane Plugin Manager** (XPLM). The XPLM is a DLL that acts as the central hub for all plugin activity. The XPLM communicates with X-Plane.
  3. **Plugins**. Plugins are DLLs that communicate with the XPLM. Plugins do not talk directly to the simulator. Plugins modify the simulator's behavior by calling functions within the XPLM library to make the simulator do things or to change the simualtor's data.
  4. **Other libraries**. Plugins can also use other libraries or DLLs that contain useful functionality.

### Plugin Signatures
Each plugin has a signature. The plugin tells the XPLM its signature when it is first initialized. This signature uniquely identifies the plugin and differentiates it from all other plugins. Signatures are built based on left-to-right keyword combining, starting with the organization responsible for creating the plug-in. For example, if XYZResearch made a plug-in to measure engine performance as part of its flight diagnostics package, it might use a signature like:
XYZResearch.diagnostics.engine_performance_meter
You can use any string you wish for the plugin's signature, but you should pick the first string based on the organization (be it commercial or for shareware) so that you don't accidentally pick the same name as another plugin. (If the XPLM ever encounters multiple plugins installed with the same signature, it will only load one of them.)
### Enabling Plugins
A plugin may be enabled or disabled. An enabled plugin will have its callbacks called (if it has any that need to be called), but a disabled callback will not. A disabled plugin cannot affect the sim since its callbacks are not called. Disabling provides a way for users to turn off a plugin that they do not want to use or that is being problematic. A dialog box in the simulator allows users to enable and disable plugins.
All plugins start out disabled when they are loaded. Then they are each enabled one by one when the sim is ready to start. Plugins are disabled before they are unloaded when the user quits the simulator. If a user previously disabled a plugin before quitting the simulator or disables all plugins on startup (by holding down the shift key while the simulator is started), plugins will start disabled and stay disabled until the user enables them.
A plugin will receive a callback when it is enabled and disabled, but does not necessarily need to do anything in response to this callback. Windows will automatically be hidden, menu items disabled, and timers paused when a plugin is disabled. Plugins that allocate other resources might want to free them when disabled. For example, if the plugin communicates with a server over the internet, it might want to disconnect from the server when disabled and reconnect when enabled.
## Anatomy of a Plugin
This section describes how a plugin is set up in detail.
A plugin is a DLL with a number of functions. Among these are various callbacks that the XPLM calls to notify the plugin of events, as well as any helper functions, utilities, etc. that the plugin needs.
Callbacks in plugins come in two flavors:
  1. _Required_ callbacks are functions that the plugin exports as a DLL. The XPLM finds these functions and calls them when appropriate. The plugin must export all five required callbacks below or else it will not load properly. You do not necessarily have to _do_ anything in the callbacks; they can be empty functions. The XPLM finds the required callbacks by searching the DLLs directory.
  2. _Registered_ callbacks are functions that you pass back to the XPLM to access additional simulator functionality. For example, if you create a window, you provide a callback function that will then be called when the user clicks in the window. The registered functions do not need to be exported from the DLL; instead, you pass a pointer to the function directly to the XPLM.

You can register callbacks from the required callbacks or from other callbacks. For example, you can register a callback that will be called in 5 minutes when the plugin is initialized. Then five minutes later in _that_ callback, you can create a window and register a callback for when the window is clicked.
Programming a plugin is different from programming a regular application. In a regular application, we write a main program that is run once from start to finish. In a plugin, lots of small functions are called at different times. In this way, programming a plugin is similar to event-driven UI programming.
### The Required Callbacks
There are five required callbacks that a plugin must implement, all of which are called by the XPLM:
  * **XPluginStart()**. This is called when the plugin is first loaded. You can use it to allocate any permanent resources and register any other callbacks you need. This is a good time to set up the user interface. This callback also returns the plugin's name, signature, and a description to the XPLM.
  * **XPluginEnable()**. This is called when the plugin is enabled. You do not need to do anything in this callback, but if you want, we can allocate resources that we only need while enabled.
  * **XPluginDisable()**. This is called when the plugin is disabled. You do not need to do anything in this callback, but if we want, you can deallocate resources that are only needed while enabled. Once disabled, the plugin may not run again for a very long time, so you should close any network connections that might time out otherwise.
  * **XPluginStop()**. This is called right before the plugin is unloaded. You should unregister all of the callbacks, release all resources, close all files, and generally clean up.
  * **XPluginReceiveMessage()**. This is called when a plugin or X-Plane sends the plugin a message. See the article on "Interplugin communication and messaging" for more information. The XPLM notifies you when events happen in the simulator (such as the user crashing the plane or selecting a new aircraft model) by calling this function.

### Registering Additional Callbacks
Many parts of the SDK API require you to register additional callbacks. A couple examples:
  * To create a menu item, you register a callback that will be called when the user clicks on the menu item. This callback can then do what the menu item says. (See the [XPLMMenus](/sdk/XPLMMenus/) header for more info.)
  * To create a window, you register a callback that will be called when the user clicks in the window. You'll also need to register a callback to handle drawing the window. (See [XPLMCreateWindowEx](/sdk/XPLMCreateWindowEx/)() for more info.)

You often want to register these additional callbacks from your XPluginStart() callback and unregister them from XPluginStop().
A typical session of the simulator might go like this:
  1. The user starts the simulator.
  2. The simulator (via the XPLM) loads the plugin and calls your XPluginStart().
  3. Your plugin creates a menu item and registers a callback for it.
  4. The simulator then calls your XPluginEnable() function to notify you that the plugin is now enabled.
  5. The user clicks your menu item, so the simulator calls your [XPLMMenuHandler_f](/sdk/XPLMMenuHandler_f/) callback.
  6. Your callback function then does something—for example, it might write some data to a file.
  7. The user quits the simulator.
  8. The simulator calls your XPluginDisable() function and then your XPluginStop() function.
  9. Your plugin DLL is unloaded and the simulator quits.

### How Plugins Interact with X-Plane
Plugins interact with the simulator by calling functions in the XPLM DLL. These functions are defined in the header files that start with XPLM. For instance:
  * If the plugin wants to read or write data from or to the simulator, it uses the APIs defined in [XPLMDataAccess](/sdk/XPLMDataAccess/).
  * If the plugin wants to create a user interface, it uses the APIs defined in [XPLMDisplay](/sdk/XPLMDisplay/).
  * If the plugin wants to execute a command (for example, pause the sim), it uses the APIs in [XPLMUtilities](/sdk/XPLMUtilities/).

For example, suppose you want to make a plugin that implements a pause-the-sim menu item. Your plugin would simply call [XPLMCommandKeyStroke](/sdk/XPLMCommandKeyStroke/)() with the constant xplm_key_pause to from within your menu callback.
See [the "Available APIs" section](http://developer.x-plane.com/sdk/#available-apis) of the SDK home page for a complete list of the available APIs.
### Limitations of Plugins
Plugins run synchronously inside the X-Plane process. This has a few basic important ramifications:
  * The plugin and the simulator will not be running at the same time. If the plugin is slow, the simulator's frame rate will slow down too.
  * The plugin is in the same process as the simulator; if the plugin crashes, it will crash the whole simulator. The plugin can scribble on simulator memory.
  * You cannot use the X-Plane SDK from another process.

All of these things may be overcome by writing additional code (for example, interprocess communication or threading code), but they are not part of the basic plugin system.
## Programming Idioms in the XPLM
A programming idiom is a style of coding that is used to accomplish certain goals. The plugin APIs are written in C to provide access to the widest range of languages possible. However, the design of the system is somewhat object-oriented. To accomplish this in a non-object-oriented language, we use the following idioms:
#### Opaque Handles
An opaque handle is a value used to identify an object created within the plug-in system. Opaque handles are usually defined as integers or _void_ pointers. The handles are 'opaque' because the plugin does not know what they mean or what internal structures they represent. You should never try to do anything with an opaque handle except pass it back to the SDK APIs.
The typical life of an object goes something like this: you first create the object and receive an opaque handle. You then make function calls passing in that handle to manipulate it. Finally, you call a function to free the object, passing the handle in again. From that point on the handle is no longer valid.
#### Callbacks
As discussed above, a callback is a function in the code that the simulator calls to accomplish specific behaviors. For example, when the simulator needs to draw the window, it calls the draw callback you specify when you create the window.
Typically callbacks are specified when an object is created and provide unique behaviors for that object. Providing a callback is similar to overriding a virtual function; it lets you customize an object's behavior.
#### Reference Values ("refcons")
All of the APIs that take callbacks also take "reference constants." These are pointers that will be passed back to the callback when the callback is called. This allows you to specify specific data of some kind with different uses of the callback. A few uses of refcons include:
  * For object-oriented programming languages, you can pass a pointer to an object that the callback should work on. When the callback is called, it will know what object to operate on.
  * For callbacks that provide behavior to multiple windows, you can provide a pointer to data for that specific window.
  * For callbacks that execute commands, you can specify the command using data of our choosing.

You don't have to use refcons (you can simply pass in NULL); they are provided only for convenience. Generally if the data the callback operates on is global, you won't need a refcon, but if multiple copies of that data exist per object, you will.
## Writing Plugins
This section describes the step-by-step process of making a simple plug-in.
### Creating a Plugin Project
The plugin SDK comes with a number of example plugins with associated sample projects. You can see the latest sample projects in [the "Sample Code" section](http://developer.x-plane.com/sdk/#sample-code) of the SDK home page.
#### Developing Plugins in C
Developing plugins in C is straight forward. Simply implement the required callback functions and write any additional callbacks. A few considerations:
  * The required callbacks must be exported from the DLL. The process of doing this is compiler-specific and OS-specific. For C and C++ on Microsoft Visual Studio, Xcode, and GCC, we provide the PLUGIN_API macro to make this easier.
  * You must include the XPLM headers to call the functions and link against XPLM_64.dll to build the DLL. You should do a hard dynamic link against XPLM_64.dll. You do _not_ have to use the _same_ XPLM_64.dll to link as runs in the sim.

#### Developing Plugins in C++
Developing plugins in C++ is similar to developing in C. One additional thing to note is that you have to be careful not name-mangle the required callbacks. Normally, a C++ compiler changes the function names into gibberish to distinguish between functions with the same name and different arguments. (The gibberish is different depending on the arguments to the function.) If the functions are name-mangled, X-Plane will not find them and will not load the plugin. To avoid this, use ["extern C" syntax](https://stackoverflow.com/questions/1041866/in-c-source-what-is-the-effect-of-extern-c). If you use the PLUGIN_API macro, this is done for you.
#### Developing Plugins in Another High-level Language
You can develop plugins in any other high level language that supports C calling conventions. Contact us to find out about support for a given language.
#### Developing Plugins for Mac
Plugins on macOS are libraries. Xcode can produce these libraries very easily, and examples are provided with the SDK.
When working on Mac, we should also link against CoreFoundation.framework and XPLM.framework (as well as any other frameworks you need, like [XPWidgets](/sdk/XPWidgets/).framework and OpenGL.framework).
#### Developing Plugins for PC
Plugins on Windows are DLLs. Visual Studio can produce DLLs very easily, and examples are provided with the SDK.
#### Developing Cross-Platform Plugins
X-Plane runs on Mac, Windows, and Linux. All functionality in the plugin system works cross-platform. You may be able to write a plugin that can be compiled and deployed on both platforms with the same source code. Here are some of the subsystems to use and their implications:
  * X-Plane interaction. All XPLM APIs are cross-platform.
  * File Access. Use the standard C libraries to access files.
  * Directory Structures. The XPLM APIs provide cross-platform file system directory searching and provide the platform-native directory separator. (See [XPLMUtilities](/sdk/XPLMUtilities/).)
  * Graphics and UI. All graphics and UI are done via OpenGL, and are therefore fully cross-platform
  * Networking. There is, at this time, no cross-platform threading API. Contact us for some possible solutions that are in the works.
  * Threading. There is, at this time, no cross-platform threading API.

If you are developing code for one platform that could be compiled on the other, contact us for possible help building cross-platform plugins.
### Analysis of a Sample Plugin
[The Hello World Sample Plugin](http://developer.x-plane.com/code-sample/hello-world-sdk-3/) provides complete projects for Windows, Mac, and Linux—go ahead and download the one appropriate to your platform and open it in a text editor.
The sample plugin provides all the required callbacks, and it registers a handful of other callbacks. Let's look at those in a bit more detail.
  * XPluginStart() 
    * Accomplishes two things: 
      * Provides information about the plugin (via the three strcpy() calls at the top of the function)
      * Creates a window, via [XPLMCreateWindowEx](/sdk/XPLMCreateWindowEx/)() 
        * Registers a handful of callbacks (params.drawWindowFunc and the five params.handleXXXFunc)
        * As is typical when writing plugin code, these callbacks are registered when creating an object (in this case, a window).
        * We do not provide a refcon value because our plugin will always do the same thing: it will always print "hello world."
    * Returns true to indicate it loaded successfully. (If we were to return false, due to our window being NULL, we would be unloaded immediately.)
  * XPluginStop() 
    * Cleans up the window we created (the window ID we received from [XPLMCreateWindowEx](/sdk/XPLMCreateWindowEx/)()).
    * This is  _technically_ unnecessary, because the SDK would  _forcibly_ destroy any windows we failed to clean up when our plugin is unloaded, but it's good practice.
  * XPluginEnable(), XPluginDisable(), and XPluginReceiveMessage() 
    * These "stub" callbacks must be provided, even though we don't need them to do anything.
  * The registered window drawing callback, draw_hello_world() 
    * Before any drawing can take place, we  _must_ call [XPLMSetGraphicsState](/sdk/XPLMSetGraphicsState/)(). (Failing to do so means you'll draw in whatever state the OpenGL driver was in last, which may be different between runs of the simulator, or even frame to frame.
    * We query the SDK for the window's bounds, in case it was moved or resized since last frame. (Since the window is styled like the default X-Plane 11 windows, it can be moved freely, and resized within the limits we set in our call to [XPLMSetWindowResizingLimits](/sdk/XPLMSetWindowResizingLimits/)() when we created the window.)
    * [XPLMDrawString](/sdk/XPLMDrawString/)() does the final drawing of the text; note that we  _could_ have used raw OpenGL code to implement this, but there's no need to since the SDK can handle text drawing.

## Guidelines for Plugin Design
Writing X-Plane plugins is different from writing regular applications. Plugins are guests inside X-Plane and misdesigned plugin can ruin simulator performance.
Plugins always operate synchronously with respect to each other; no parallelization or multithreading is provided. If we need threading or a separate process to maintain simulator performance while running, we must implement this ourselves. Xplane handles each plugin in a serial fashion, it never calls into more than one plugin at the same time. It calls them in a round robin fashion.
The following is a list of guidelines for designing plugins.
### Understand The Plugin's Resource Consumption
The most important first step to building a plugin is to understand what the costs of the plugin running will be. Consider:
  * Does my plugin have to do any computation that takes so long that it will degrade simulator performance by holding the sim off? Remember the simulator makes it through all of its operations in 50 ms at 20 fps. If we hold the simulator off by another 15 ms, we'll slow the simulator to 15 fps.
  * Does my plugin do computation that happens so frequently and takes so long that it takes a percentage of the CPU? For example, a 2 ms calculation done every 10 ms will consume 20% of the CPU immediately. To prevent a 20% slowdown of X-Plane, the task must run less frequently or take less time each time it runs.
  * Does my plugin do I/O that takes a long time to complete and/or must be done frequently?

Plugins share resources with the simulator. X-Plane normally uses all available CPU, memory, and graphics bandwidth in its operation (although it will be bounded by only one, varying from machine to machine). Any non-I/O resource consumption from the plugin directly takes away from the simulator.
### Structure Plugins as State Machines and Event Handlers
The plugin will be made up of a series of callbacks; any long processing will detract from simulator performance. If possible structure the plugin as a state machine and/or a series of event handlers. This allows you to control program flow without the code running for long uninterrupted periods.
### Drawing and UI Should Represent State
With conventional programs we can usually draw to the screen whenever it is convenient for us. For example, we can draw to the screen immediately when we receive a mouse click or finish computing a value.
With plugins this is not the case. The plugin can only draw in response to a draw request from the simulator. These callbacks occur at high frequency. For this reason, code that would normally draw in a conventional program needs to record data that will cause the draw handler to draw differently. The whole screen will be completely redrawn every sim frame, so the draw handler needs to keep drawing that data until we want it to disappear.
There are some advantages to programming like this. You do not need to worry about redrawing the user interface as events happen. It will be continually redrawn. The drawing code runs all the time, so the plugin will draw no slower for changing its graphics on a regular basis or animating.
### Minimize Time Spent in Any One Callback
While the callback is running, no other callback can run and the simulator cannot run. Try to minimize the time spent in callbacks to keep the simulator running rapidly. Break up slow segments of code.
### Minimize Busy Work
When possible base tasks on user inputs rather than timers because user events come less frequently (and only when the user is doing something). Try to use the longest timer periods that are acceptable. Having a callback run too frequently will waste CPU, X-Plane's most valuable resource. Don't schedule a callback to run every time the sim redraws unless we really need to run every time the sim draws (for example to set up the camera). When possible, base callbacks on real amounts of time. Disable callbacks that aren't needed.
### Poll for Data Only When Needed
Don't read data from the sim repeatedly if a cached copy will work. Reading data from the sim is very fast, but within one callback the data almost definitely cannot change.
### Cache Information to Achieve the Above Goals
Save values that are expensive to compute to keep callbacks fast. In particular, make sure you don't compute values unnecessarily in the draw callback, since it will run very very frequently. Cache values and recompute them when they would change, rather than recomputing them every time they are needed.
### I/O - Use Non-Blocking/Async/Overlapped I/O or Use a Separate Thread
If the plugin spends the majority of its time doing I/O (file, network, or otherwise), use APIs that do not wait for the I/O to complete before they return. Or create a separate thread that can call the I/O routines.
### Use Non-Blocking or Overlapped I/O or a Thread
If the plugin does a lot of I/O, consider using non-blocking or overlapped or asynchronous I/O calls to keep callbacks fast. Blocking in a callback for I/O blocks the entire simulator, hurting frame rate and wasting CPU cycles.
Another strategy is to use thread-blocking I/O and run a separate worker thread.
## Basic Plugin Reference
### Architecture
Plugins are implemented as DLLs. Plugins link against the plugin-manager (which is also a DLL) to call functions in the plugin API SDK that in turn manipulate the simulator. Plugins implement five callbacks via exported functions, and also can provide additional function pointers for registering callbacks via API calls.
### Plugin Build Environments
Mac plugins are libraries with the required plugins exported. They hard link against the XPLM.framework and other libraries if necessary. The main symbol is unused.
PC plugins are DLLs with the required plugins exported. The thread attach functions etc. are unused. They hard link against the XPLM_64.dll and other libraries if necessary.
### The Required Callbacks
There are five callbacks you must implement in the plugin via exported DLLs.
#### XPluginStart
    
    PLUGIN_API int XPluginStart(char * outName, char * outSignature, char * outDescription);
    
  * Description 
    * This function is called by X-Plane right after the plugin's DLL is loaded.
    * Do any initialization necessary for the plugin. This includes creating user interfaces, installing registered callbacks, allocating resources, etc.
    * Copy null-terminated C strings of less than 256 characters each into the three buffers passed in.
    * If you return success (1), the plugin will receive additional callbacks. If you return failure (0), the plugin will be unloaded immediately with no further callbacks. In the case of failure, the plugin will be in the disabled state after this call.
  * Arguments 
    * outName: a pointer to a buffer. Fill this buffer with the human-readable name of the plugin.
    * outSignature: a pointer to a buffer. Fill this buffer with the plugin's (globally unique) signature. By convention, start this plugin with the organization name to prevent collisions.
    * outDescription: a pointer to a buffer. If the plugin loads successfully, fill this buffer with a human-readable description of the plugin; if the plugin fails to load, fill it with a description of what went wrong.
  * Return value: 1 if the plugin loaded successfully, otherwise 0

#### XPluginStop
    
    PLUGIN_API void XPluginStop(void);
    
  * Description: 
    * This function is called by X-Plane right before the DLL is unloaded. The plugin will be disabled (if it was enabled) before this routine is called.
    * Unregister any callbacks that can be unregistered, dispose of any objects or resources, and clean up all allocations done by the plugin. After you return, the plugin's DLL will be unloaded.
  * Arguments: None
  * Return value: None

#### XPluginEnable
    
    PLUGIN_API int XPluginEnable(void);
    
  * Description: 
    * This function is called by X-Plane right before the plugin is enabled. Until the plugin is enabled, it will not receive any other callbacks and its UI will be hidden and/or disabled.
    * The plugin will be enabled after all plugins are loaded unless the plugin was disabled during the last X-Plane run (and this information was saved in preferences) or all plugins were disabled by the user on startup. If the user manually enables the plugin, this callback is also called. XPluginEnable() will not be called twice in a row without XPluginDisable() being called.
    * This callback should be used to allocate any resources that the plugin maintains while enabled. If the plugin launches threads, start the threads. If the plugin uses the network, begin network communications.
    * You should structure the resource usage of the plugin so that the plugin has minimal costs for running while it is disabled by allocating expensive resources when _enabled_ instead of when _loaded_.
  * Arguments: None
  * Return value: 1 if the plugin started successfully, otherwise 0

#### XPluginDisable
    
    PLUGIN_API void XPluginDisable(void);
    
  * Description: 
    * This function is called by X-Plane right before the plugin is disabled. When the plugin is disabled, it will not receive any other callbacks and its UI will be hidden and/or disabled.
    * The plugin will be disabled either before it is unloaded or after the user disables it. It will not be unloaded until after it is disabled.
    * Deallocate any significant resources and prepare to not receive any callbacks for a potentially long duration.
  * Arguments: None
  * Return value: None

#### XPluginReceiveMessage
    
    PLUGIN_API void XPluginReceiveMessage([XPLMPluginID](/sdk/XPLMPluginID/) inFrom, int inMessage, void * inParam);
    
  * Description: 
    * This function is called by the plugin manager when a message is sent to the plugin. You will receive both messages that are specifically routed to your plugin and messages that are broadcast to all plugins.
    * Specific messages are sent from X-Plane and are described in the plugin messaging documentation. The parameter passed varies depending on the message. Plugins may also define their own private messages to send. If you receive a message you do not recognize, you should just ignore it.
    * Note: in older versions of the SDK, inMessage was declared as type "long." The message has been, and always will be, a 32-bit signed integer under 32 and 64 bits for all platforms. The change of the C type from long to int is to make the headers safe for 64-bit compilers; int is 32 bits on all compilers that are compatible with X-Plane plugins, but the long data type is 32 or 64 bits depending on ABI and platform and compiler, and is thus not safe to use.
  * Arguments: 
    * inFrom: the ID of the plugin that sent the message.
    * inMessage: an integer indicating the message sent.
    * inParam: a pointer to data that is specific to the message.
  * Return value: None
