# Building and Installing Plugins - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

**Note** : This article is for plugin developers. If you're a user just trying to install a plugin, please see [the "Expanding X-Plane" section](http://www.x-plane.com/manuals/desktop/#expandingx-plane) of the X-Plane manual. (The exact method of installation depends on the plugin, but for many plugins, you can simply drop the plugin directory into your Resources\plugins\ directory.)
* * *
## Platform Choices & Decisions
X-Plane supports plugins for all three operating systems (macOS, Windows, and Linux). This article covers issues of building and packaging plugins.
### Which API to Use
There are three revisions of the X-Plane plugin API:
  * 1.0 API is supported by X-Plane 6.70 and newer.
  * 2.0 API is supported by X-Plane 9.00 and newer, is a superset of the 1.0 API
  * 2.1 API is supported by X-Plane 10.00 and newer, is a superset of the 2.0 API
  * 3.0 API is supported by X-Plane 11.10 and newer, is a superset of the 2.1 API

Plugins are almost the same for all APIs; a few exceptions for compiling and linking 2.0+ plugins will be noted.
You can use the latest SDK download to build your plugin no matter what version of the API you are targeting. By default your plugin will only be able to use 1.0 APIs and run on any version of X-Plane.
If you define the symbol XPLM200, then the 2.0 API will be available to your code, and your plugin will only work on X-Plane 9 and later.
Likewise, if you define the symbol XPLM210, you'll have access to the 2.1 API and your plugin will only work on X-Plane 10 and newer.
Finally, if you define the symbol XPLM300, you'll get access to the 3.0 API, and your plugin will work X-Plane 11.10 and newer.
**If you 're just starting out, we recommend using the SDK 3.0 API and defining XPLM200, XPLM210,  _and_ XPLM300.**
If you're building a backward-compatible plugin (say, for both X-Plane 10 and 11), you can link against the 2.0 API and use the [XPLMFindSymbol](/sdk/XPLMFindSymbol/)() utility to  _optionally_ fetch newer APIs. If they are not present, your plugin will receive a NULL return value and can take a fallback path.
### Plugin Containers
[Plugins are DLLs](http://developer.x-plane.com/?article=developing-plugins) (dynamically linked libraries). While these have different names on different operating systems, the concept is the same: code that is linked into X-Plane while it is running. We will use the term DLL generically to mean a shared library of the appropriate type for your chosen ABI. The container types are:
  * DLLs (.dll) for Windows
  * Shared Objects (.so) for Linux
  * Shared Dynamic Libraries (.dylib) for macOS

Note that in all of these cases the plugin's extension is .xpl, as dictated by SDK conventions, not the native platform.
#### Packaging Thin Plugins
A "thin" plugin is the original way of packaging plugins: the plugin consists of a single DLL with the extension .xpl, which is dropped into X-Plane's Resources\plugins\ directory. All auxiliary files (image files, etc.) must be kept outside the plugin. Thin plugins are the only packaging supported by the 1.0 API. (Originally thin plugins were just called "plugins".)
The obvious downsides of this are that you have to distribute a different plugin for each operating system, and you don't get a plugin "home" directory to pack your resources.
In order to build a thin plugin, make sure your plugin ends in the extension ".xpl".
#### Packaging Fat Plugins (XPLM 2.0 API)
A "fat" plugin is a folder containing one or more plugins. The plugins inside the folder have the specific names win.xpl, mac.xpl and lin.xpl. A fat plugin provides a container format that is portable across multiple operating systems; X-Plane loads only the plugin that is appropriate for the host computer and ignores the rest. The folder can also contain support files, allowing the user to install the plugin by dragging a single folder.
Fat plugins require the 2.0 API, and are the recommended way of packaging plugins that would require the 2.0 API or X-Plane 9 for other reasons.
To build a fat plugin, make sure the actual binary is inside a folder (which in turn is inside the plugins folder) and that the binary is named win.xpl for Windows, mac.xpl for OS X, or lin.xpl for Linux.
The recommended layout for a fat plugin is:
    
    plugin folder
     mac.xpl <- mac dylib with multiple code architectures: ppc, i386, x86_64
     32
     win.xpl <- 32-bit (win32) windows dll
     lin.xpl <- 32-bit (i386) Linux shared object
     optional other 32-bit dlls needed
     64
     win.xpl <- 64-bit (x64) windows dll
     lin.xpl <- 64-bit (x86_64) Linux shared object
     optional other 64-bit dlls needed
     other files for the plugin (pngs, etc.) in any sub folders as desired.
#### Packaging Fat Plugins (XPLM 2.1 API)
New in X-Plane 10 are the 32-bit and 64-bit variants of fat plugins. In this case, the plugin packaging looks like this:
  * my_plugin/ 
    * 64/ 
      * mac.xpl
      * win.xpl
      * lin.xpl
    * 32/ 
      * mac.xpl
      * win.xpl
      * lin.xpl

(Note that you don't need to ship both 32- and 64-bit variants of your plugins to use this packaging.)
#### Packaging Fat Plugins (XPLM 3.0 API)
As of X-Plane 11.10, we support [one more format](http://developer.x-plane.com/2017/09/new-plugin-packaging-for-the-sdk-3-0/) for packaging plugins. The downside to the 2.1 format is that all plugins are named [platform].xpl, which makes debugging tools hard, if not impossible, to use with shipping plugins. (They assume that all DLLs will have unique names, and therefore get confused if you have multiple plugins loaded that are both called, e.g., win.xpl.)
For that reason, as of XPLM 3.0, you can package plugins like this:
  * my_plugin/ 
    * mac_x64/ 
      * my_plugin.xpl
    * win_x64/ 
      * my_plugin.xpl
    * lin_x64/ 
      * my_plugin.xpl

(Note, of course, that X-Plane 11 supports  _only_ 64-bit plugins.)
### Global, Aircraft, or Scenery Plugin
A "global" plugin is one that is installed in the Resources\plugins\ folder. This is the original way to install a plugin and the only one supported by the 1.0 SDK. Global plugins must be installed directly into the plugins folder (which is in turn inside the Resources folder); sub-folders are not examined.
The 2.0 API also allows plugins to be stored with aircraft; such aircraft-based plugins are loaded when the user loads the plane (not counting multi-player use of the plane) and unloads the plugin when the user picks another aircraft.
The 2.1 API also allows plugins to be stored with a scenery pack; such scenery-based plugins are loaded at startup with the global plugins, so be sure to keep resource use to zero when the user is not in your scenery area.
Only fat plugins can be stored with an aircraft or scenery pack. An aircraft plugin goes in a "plugins" folder that in turn is inside the root folder of your aircraft package. A scenery plugin goes into a "plugins" folder that in turn is inside the root folder of your scenery package.
## Compiling Plugins
This section describes some of the issues when compiling plugins. This document is not meant to be substitute for the original documentation for your development tools, nor is it meant to be instructional in this regard. You should be able to use your chosen tool set to build DLLs before you begin writing plugins.
**The simplest way to compile your plugins is to start with[one of the examples](http://developer.x-plane.com/sdk/#sample-code)**, which come with project files and perform all the setup described below.
### Recommended Compilers
We do not recommend any particular compiler, but there are more SDK-related resources available for the compilers used to produce the basic examples. Those compilers are:
  * macOS: LLVM (via Xcode)
  * Windows: Visual Studio 2010 Express or Visual Studio 2015
  * Linux: GCC 4.x

The sample code provides templates that support these compilers. One way to get started is to download a sample and then replace the code while keeping the project.
### Setting Up Defined Macros
In order to use the X-Plane SDK headers, you must pre-define some macros before including any SDK headers. This is usually done by setting up the definitions (a.k.a. preprocessor macros) in your compiler settings. Most compilers accept the command-line option -D<symbol>=<value>; Xcode and Visual Studio both have project settings to predefine symbols, and in both cases they result in -D command-line options being sent to the compiler.
You must define one of the macros APL, IBM, or LIN to 1, depending on the OS you are compiling for. If no platform is defined, you will get a "platform not defined" compile error as soon as you include any SDK headers. Typically you would define the platform in the project settings or make file for each platform, so that the code can be shared between all three without modification.
#### Macros for the 2.0 and newer SDK APIs
In order to use the 2.0 APIs, you'll need to define the symbol XPLM200. To the the 2.1 APIs, you'll need to  _also_ define XPLM210, and for the 3.0 APIs, you'll need to define  _all three_ of XPLM200, XPLM210, and XPLM300.
(This feature is designed to let you build plugins that link against old versions of the SDK from the same SDK headers. If newer symbols are not defined, you cannot accidentally use a newer routine.)
### Including the SDK Headers
To use X-Plane SDK functionality, you must include the SDK headers, like this:
    
    #include <[XPLMProcessing](/sdk/XPLMProcessing/).h>
    
In order for this to work, you must tell your compiler where to locate the header files. You must first decide where to install the header files. There are two basic choices:
  * If you work on your projects by yourself, you can pick one location on your hard drive to place the SDK and use it for all of your projects. The advantage is you will only have one copy of the SDK to update in the future.

  * If you share your projects with other developers, it is important that the SDK location be the same for all developers, and be located relative to the project. (Otherwise all developers would need the same hard drive name.) In this case, it makes sense to copy the SDK headers and libraries into the source tree of each project.

Once you have decided on an install location, you must provide your installer with an include path that tells it where the headers are. For example,
    
    -IXPSDK/CHeaders/XPLM
    -IXPSDK/CHeaders/XPWidgets
    
Most compilers require one include path for each directory to be searched.
### OpenGL Considerations
OpenGL is not part of the SDK, but it is the main API for drawing from plugins, so you will almost certainly need it to create any kind of custom graphics. OpenGL deployment varies a bit by platform.
On macOS, OpenGL is a framework, and it is always available. Using OpenGL requires two steps:
  1. Add the framework to your project in Xcode (or use -framework OpenGL on the command line).
  2. Include the headers like this:

    
    #include <OpenGL/gl.h>
    
For Windows and Linux, include OpenGL like this:
    
    #include <GL/gl.h>
    
On Linux, you may have to first install a package, e.g.:
    
    sudo apt-get install freeglut3-dev
    
(Specific instructions for your distro may vary.)
### DLL Attach Functions for Windows
Windows requires a "DLLMain" function to be included in your code. It is essentially boilerplate, and typically doesn't have to do any useful work. Here is a sample DLLMain function:
    
    #if IBM
    #include <windows.h>
    BOOL APIENTRY DllMain( HANDLE hModule,
     DWORD ul_reason_for_call,
     LPVOID lpReserved
     )
    {
     switch (ul_reason_for_call)
     {
     case DLL_PROCESS_ATTACH:
     case DLL_THREAD_ATTACH:
     case DLL_THREAD_DETACH:
     case DLL_PROCESS_DETACH:
     break;
     }
     return TRUE;
    }
    #endif
    
### Symbol Visibility (GCC4 or higher)
For GCC-based environments (command-line Linux), the default behavior is to export all non-static C functions out of your plugin. This is not what you want, and can cause serious compatibility problems for 32-bit plugins. To get around this, use
    
    -fvisibility=hidden
    
on your compiling command line.
The macro PLUGIN_API (defined by [XPLMDefs](/sdk/XPLMDefs/).h) automatically marks a symbol as exported, so yo can do this:
    
    PLUGIN_API void XPluginStop() { /* ... */ }
    
and your plugin will work correctly.
## Linking Plugins
Linking is the process of taking your compiled code and making an actual DLL file on disk that is your plugin.
### Linking on Windows
You will need to link against XPLM.lib, found in the SDK under SDK/Libraries/Win. Link against:
  * 32bit plugins: [XPWidgets](/sdk/XPWidgets/).lib, XPLM.lib
  * 64bit plugins: XPWidgets_64.lib, XPLM_64.lib

If you are using OpenGL, you'll also want to link against OpenGL32.lib.
We recommend you set all MSVC settings to avoid depending on external DLLs other than the CRT runtime; these DLLs may not be present on destination machines, and can cause your plugin load to fail. Linking the CRT runtime statically however will cause problems for users who have a lot of plugins installed and run out of TLS slots when loading plugins.
  * C++ Code Generation: set the runtime to "multi-threaded DLL", not "multi-threaded."
  * General: do not use common language runtime support or MFC.

### Linking on macOS
You will need to add XPLM.framework and [XPWidgets](/sdk/XPWidgets/).framework from the SDK to your plugin; you may also need to add the system frameworks OpenGL and possibly System or CoreFoundation to your plugin depending on what Mac settings you use.
**Do not** use the options "-undefined_warning" or "-flat_namespace" - these are no longer needed and not recommended.
 
### Linking on Linux
There are no link libraries on Linux for the SDK; instead pass the command-line option
    
    -shared -rdynamic -nodefaultlibs -undefined_warning
    
to the linker. This will let you link despite not having XPLM symbols defined. To include libraries like OpenGL use this:
    
    -lGL -lGLU
    
Finally, you may need to use a linker-script for your 32-bit plugin to avoid symbol collisions. To do this, create a file like this:
    
    {
     global:
     XPluginStart;
     XPluginStop;
     XPluginEnable;
     XPluginDisable;
     XPluginReceiveMessage;
     local:
     *;
    };
    
and then refer to it on the command line like this:
    
    -Wl,--version-script=<text file>
