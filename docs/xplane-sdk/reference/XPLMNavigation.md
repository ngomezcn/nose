# XPLMNavigation

Source: https://developer.x-plane.com/  (mirrored locally)

# [XPLMNavigation](/sdk/XPLMNavigation/) API
The XPLM Navigation APIs give you some access to the navigation databases inside X-Plane. X-Plane stores all navigation information in RAM, so by using these APIs you can gain access to most information without having to go to disk or parse the files yourself.
You can also use this API to program the FMS. You must use the navigation APIs to find the nav-aids you want to program into the FMS, since the FMS is powered internally by X-Plane's navigation database.
## NAVIGATION DATABASE ACCESS
### [XPLMNavType](/sdk/XPLMNavType/)
These enumerations define the different types of navaids. They are each defined with a separate bit so that they may be bit-wise added together to form sets of nav-aid types.
NOTE: [xplm_Nav_LatLon](/sdk/xplm_Nav_LatLon/) is a specific lat-lon coordinate entered into the FMS. It will not exist in the database, and cannot be programmed into the FMS. Querying the FMS for navaids will return it. Use [XPLMSetFMSEntryLatLon](/sdk/XPLMSetFMSEntryLatLon/) to set a lat/lon waypoint.
Name| Value| Description  
---|---|---  
[xplm_Nav_Unknown](/sdk/xplm_Nav_Unknown/)| "0"|   
[xplm_Nav_Airport](/sdk/xplm_Nav_Airport/)| "1"|   
[xplm_Nav_NDB](/sdk/xplm_Nav_NDB/)| "2"|   
[xplm_Nav_VOR](/sdk/xplm_Nav_VOR/)| "4"|   
[xplm_Nav_ILS](/sdk/xplm_Nav_ILS/)| "8"|   
[xplm_Nav_Localizer](/sdk/xplm_Nav_Localizer/)| "16"|   
[xplm_Nav_GlideSlope](/sdk/xplm_Nav_GlideSlope/)| "32"|   
[xplm_Nav_OuterMarker](/sdk/xplm_Nav_OuterMarker/)| "64"|   
[xplm_Nav_MiddleMarker](/sdk/xplm_Nav_MiddleMarker/)| "128"|   
[xplm_Nav_InnerMarker](/sdk/xplm_Nav_InnerMarker/)| "256"|   
[xplm_Nav_Fix](/sdk/xplm_Nav_Fix/)| "512"|   
[xplm_Nav_DME](/sdk/xplm_Nav_DME/)| "1024"|   
[xplm_Nav_LatLon](/sdk/xplm_Nav_LatLon/)| "2048"|   
[xplm_Nav_TACAN](/sdk/xplm_Nav_TACAN/)| "4096"|   
### [XPLMNavRef](/sdk/XPLMNavRef/)
    
    typedef int [XPLMNavRef](/sdk/XPLMNavRef/);
[XPLMNavRef](/sdk/XPLMNavRef/) is an iterator into the navigation database. The navigation database is essentially an array, but it is not necessarily densely populated. The only assumption you can safely make is that like-typed nav-aids are grouped together.
Use [XPLMNavRef](/sdk/XPLMNavRef/) to refer to a nav-aid.
[XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) is returned by functions that return an [XPLMNavRef](/sdk/XPLMNavRef/) when the iterator must be invalid.
### [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/)
    
    #define [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/)   -1
### [XPLMGetFirstNavAid](/sdk/XPLMGetFirstNavAid/)
    
    XPLM_API [XPLMNavRef](/sdk/XPLMNavRef/) [XPLMGetFirstNavAid](/sdk/XPLMGetFirstNavAid/)(void);
    
This returns the very first navaid in the database. Use this to traverse the entire database. Returns [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) if the nav database is empty.
### [XPLMGetNextNavAid](/sdk/XPLMGetNextNavAid/)
    
    XPLM_API [XPLMNavRef](/sdk/XPLMNavRef/) [XPLMGetNextNavAid](/sdk/XPLMGetNextNavAid/)(
                             [XPLMNavRef](/sdk/XPLMNavRef/)           inNavAidRef);
    
Given a valid navaid ref, this routine returns the next navaid. It returns [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) if the navaid passed in was invalid or if the navaid passed in was the last one in the database. Use this routine to iterate across all like-typed navaids or the entire database.
### [XPLMFindFirstNavAidOfType](/sdk/XPLMFindFirstNavAidOfType/)
    
    XPLM_API [XPLMNavRef](/sdk/XPLMNavRef/) [XPLMFindFirstNavAidOfType](/sdk/XPLMFindFirstNavAidOfType/)(
                             [XPLMNavType](/sdk/XPLMNavType/)          inType);
    
This routine returns the ref of the first navaid of the given type in the database or [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) if there are no navaids of that type in the database. You must pass exactly one navaid type to this routine.
### [XPLMFindLastNavAidOfType](/sdk/XPLMFindLastNavAidOfType/)
    
    XPLM_API [XPLMNavRef](/sdk/XPLMNavRef/) [XPLMFindLastNavAidOfType](/sdk/XPLMFindLastNavAidOfType/)(
                             [XPLMNavType](/sdk/XPLMNavType/)          inType);
    
This routine returns the ref of the last navaid of the given type in the database or [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) if there are no navaids of that type in the database. You must pass exactly one navaid type to this routine.
### [XPLMFindNavAid](/sdk/XPLMFindNavAid/)
    
    XPLM_API [XPLMNavRef](/sdk/XPLMNavRef/) [XPLMFindNavAid](/sdk/XPLMFindNavAid/)(
                             const char *         inNameFragment,    /* Can be NULL */
                             const char *         inIDFragment,    /* Can be NULL */
                             float *              inLat,    /* Can be NULL */
                             float *              inLon,    /* Can be NULL */
                             int *                inFrequency,    /* Can be NULL */
                             [XPLMNavType](/sdk/XPLMNavType/)          inType);
    
This routine provides a number of searching capabilities for the nav database. [XPLMFindNavAid](/sdk/XPLMFindNavAid/) will search through every navaid whose type is within inType (multiple types may be added together) and return any navaids found based on the following rules:
  * If inLat and inLon are not NULL, the navaid nearest to that lat/lon will be returned, otherwise the last navaid found will be returned.
  * If inFrequency is not NULL, then any navaids considered must match this frequency. Note that this will screen out radio beacons that do not have frequency data published (like inner markers) but not fixes and airports.
  * If inNameFragment is not NULL, only navaids that contain the fragment in their name will be returned.
  * If inIDFragment is not NULL, only navaids that contain the fragment in their IDs will be returned.

This routine provides a simple way to do a number of useful searches: * Find the nearest navaid on this frequency. * Find the nearest airport. * Find the VOR whose ID is "BOS". * Find the nearest airport whose name contains "Chicago".
### [XPLMGetNavAidInfo](/sdk/XPLMGetNavAidInfo/)
    
    XPLM_API void       [XPLMGetNavAidInfo](/sdk/XPLMGetNavAidInfo/)(
                             [XPLMNavRef](/sdk/XPLMNavRef/)           inRef,
                             [XPLMNavType](/sdk/XPLMNavType/) *        outType,    /* Can be NULL */
                             float *              outLatitude,    /* Can be NULL */
                             float *              outLongitude,    /* Can be NULL */
                             float *              outHeight,    /* Can be NULL */
                             int *                outFrequency,    /* Can be NULL */
                             float *              outHeading,    /* Can be NULL */
                             char *               outID,    /* Can be NULL */
                             char *               outName,    /* Can be NULL */
                             char *               outReg);    /* Can be NULL */
    
This routine returns information about a navaid. Any non-null field is filled out with information if it is available.
Frequencies are in the nav.dat convention as described in the X-Plane nav database FAQ: NDB frequencies are exact, all others are multiplied by 100.
The buffer for IDs should be at least 6 chars and the buffer for names should be at least 41 chars, but since these values are likely to go up, I recommend passing at least 32 chars for IDs and 256 chars for names when possible.
The outReg parameter tells if the navaid is within the local "region" of loaded DSFs. (This information may not be particularly useful to plugins.) The parameter is a single byte value 1 for true or 0 for false, not a C string.
## FLIGHT MANAGEMENT COMPUTER
Note: the FMS works based on an array of entries. Indices into the array are zero-based. Each entry is a navaid plus an altitude. The FMS tracks the currently displayed entry and the entry that it is flying to.
The FMS must be programmed with contiguous entries, so clearing an entry at the end shortens the effective flight plan. There is a max of 100 waypoints in the flight plan.
### [XPLMCountFMSEntries](/sdk/XPLMCountFMSEntries/)
    
    XPLM_API int        [XPLMCountFMSEntries](/sdk/XPLMCountFMSEntries/)(void);
    
This routine returns the number of entries in the FMS.
### [XPLMGetDisplayedFMSEntry](/sdk/XPLMGetDisplayedFMSEntry/)
    
    XPLM_API int        [XPLMGetDisplayedFMSEntry](/sdk/XPLMGetDisplayedFMSEntry/)(void);
    
This routine returns the index of the entry the pilot is viewing.
### [XPLMGetDestinationFMSEntry](/sdk/XPLMGetDestinationFMSEntry/)
    
    XPLM_API int        [XPLMGetDestinationFMSEntry](/sdk/XPLMGetDestinationFMSEntry/)(void);
    
This routine returns the index of the entry the FMS is flying to.
### [XPLMSetDisplayedFMSEntry](/sdk/XPLMSetDisplayedFMSEntry/)
    
    XPLM_API void       [XPLMSetDisplayedFMSEntry](/sdk/XPLMSetDisplayedFMSEntry/)(
                             int                  inIndex);
    
This routine changes which entry the FMS is showing to the index specified.
### [XPLMSetDestinationFMSEntry](/sdk/XPLMSetDestinationFMSEntry/)
    
    XPLM_API void       [XPLMSetDestinationFMSEntry](/sdk/XPLMSetDestinationFMSEntry/)(
                             int                  inIndex);
    
This routine changes which entry the FMS is flying the aircraft toward. The track is from the n-1'th point to the n'th point.
### [XPLMGetFMSEntryInfo](/sdk/XPLMGetFMSEntryInfo/)
    
    XPLM_API void       [XPLMGetFMSEntryInfo](/sdk/XPLMGetFMSEntryInfo/)(
                             int                  inIndex,
                             [XPLMNavType](/sdk/XPLMNavType/) *        outType,    /* Can be NULL */
                             char *               outID,    /* Can be NULL */
                             [XPLMNavRef](/sdk/XPLMNavRef/) *         outRef,    /* Can be NULL */
                             int *                outAltitude,    /* Can be NULL */
                             float *              outLat,    /* Can be NULL */
                             float *              outLon);    /* Can be NULL */
    
This routine returns information about a given FMS entry. If the entry is an airport or navaid, a reference to a nav entry can be returned allowing you to find additional information (such as a frequency, ILS heading, name, etc.). Note that this reference can be [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) until the information has been looked up asynchronously, so after flightplan changes, it might take up to a second for this field to become populated. The other information is available immediately. For a lat/lon entry, the lat/lon is returned by this routine but the navaid cannot be looked up (and the reference will be [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/)). FMS name entry buffers should be at least 256 chars in length.
WARNING: Due to a bug in X-Plane prior to 11.31, the navaid reference will not be set to [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) while no data is available, and instead just remain the value of the variable that you passed the pointer to. Therefore, always initialize the variable to [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) before passing the pointer to this function.
### [XPLMSetFMSEntryInfo](/sdk/XPLMSetFMSEntryInfo/)
    
    XPLM_API void       [XPLMSetFMSEntryInfo](/sdk/XPLMSetFMSEntryInfo/)(
                             int                  inIndex,
                             [XPLMNavRef](/sdk/XPLMNavRef/)           inRef,
                             int                  inAltitude);
    
This routine changes an entry in the FMS to have the destination navaid passed in and the altitude specified. Use this only for airports, fixes, and radio-beacon navaids. Currently of radio beacons, the FMS can only support VORs and NDBs. Use the routines below to clear or fly to a lat/lon.
### [XPLMSetFMSEntryLatLon](/sdk/XPLMSetFMSEntryLatLon/)
    
    XPLM_API void       [XPLMSetFMSEntryLatLon](/sdk/XPLMSetFMSEntryLatLon/)(
                             int                  inIndex,
                             float                inLat,
                             float                inLon,
                             int                  inAltitude);
    
This routine changes the entry in the FMS to a lat/lon entry with the given coordinates.
### [XPLMClearFMSEntry](/sdk/XPLMClearFMSEntry/)
    
    XPLM_API void       [XPLMClearFMSEntry](/sdk/XPLMClearFMSEntry/)(
                             int                  inIndex);
    
This routine clears the given entry, potentially shortening the flight plan.
### [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)
    
    These enumerations defines the flightplan you are accesing using the FMSFlightPlan functions.
    An airplane can have up to two navigation devices (GPS or FMS) and each device can have two flightplans.
    A GPS has an enroute and an approach flightplan.
    An FMS has an active and a temporary flightplan.
    If you are trying to access a flightplan that doesn't exist in your aircraft, e.g. asking a GPS for a temp flightplan, FMSFlighPlan functions have no effect and will return no information.
    
Name| Value| Description  
---|---|---  
[xplm_Fpl_Pilot_Primary](/sdk/xplm_Fpl_Pilot_Primary/)| "0"|   
[xplm_Fpl_CoPilot_Primary](/sdk/xplm_Fpl_CoPilot_Primary/)| "1"|   
[xplm_Fpl_Pilot_Approach](/sdk/xplm_Fpl_Pilot_Approach/)| "2"|   
[xplm_Fpl_CoPilot_Approach](/sdk/xplm_Fpl_CoPilot_Approach/)| "3"|   
[xplm_Fpl_Pilot_Temporary](/sdk/xplm_Fpl_Pilot_Temporary/)| "4"|   
[xplm_Fpl_CoPilot_Temporary](/sdk/xplm_Fpl_CoPilot_Temporary/)| "5"|   
### [XPLMCountFMSFlightPlanEntries](/sdk/XPLMCountFMSFlightPlanEntries/)
    
    XPLM_API int        [XPLMCountFMSFlightPlanEntries](/sdk/XPLMCountFMSFlightPlanEntries/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan);
    
This routine returns the number of entries in the FMS.
### [XPLMGetDisplayedFMSFlightPlanEntry](/sdk/XPLMGetDisplayedFMSFlightPlanEntry/)
    
    XPLM_API int        [XPLMGetDisplayedFMSFlightPlanEntry](/sdk/XPLMGetDisplayedFMSFlightPlanEntry/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan);
    
This routine returns the index of the entry the pilot is viewing.
### [XPLMGetDestinationFMSFlightPlanEntry](/sdk/XPLMGetDestinationFMSFlightPlanEntry/)
    
    XPLM_API int        [XPLMGetDestinationFMSFlightPlanEntry](/sdk/XPLMGetDestinationFMSFlightPlanEntry/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan);
    
This routine returns the index of the entry the FMS is flying to.
### [XPLMSetDisplayedFMSFlightPlanEntry](/sdk/XPLMSetDisplayedFMSFlightPlanEntry/)
    
    XPLM_API void       [XPLMSetDisplayedFMSFlightPlanEntry](/sdk/XPLMSetDisplayedFMSFlightPlanEntry/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex);
    
This routine changes which entry the FMS is showing to the index specified.
### [XPLMSetDestinationFMSFlightPlanEntry](/sdk/XPLMSetDestinationFMSFlightPlanEntry/)
    
    XPLM_API void       [XPLMSetDestinationFMSFlightPlanEntry](/sdk/XPLMSetDestinationFMSFlightPlanEntry/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex);
    
This routine changes which entry the FMS is flying the aircraft toward. The track is from the n-1'th point to the n'th point.
### [XPLMSetDirectToFMSFlightPlanEntry](/sdk/XPLMSetDirectToFMSFlightPlanEntry/)
    
    XPLM_API void       [XPLMSetDirectToFMSFlightPlanEntry](/sdk/XPLMSetDirectToFMSFlightPlanEntry/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex);
    
This routine changes which entry the FMS is flying the aircraft toward. The track is from the current position of the aircraft directly to the n'th point, ignoring the point before it.
### [XPLMGetFMSFlightPlanEntryInfo](/sdk/XPLMGetFMSFlightPlanEntryInfo/)
    
    XPLM_API void       [XPLMGetFMSFlightPlanEntryInfo](/sdk/XPLMGetFMSFlightPlanEntryInfo/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex,
                             [XPLMNavType](/sdk/XPLMNavType/) *        outType,    /* Can be NULL */
                             char *               outID,    /* Can be NULL */
                             [XPLMNavRef](/sdk/XPLMNavRef/) *         outRef,    /* Can be NULL */
                             int *                outAltitude,    /* Can be NULL */
                             float *              outLat,    /* Can be NULL */
                             float *              outLon);    /* Can be NULL */
    
This routine returns information about a given FMS entry. If the entry is an airport or navaid, a reference to a nav entry can be returned allowing you to find additional information (such as a frequency, ILS heading, name, etc.). Note that this reference can be [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) until the information has been looked up asynchronously, so after flightplan changes, it might take up to a second for this field to become populated. The other information is available immediately. For a lat/lon entry, the lat/lon is returned by this routine but the navaid cannot be looked up (and the reference will be [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/)). FMS name entry buffers should be at least 256 chars in length.
WARNING: Due to a bug in X-Plane prior to 11.31, the navaid reference will not be set to [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) while no data is available, and instead just remain the value of the variable that you passed the pointer to. Therefore, always initialize the variable to [XPLM_NAV_NOT_FOUND](/sdk/XPLM_NAV_NOT_FOUND/) before passing the pointer to this function.
### [XPLMSetFMSFlightPlanEntryInfo](/sdk/XPLMSetFMSFlightPlanEntryInfo/)
    
    XPLM_API void       [XPLMSetFMSFlightPlanEntryInfo](/sdk/XPLMSetFMSFlightPlanEntryInfo/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex,
                             [XPLMNavRef](/sdk/XPLMNavRef/)           inRef,
                             int                  inAltitude);
    
This routine changes an entry in the FMS to have the destination navaid passed in and the altitude specified. Use this only for airports, fixes, and radio-beacon navaids. Currently of radio beacons, the FMS can only support VORs, NDBs and TACANs. Use the routines below to clear or fly to a lat/lon.
### [XPLMSetFMSFlightPlanEntryLatLon](/sdk/XPLMSetFMSFlightPlanEntryLatLon/)
    
    XPLM_API void       [XPLMSetFMSFlightPlanEntryLatLon](/sdk/XPLMSetFMSFlightPlanEntryLatLon/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex,
                             float                inLat,
                             float                inLon,
                             int                  inAltitude);
    
This routine changes the entry in the FMS to a lat/lon entry with the given coordinates.
### [XPLMSetFMSFlightPlanEntryLatLonWithId](/sdk/XPLMSetFMSFlightPlanEntryLatLonWithId/)
    
    XPLM_API void       [XPLMSetFMSFlightPlanEntryLatLonWithId](/sdk/XPLMSetFMSFlightPlanEntryLatLonWithId/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex,
                             float                inLat,
                             float                inLon,
                             int                  inAltitude,
                             const char*          inId,
                             unsigned int         inIdLength);
    
This routine changes the entry in the FMS to a lat/lon entry with the given coordinates. You can specify the display ID of the waypoint.
### [XPLMClearFMSFlightPlanEntry](/sdk/XPLMClearFMSFlightPlanEntry/)
    
    XPLM_API void       [XPLMClearFMSFlightPlanEntry](/sdk/XPLMClearFMSFlightPlanEntry/)(
                             [XPLMNavFlightPlan](/sdk/XPLMNavFlightPlan/)    inFlightPlan,
                             int                  inIndex);
    
This routine clears the given entry, potentially shortening the flight plan.
### [XPLMLoadFMSFlightPlan](/sdk/XPLMLoadFMSFlightPlan/)
    
    XPLM_API void       [XPLMLoadFMSFlightPlan](/sdk/XPLMLoadFMSFlightPlan/)(
                             int                  inDevice,
                             const char *         inBuffer,
                             unsigned int         inBufferLen);
    
Loads an X-Plane 11 and later formatted flightplan from the buffer into the FMS or GPS, including instrument procedures. Use device index 0 for the pilot-side and device index 1 for the co-pilot side unit.
### [XPLMSaveFMSFlightPlan](/sdk/XPLMSaveFMSFlightPlan/)
    
    XPLM_API unsigned int [XPLMSaveFMSFlightPlan](/sdk/XPLMSaveFMSFlightPlan/)(
                             int                  inDevice,
                             char *               inBuffer,
                             unsigned int         inBufferLen);
    
Saves an X-Plane 11 formatted flightplan from the FMS or GPS into a char buffer that you provide. Use device index 0 for the pilot-side and device index 1 for the co-pilot side unit. Provide the length of the buffer you allocated. X-Plane will write a null-terminated string if the full flight plan fits into the buffer. If your buffer is too small, X-Plane will write inBufferLen characters, and the resulting buffer is not null-terminated. The return value is the number of characters (including null terminator) that X-Plane needed to write the flightplan. If this number is larger than the buffer you provided, the flightplan in the buffer will be incomplete and the buffer not null-terminated.
## GPS RECEIVER
These APIs let you read data from the GPS unit.
### [XPLMGetGPSDestinationType](/sdk/XPLMGetGPSDestinationType/)
    
    XPLM_API [XPLMNavType](/sdk/XPLMNavType/) [XPLMGetGPSDestinationType](/sdk/XPLMGetGPSDestinationType/)(void);
    
This routine returns the type of the currently selected GPS destination, one of fix, airport, VOR or NDB.
### [XPLMGetGPSDestination](/sdk/XPLMGetGPSDestination/)
    
    XPLM_API [XPLMNavRef](/sdk/XPLMNavRef/) [XPLMGetGPSDestination](/sdk/XPLMGetGPSDestination/)(void);
    
This routine returns the current GPS destination.
