# XPLMProcessing

Source: https://developer.x-plane.com/  (mirrored locally)

# [XPLMProcessing](/sdk/XPLMProcessing/) API
This API allows you to get regular callbacks during the flight loop, the part of X-Plane where the plane's position calculates the physics of flight, etc. Use these APIs to accomplish periodic tasks like logging data and performing I/O.
You can receive a callback either just before or just after the per-frame physics calculations happen - you can use post-flightmodel callbacks to "patch" the flight model after it has run.
If the user has set the number of flight model iterations per frame greater than one your plugin will _not_ see this; these integrations run on the sub-section of the flight model where iterations improve responsiveness (e.g. physical integration, not simple systems tracking) and are thus opaque to plugins.
Flight loop scheduling, when scheduled by time, is scheduled by a "first callback after the deadline" schedule, e.g. your callbacks will always be slightly late to ensure that we don't run faster than your deadline.
WARNING: Do NOT use the post-flightmodel callback for initialization, resource creation, etc. The only recommended use of post-FM callbacks is to "patch" the computed values of the flightmodel using dataref read-writes, and to compute custom system values by reading the flightmodel. APIs that create resources or initialize the sim may issue warnings, crash rhe sim, or have unexpected results.
WARNING: Do NOT use these callbacks to draw! You cannot draw during flight loop callbacks. Use the drawing callbacks (see [XPLMDisplay](/sdk/XPLMDisplay/) for more info) for graphics or the [XPLMInstance](/sdk/XPLMInstance/) functions for aircraft or models. (One exception: you can use a post-flight loop callback to update your own off-screen FBOs.)
## FLIGHT LOOP CALLBACKS
### [XPLMFlightLoopPhaseType](/sdk/XPLMFlightLoopPhaseType/)
You can register a flight loop callback to run either before or after the flight model is integrated by X-Plane.
Name| Value| Description  
---|---|---  
[xplm_FlightLoop_Phase_BeforeFlightModel](/sdk/xplm_FlightLoop_Phase_BeforeFlightModel/)| "0"|  Your callback runs before X-Plane integrates the flight model.  
[xplm_FlightLoop_Phase_AfterFlightModel](/sdk/xplm_FlightLoop_Phase_AfterFlightModel/)| "1"|  Your callback runs after X-Plane integrates the flight model.  
### [XPLMFlightLoopID](/sdk/XPLMFlightLoopID/)
    
    typedef void * [XPLMFlightLoopID](/sdk/XPLMFlightLoopID/);
This is an opaque identifier for a flight loop callback. You can use this identifier to easily track and remove your callbacks, or to use the new flight loop APIs.
### [XPLMFlightLoop_f](/sdk/XPLMFlightLoop_f/)
    
    typedef float (* [XPLMFlightLoop_f](/sdk/XPLMFlightLoop_f/))(
                             float                inElapsedSinceLastCall,
                             float                inElapsedTimeSinceLastFlightLoop,
                             int                  inCounter,
                             void *               inRefcon);
    
This is your flight loop callback. Each time the flight loop is iterated through, you receive this call at the end.
Flight loop callbacks receive a number of input timing parameters. These input timing parameters are not particularly useful; you may need to track your own timing data (e.g. by reading datarefs). The input parameters are:
  * inElapsedSinceLastCall: the wall time since your last callback.
  * inElapsedTimeSinceLastFlightLoop: the wall time since any flight loop was dispatched.
  * inCounter: a monotonically increasing counter, bumped once per flight loop dispatch from the sim.
  * inRefcon: your own pointer constant provided when you registered yor callback.

Your return value controls when you will next be called.
  * Return 0 to stop receiving callbacks.
  * Return a positive number to specify how many seconds until the next callback. (You will be called at or after this time, not before.)
  * Return a negative number to specify how many loops must go by until you are called. For example, -1.0 means call me the very next loop.

Try to run your flight loop as infrequently as is practical, and suspend it (using return value 0) when you do not need it; lots of flight loop callbacks that do nothing lowers X-Plane's frame rate.
Your callback will NOT be unregistered if you return 0; it will merely be inactive.
### [XPLMCreateFlightLoop_t](/sdk/XPLMCreateFlightLoop_t/)
[XPLMCreateFlightLoop_t](/sdk/XPLMCreateFlightLoop_t/) contains the parameters to create a new flight loop callback. The structure may be expanded in future SDKs - always set structSize to the size of your structure in bytes.
    
    typedef struct {
         int                       structSize;
         [XPLMFlightLoopPhaseType](/sdk/XPLMFlightLoopPhaseType/)   phase;
         [XPLMFlightLoop_f](/sdk/XPLMFlightLoop_f/)          callbackFunc;
         void *                    refcon;
    } [XPLMCreateFlightLoop_t](/sdk/XPLMCreateFlightLoop_t/);
### [XPLMGetElapsedTime](/sdk/XPLMGetElapsedTime/)
    
    XPLM_API float      [XPLMGetElapsedTime](/sdk/XPLMGetElapsedTime/)(void);
    
This routine returns the elapsed time since the sim started up in decimal seconds. This is a wall timer; it keeps counting upward even if the sim is pasued.
**WARNING** : [XPLMGetElapsedTime](/sdk/XPLMGetElapsedTime/) is not a very good timer! It lacks precision in both its data type and its source. Do not attempt to use it for timing critical applications like network multiplayer.
### [XPLMGetCycleNumber](/sdk/XPLMGetCycleNumber/)
    
    XPLM_API int        [XPLMGetCycleNumber](/sdk/XPLMGetCycleNumber/)(void);
    
This routine returns a counter starting at zero for each sim cycle computed/video frame rendered.
### [XPLMRegisterFlightLoopCallback](/sdk/XPLMRegisterFlightLoopCallback/)
    
    XPLM_API void       [XPLMRegisterFlightLoopCallback](/sdk/XPLMRegisterFlightLoopCallback/)(
                             [XPLMFlightLoop_f](/sdk/XPLMFlightLoop_f/)     inFlightLoop,
                             float                inInterval,
                             void *               inRefcon);
    
This routine registers your flight loop callback. Pass in a pointer to a flight loop function and a refcon (an optional reference value determined by you). inInterval defines when you will be called. Pass in a positive number to specify seconds from registration time to the next callback. Pass in a negative number to indicate when you will be called (e.g. pass -1 to be called at the next cylcle). Pass 0 to not be called; your callback will be inactive.
(This legacy function only installs pre-flight-loop callbacks; use [XPLMCreateFlightLoop](/sdk/XPLMCreateFlightLoop/) for more control.)
### [XPLMUnregisterFlightLoopCallback](/sdk/XPLMUnregisterFlightLoopCallback/)
    
    XPLM_API void       [XPLMUnregisterFlightLoopCallback](/sdk/XPLMUnregisterFlightLoopCallback/)(
                             [XPLMFlightLoop_f](/sdk/XPLMFlightLoop_f/)     inFlightLoop,
                             void *               inRefcon);
    
This routine unregisters your flight loop callback. Do NOT call it from your flight loop callback. Once your flight loop callback is unregistered, it will not be called again.
Only use this on flight loops registered via [XPLMRegisterFlightLoopCallback](/sdk/XPLMRegisterFlightLoopCallback/).
### [XPLMSetFlightLoopCallbackInterval](/sdk/XPLMSetFlightLoopCallbackInterval/)
    
    XPLM_API void       [XPLMSetFlightLoopCallbackInterval](/sdk/XPLMSetFlightLoopCallbackInterval/)(
                             [XPLMFlightLoop_f](/sdk/XPLMFlightLoop_f/)     inFlightLoop,
                             float                inInterval,
                             int                  inRelativeToNow,
                             void *               inRefcon);
    
This routine sets when a callback will be called. Do NOT call it from your callback; use the return value of the callback to change your callback interval from inside your callback.
inInterval is formatted the same way as in [XPLMRegisterFlightLoopCallback](/sdk/XPLMRegisterFlightLoopCallback/); positive for seconds, negative for cycles, and 0 for deactivating the callback. If inRelativeToNow is 1, times are from the time of this call; otherwise they are from the time the callback was last called (or the time it was registered if it has never been called.
### [XPLMCreateFlightLoop](/sdk/XPLMCreateFlightLoop/)
    
    XPLM_API [XPLMFlightLoopID](/sdk/XPLMFlightLoopID/) [XPLMCreateFlightLoop](/sdk/XPLMCreateFlightLoop/)(
                             [XPLMCreateFlightLoop_t](/sdk/XPLMCreateFlightLoop_t/) * inParams);
    
This routine creates a flight loop callback and returns its ID. The flight loop callback is created using the input param struct, and is inited to be unscheduled.
### [XPLMDestroyFlightLoop](/sdk/XPLMDestroyFlightLoop/)
    
    XPLM_API void       [XPLMDestroyFlightLoop](/sdk/XPLMDestroyFlightLoop/)(
                             [XPLMFlightLoopID](/sdk/XPLMFlightLoopID/)     inFlightLoopID);
    
This routine destroys a flight loop callback by ID. Only call it on flight loops created with the newer [XPLMCreateFlightLoop](/sdk/XPLMCreateFlightLoop/) API.
### [XPLMScheduleFlightLoop](/sdk/XPLMScheduleFlightLoop/)
    
    XPLM_API void       [XPLMScheduleFlightLoop](/sdk/XPLMScheduleFlightLoop/)(
                             [XPLMFlightLoopID](/sdk/XPLMFlightLoopID/)     inFlightLoopID,
                             float                inInterval,
                             int                  inRelativeToNow);
    
This routine schedules a flight loop callback for future execution. If inInterval is negative, it is run in a certain number of frames based on the absolute value of the input. If the interval is positive, it is a duration in seconds.
If inRelativeToNow is true, times are interpreted relative to the time this routine is called; otherwise they are relative to the last call time or the time the flight loop was registered (if never called).
