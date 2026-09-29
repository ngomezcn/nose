# XPLMPlugin

Source: https://developer.x-plane.com/  (mirrored locally)

# [XPLMPlugin](/sdk/XPLMPlugin/) API
These APIs provide facilities to find and work with other plugins and manage other plugins.
## FINDING PLUGINS
These APIs allow you to find another plugin or yourself, or iterate across all plugins. For example, if you wrote an FMS plugin that needed to talk to an autopilot plugin, you could use these APIs to locate the autopilot plugin.
### [XPLMGetMyID](/sdk/XPLMGetMyID/)
    
    XPLM_API [XPLMPluginID](/sdk/XPLMPluginID/) [XPLMGetMyID](/sdk/XPLMGetMyID/)(void);
    
This routine returns the plugin ID of the calling plug-in. Call this to get your own ID.
### [XPLMCountPlugins](/sdk/XPLMCountPlugins/)
    
    XPLM_API int        [XPLMCountPlugins](/sdk/XPLMCountPlugins/)(void);
    
This routine returns the total number of plug-ins that are loaded, both disabled and enabled.
### [XPLMGetNthPlugin](/sdk/XPLMGetNthPlugin/)
    
    XPLM_API [XPLMPluginID](/sdk/XPLMPluginID/) [XPLMGetNthPlugin](/sdk/XPLMGetNthPlugin/)(
                             int                  inIndex);
    
This routine returns the ID of a plug-in by index. Index is 0 based from 0 to [XPLMCountPlugins](/sdk/XPLMCountPlugins/)-1, inclusive. Plugins may be returned in any arbitrary order.
### [XPLMFindPluginByPath](/sdk/XPLMFindPluginByPath/)
    
    XPLM_API [XPLMPluginID](/sdk/XPLMPluginID/) [XPLMFindPluginByPath](/sdk/XPLMFindPluginByPath/)(
                             const char *         inPath);
    
This routine returns the plug-in ID of the plug-in whose file exists at the passed in absolute file system path. [XPLM_NO_PLUGIN_ID](/sdk/XPLM_NO_PLUGIN_ID/) is returned if the path does not point to a currently loaded plug-in.
### [XPLMFindPluginBySignature](/sdk/XPLMFindPluginBySignature/)
    
    XPLM_API [XPLMPluginID](/sdk/XPLMPluginID/) [XPLMFindPluginBySignature](/sdk/XPLMFindPluginBySignature/)(
                             const char *         inSignature);
    
This routine returns the plug-in ID of the plug-in whose signature matches what is passed in or [XPLM_NO_PLUGIN_ID](/sdk/XPLM_NO_PLUGIN_ID/) if no running plug-in has this signature. Signatures are the best way to identify another plug-in as they are independent of the file system path of a plug-in or the human-readable plug-in name, and should be unique for all plug-ins. Use this routine to locate another plugin that your plugin interoperates with
### [XPLMGetPluginInfo](/sdk/XPLMGetPluginInfo/)
    
    XPLM_API void       [XPLMGetPluginInfo](/sdk/XPLMGetPluginInfo/)(
                             [XPLMPluginID](/sdk/XPLMPluginID/)         inPlugin,
                             char *               outName,    /* Can be NULL */
                             char *               outFilePath,    /* Can be NULL */
                             char *               outSignature,    /* Can be NULL */
                             char *               outDescription);    /* Can be NULL */
    
This routine returns information about a plug-in. Each parameter should be a pointer to a buffer of at least 256 characters, or NULL to not receive the information.
outName - the human-readable name of the plug-in. outFilePath - the absolute file path to the file that contains this plug-in. outSignature - a unique string that identifies this plug-in. outDescription - a human-readable description of this plug-in.
## ENABLING/DISABLING PLUG-INS
These routines are used to work with plug-ins and manage them. Most plugins will not need to use these APIs.
### [XPLMIsPluginEnabled](/sdk/XPLMIsPluginEnabled/)
    
    XPLM_API int        [XPLMIsPluginEnabled](/sdk/XPLMIsPluginEnabled/)(
                             [XPLMPluginID](/sdk/XPLMPluginID/)         inPluginID);
    
Returns whether the specified plug-in is enabled for running.
### [XPLMEnablePlugin](/sdk/XPLMEnablePlugin/)
    
    XPLM_API int        [XPLMEnablePlugin](/sdk/XPLMEnablePlugin/)(
                             [XPLMPluginID](/sdk/XPLMPluginID/)         inPluginID);
    
This routine enables a plug-in if it is not already enabled. It returns 1 if the plugin was enabled or successfully enables itself, 0 if it does not. Plugins may fail to enable (for example, if resources cannot be acquired) by returning 0 from their XPluginEnable callback.
### [XPLMDisablePlugin](/sdk/XPLMDisablePlugin/)
    
    XPLM_API void       [XPLMDisablePlugin](/sdk/XPLMDisablePlugin/)(
                             [XPLMPluginID](/sdk/XPLMPluginID/)         inPluginID);
    
This routine disables an enabled plug-in.
### [XPLMReloadPlugins](/sdk/XPLMReloadPlugins/)
    
    XPLM_API void       [XPLMReloadPlugins](/sdk/XPLMReloadPlugins/)(void);
    
This routine reloads all plug-ins. Once this routine is called and you return from the callback you were within (e.g. a menu select callback) you will receive your XPluginDisable and XPluginStop callbacks and your DLL will be unloaded, then the start process happens as if the sim was starting up.
## INTERPLUGIN MESSAGING
Plugin messages are defined as 32-bit integers. Messages below 0x00FFFFFF are reserved for X-Plane and the plugin SDK.
Messages come with a pointer parameter; the meaning of this pointer depends on the message itself. In some messages, the pointer parameter contains an actual typed pointer to data that can be inspected in the plugin; in these cases the documentation will state that the parameter "points to" information.
in other cases, the value of the pointer is actually an integral number stuffed into the pointer's storage. In these second cases, the pointer parameter needs to be cast, not dereferenced. In these caess, the documentation will state that the parameter "contains" a value, which will always be an integral type.
Some messages don't use the pointer parameter - in this case your plugin should ignore it.
Messages have two conceptual uses: notifications and commands. Commands are sent from one plugin to another to induce behavior; notifications are sent from one plugin to all others for informational purposes. It is important that commands and notifications not have the same values because this could cause a notification sent by one plugin to accidentally induce a command in another.
By convention, plugin-defined notifications should have the high bit set (e.g. be greater or equal to unsigned 0x8000000) while commands should have this bit be cleared.
The following messages are sent to your plugin by X-Plane.
### [XPLM_MSG_PLANE_CRASHED](/sdk/XPLM_MSG_PLANE_CRASHED/)
    
    #define [XPLM_MSG_PLANE_CRASHED](/sdk/XPLM_MSG_PLANE_CRASHED/) 101
This message is sent to your plugin whenever the user's plane crashes. The parameter is ignored.
### [XPLM_MSG_PLANE_LOADED](/sdk/XPLM_MSG_PLANE_LOADED/)
    
    #define [XPLM_MSG_PLANE_LOADED](/sdk/XPLM_MSG_PLANE_LOADED/) 102
This message is sent to your plugin whenever a new plane is loaded. The parameter contains the index number of the plane being loaded; 0 indicates the user's plane. The parameter is an integer bit-cast to a pointer.
### [XPLM_MSG_AIRPORT_LOADED](/sdk/XPLM_MSG_AIRPORT_LOADED/)
    
    #define [XPLM_MSG_AIRPORT_LOADED](/sdk/XPLM_MSG_AIRPORT_LOADED/) 103
This messages is sent whenever the user's plane is positioned at a new airport. The parameter is ignored.
### [XPLM_MSG_SCENERY_LOADED](/sdk/XPLM_MSG_SCENERY_LOADED/)
    
    #define [XPLM_MSG_SCENERY_LOADED](/sdk/XPLM_MSG_SCENERY_LOADED/) 104
This message is sent whenever new scenery is loaded. Use datarefs to determine the new scenery files that were loaded. The parameter is ignored.
### [XPLM_MSG_AIRPLANE_COUNT_CHANGED](/sdk/XPLM_MSG_AIRPLANE_COUNT_CHANGED/)
    
    #define [XPLM_MSG_AIRPLANE_COUNT_CHANGED](/sdk/XPLM_MSG_AIRPLANE_COUNT_CHANGED/) 105
This message is sent whenever the user adjusts the number of X-Plane aircraft models. You must use XPLMCountPlanes to find out how many planes are now available. This message will only be sent in XP7 and higher because in XP6 the number of aircraft is not user-adjustable. The parameter is ignored.
### [XPLM_MSG_PLANE_UNLOADED](/sdk/XPLM_MSG_PLANE_UNLOADED/)
    
    #define [XPLM_MSG_PLANE_UNLOADED](/sdk/XPLM_MSG_PLANE_UNLOADED/) 106
This message is sent to your plugin whenever a plane is unloaded. The parameter contains the index number of the plane being unloaded; 0 indicates the user's plane. The parameter is of type int, bit-cast to a pointer.
### [XPLM_MSG_WILL_WRITE_PREFS](/sdk/XPLM_MSG_WILL_WRITE_PREFS/)
    
    #define [XPLM_MSG_WILL_WRITE_PREFS](/sdk/XPLM_MSG_WILL_WRITE_PREFS/) 107
This message is sent to your plugin right before X-Plane writes its preferences file. You can use this for two purposes: to write your own preferences, and to modify any datarefs to influence preferences output. For example, if your plugin temporarily modifies saved preferences, you can put them back to their default values here to avoid having the tweaks be persisted if your plugin is not loaded on the next invocation of X-Plane. The parameter is ignored.
### [XPLM_MSG_LIVERY_LOADED](/sdk/XPLM_MSG_LIVERY_LOADED/)
    
    #define [XPLM_MSG_LIVERY_LOADED](/sdk/XPLM_MSG_LIVERY_LOADED/) 108
This message is sent to your plugin right after a livery is loaded for an airplane. You can use this to check the new livery (via datarefs) and react accordingly. The parameter contains the index number of the aircraft whose livery is changing. The parameter is an integer, bit-cast to a pointer.
### [XPLM_MSG_ENTERED_VR](/sdk/XPLM_MSG_ENTERED_VR/)
    
    #define [XPLM_MSG_ENTERED_VR](/sdk/XPLM_MSG_ENTERED_VR/)  109
Sent to your plugin right before X-Plane enters virtual reality mode (at which time any windows that are not positioned in VR mode will no longer be visible to the user). The parameter is unused and should be ignored.
### [XPLM_MSG_EXITING_VR](/sdk/XPLM_MSG_EXITING_VR/)
    
    #define [XPLM_MSG_EXITING_VR](/sdk/XPLM_MSG_EXITING_VR/)  110
Sent to your plugin right before X-Plane leaves virtual reality mode (at which time you may want to clean up windows that are positioned in VR mode). The parameter is unused and should be ignored.
### [XPLM_MSG_RELEASE_PLANES](/sdk/XPLM_MSG_RELEASE_PLANES/)
    
    #define [XPLM_MSG_RELEASE_PLANES](/sdk/XPLM_MSG_RELEASE_PLANES/) 111
Sent to your plugin if another plugin wants to take over AI planes. If you are a synthetic traffic provider, that probably means a plugin for an online network has connected and wants to supply aircraft flown by real humans and you should cease to provide synthetic traffic. If however you are providing online traffic from real humans, you probably don't want to disconnect, in which case you just ignore this message. The sender is the plugin ID of the plugin asking for control of the planes now. You can use it to find out who is requesting and whether you should yield to them. Synthetic traffic providers should always yield to online networks. The parameter is unused and should be ignored. Do not send this message directly; always use the [XPLMAcquirePlanes](/sdk/XPLMAcquirePlanes/)() call.
### [XPLM_MSG_FMOD_BANK_LOADED](/sdk/XPLM_MSG_FMOD_BANK_LOADED/)
    
    #define [XPLM_MSG_FMOD_BANK_LOADED](/sdk/XPLM_MSG_FMOD_BANK_LOADED/) 112
Sent to your plugin after FMOD sound banks are loaded. The parameter is the [XPLMBankID](/sdk/XPLMBankID/) enum in [XPLMSound](/sdk/XPLMSound/).h, 0 for the master bank and 1 for the radio bank. The bank ID is bit-cast to a pointer.
### [XPLM_MSG_FMOD_BANK_UNLOADING](/sdk/XPLM_MSG_FMOD_BANK_UNLOADING/)
    
    #define [XPLM_MSG_FMOD_BANK_UNLOADING](/sdk/XPLM_MSG_FMOD_BANK_UNLOADING/) 113
Sent to your plugin before FMOD sound banks are unloaded. Any associated resources should be cleaned up at this point. The parameter is the [XPLMBankID](/sdk/XPLMBankID/) enum in [XPLMSound](/sdk/XPLMSound/).h, 0 for the master bank and 1 for the radio bank. The bank ID is bit-cast to a pointer.
### [XPLM_MSG_DATAREFS_ADDED](/sdk/XPLM_MSG_DATAREFS_ADDED/)
    
    #define [XPLM_MSG_DATAREFS_ADDED](/sdk/XPLM_MSG_DATAREFS_ADDED/) 114
Sent to your plugin per-frame (at-most) when/if datarefs are added. It will include the new data ref total count so that your plugin can keep a local cache of the total, see what's changed and know which ones to inquire about if it cares.
This message is only sent to plugins that enable the XPLM_WANTS_DATAREF_NOTIFICATIONS feature. The parameteter is a pointer to a 32-bit integer containing the new number of datarefs.
### [XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)
    
    XPLM_API void       [XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)(
                             [XPLMPluginID](/sdk/XPLMPluginID/)         inPlugin,
                             int                  inMessage,
                             void *               inParam);
    
This function sends a message to another plug-in or X-Plane. Pass [XPLM_NO_PLUGIN_ID](/sdk/XPLM_NO_PLUGIN_ID/) to broadcast to all plug-ins. Only enabled plug-ins with a message receive function receive the message.
## Plugin Features API
The plugin features API allows your plugin to "sign up" for additional capabilities and plugin system features that are normally disabled for backward compatibility or performance. This allows advanced plugins to "opt-in" to new behavior.
Each feature is defined by a permanent string name. The feature string names will vary with the particular installation of X-Plane, so plugins should not expect a feature to be guaranteed present.
## XPLM_WANTS_REFLECTIONS
Available in the SDK 2.0 and later for X-Plane 9, enabling this capability causes your plugin to receive drawing hook callbacks when X-Plane builds its off-screen reflection and shadow rendering passes. Plugins should enable this and examine the dataref sim/graphics/view/plane_render_type to determine whether the drawing callback is for a reflection, shadow calculation, or the main screen. Rendering can be simlified or omitted for reflections, and non-solid drawing should be skipped for shadow calculations.
**Note** : direct drawing via draw callbacks is not recommended; use the [XPLMInstance](/sdk/XPLMInstance/) API to create object models instead.
## XPLM_USE_NATIVE_PATHS
available in the SDK 2.1 and later for X-Plane 10, this modifies the plugin system to use Unix-style paths on all operating systems. With this enabled:
  * OS X paths will match the native OS X Unix.
  * Windows will use forward slashes but preserve C:\ or another drive letter when using complete file paths.
  * Linux uses its native file system path scheme.

Without this enabled:
  * OS X will use CFM file paths separated by a colon.
  * Windows will use back-slashes and conventional DOS paths.
  * Linux uses its native file system path scheme.

All plugins should enable this feature on OS X to access the native file system.
## XPLM_USE_NATIVE_WIDGET_WINDOWS
Available in the SDK 3.0.2 SDK, this capability tells the widgets library to use new, modern X-Plane backed [XPLMDisplay](/sdk/XPLMDisplay/) windows to anchor all widget trees. Without it, widgets will always use legacy windows.
Plugins should enable this to allow their widget hierarchies to respond to the user's UI size settings and to map widget-based windwos to a VR HMD.
Before enabling this, make sure any custom widget code in your plugin is prepared to cope with the UI coordinate system not being th same as the OpenGL window coordinate system.
## XPLM_WANTS_DATAREF_NOTIFICATIONS
Available in the SDK 4.0.0, this capability tells X-Plane to to send the enabling plugin the new [XPLM_MSG_DATAREFS_ADDED](/sdk/XPLM_MSG_DATAREFS_ADDED/) message any time new datarefs are added. The SDK will coalesce consecutive dataref registrations to minimize the number of messages sent.
### [XPLMFeatureEnumerator_f](/sdk/XPLMFeatureEnumerator_f/)
    
    typedef void (* [XPLMFeatureEnumerator_f](/sdk/XPLMFeatureEnumerator_f/))(
                             const char *         inFeature,
                             void *               inRef);
    
You pass an [XPLMFeatureEnumerator_f](/sdk/XPLMFeatureEnumerator_f/) to get a list of all features supported by a given version running version of X-Plane. This routine is called once for each feature.
### [XPLMHasFeature](/sdk/XPLMHasFeature/)
    
    XPLM_API int        [XPLMHasFeature](/sdk/XPLMHasFeature/)(
                             const char *         inFeature);
    
This returns 1 if the given installation of X-Plane supports a feature, or 0 if it does not.
### [XPLMIsFeatureEnabled](/sdk/XPLMIsFeatureEnabled/)
    
    XPLM_API int        [XPLMIsFeatureEnabled](/sdk/XPLMIsFeatureEnabled/)(
                             const char *         inFeature);
    
This returns 1 if a feature is currently enabled for your plugin, or 0 if it is not enabled. It is an error to call this routine with an unsupported feature.
### [XPLMEnableFeature](/sdk/XPLMEnableFeature/)
    
    XPLM_API void       [XPLMEnableFeature](/sdk/XPLMEnableFeature/)(
                             const char *         inFeature,
                             int                  inEnable);
    
This routine enables or disables a feature for your plugin. This will change the running behavior of X-Plane and your plugin in some way, depending on the feature.
### [XPLMEnumerateFeatures](/sdk/XPLMEnumerateFeatures/)
    
    XPLM_API void       [XPLMEnumerateFeatures](/sdk/XPLMEnumerateFeatures/)(
                             [XPLMFeatureEnumerator_f](/sdk/XPLMFeatureEnumerator_f/) inEnumerator,
                             void *               inRef);
    
This routine calls your enumerator callback once for each feature that this running version of X-Plane supports. Use this routine to determine all of the features that X-Plane can support.
