# Deferred Initialization - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

X-Plane plugins are first loaded **early** in the X-Plane init sequence. This means that there are limitations on what you can do.
## What's Available
  * You can register any plugin callbacks you need to during either XPluginStart or XPluginEnable.
  * You can get info about your plugin (like its path on disk) from XPluginStart or XPluginEnable.
  * You can submit a list of aircraft to control. They will be loaded later when x-plane gets to loading aircraft.

## What's off limits
Most parts of the system are not available. Do not use:
  * The nav database APIs.
  * Anything that depends on an aircraft being loaded, scenery being loaded, or the flight having sane values.
  * Don't use [[XPLMWorldToLocal](/sdk/XPLMWorldToLocal/)]("https://developer.x-plane.com/sdk/XPLMWorldToLocal/) or [[XPLMLocalToWorld](/sdk/XPLMLocalToWorld/)](https://developer.x-plane.com/sdk/XPLMLocalToWorld/)…because the scenery engine is not loaded, there is no local coordinate system!
  * Do not assume other plugins are loaded from your XPluginStart. They may load later.
  * Do not assume other plugins are enabled from your XPluginEnable. They may be enabled later. This means that you can resolve datarefs, but may not be able to read them (since a read will fail if the plugin providing the data is disabled).
  * Do not load aircraft or command scenery changes during startup!
  * Preferences are not loaded!

## Working around boot issues
Remember, XPluginEnable may be called during sim operations or during boot. The following technique will both allow you to defer processing until the sim is fully loaded and allow you to detect boot.
From your XPluginStart routine, register a processing callback to run on the first frame using XPLMRegisterFLightLoopCallback with an interval of -1.
## When Not to Defer
There is one case where it is important not to defer initialization: if your plugin is distributed in an aircraft package and registers datarefs that drive animated OBJs (attached to the aircraft), you must register your datarefs from XPluginStart. Don't defer!
Here's why this is necessary: the sim will load your plugin, and then load the OBJs (which will do an [XPLMFindDataRef](/sdk/XPLMFindDataRef/) to resolve datarefs in the OBJ file), and only when all loading is finished can the sim run and flight loop callbacks run. If you defer initialization, your datarefs won't be available when the OBJ file looks for them.
## Example
    
    [[XPLMRegisterFlightLoopCallback](/sdk/XPLMRegisterFlightLoopCallback/)](http://www.xsquawkbox.net/xpsdk/mediawiki/XPLMRegisterFlightLoopCallback)(DeferredInitNewAircraftFLCB, -1, NULL);
As soon as x-plane is fully loaded and trying to run for real, you get a callback. Do any loading now that you couldn't do before. Set a flag indicating that boot time is over; the next time you get your XPluginEnable call (if the user disables and enables your plugin), you'll know that it is safe to load immediately.
    
    float DeferredInitNewAircraftFLCB(float elapsedMe, float elapsedSim, int counter, void * refcon)
    {
    	static int MyProgramFLCBStartUpFlag = 0;
    	if ( MyProgramFLCBStartUpFlag == 0 )
    	{
    		MyProgramFLCBStartUpFlag = 1; // Flag tells init has already been completed
    		StartHeadX = [XPLMGetDataf](/sdk/XPLMGetDataf/)(CurrentHeadX); //Set startup parameters here.
    		
    		// ....
    		
    		DeltaTheta = 0.0;
    		[XPLMSetFlightLoopCallbackInterval](/sdk/XPLMSetFlightLoopCallbackInterval/) (MyProgramFLCB, 0.01, 1, NULL);
    	}
    	return 0; // Returning 0 stops DeferredInitFLCB from being looped again.
    }
    
## Problems With C/C++ Global Initialization
Global and file-static data are initialized **before** your XPluginStart call! So not only can you not pre-initialize global variables if you need deferred initialization, but you can't make **any** XPLM calls from global initializers. Here is an example of some offending C code:
    
    #include "XPLMDataRefs.h"
    
    // file static -- too early
    static [XPLMDataRef](/sdk/XPLMDataRef/) my_dref = [XPLMFindDataRef](/sdk/XPLMFindDataRef/)("sim/operation/pause");
    // true global -- too early
    [XPLMDataRef](/sdk/XPLMDataRef/) my_dref = [XPLMFindDataRef](/sdk/XPLMFindDataRef/)("sim/operation/pause");
    
If you have a C++ object, the same rules apply to constructors if the object is instantiated globally, like this:
    
    // wrapper obj around dataref
    class dataref_obj {
    public:
     dataref_obj(const char * s) { dref_ = [XPLMFindDataRef](/sdk/XPLMFindDataRef/)(s); }
     [XPLMDataRef](/sdk/XPLMDataRef/) dref_;
    };
    
    // these are too early too
    static dataref_obj static_obj([XPLMFindDataRef](/sdk/XPLMFindDataRef/)("sim/operation/pause");
    dataref_obj static_obj([XPLMFindDataRef](/sdk/XPLMFindDataRef/)("sim/operation/pause");
Declaring your object inside XPluginStart is legal, but often not useful.
    
    int XPluginStart(char*,char*,char*)
    {
      dataref_obj my_pause([XPLMFindDataRef](/sdk/XPLMFindDataRef/)("sim/operation/pause");
    }
because the obj goes away when XPluginStart ends. One way around this is to use dynamic allocation - this gives you control of when the object is created.
    
    // global
    dataref_obj * my_obj = NULL;	// safe - no XPLM calls
    
    int XPluginStart(...)
    {
      my_obj = new dataref_obj("sim/operation/pause");
    }
    
    void XPluginStop()
    { 
     delete my_obj;
    }
