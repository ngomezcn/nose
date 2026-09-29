# LuaJIT - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

_[LuaJIT 2.0](http://luajit.org/)_ is a just-in-time compiler for the Lua scripting language; it is the preferred way to embed Lua in a plugin. This technote describes the integration issues between X-Plane 10.20 64-bit and LuaJIT. These problems are specific to 64-bit builds of X-Plane; 32-bit plugins and Windows/Linux 64-bit plugins are not affected.
## LuaJIT 2.0 and Address Space
LuaJIT 2.0 requires that all allocations used by JIT code be in the bottom 2 GB of address space. (This requirement comes from the use of signed 32-bit relative addressing for constants in the generated code.) To meet this requirement, LuaJIT has two modifications from a 'standard' Lua run-time:
  * The built-in Lua allocator attempts to grab memory from the lower 2 GB of address space using various OS-specific techniques. The allocator is a copy of dl-malloc sitting on top of this custom bottom-2-gb scheme.
  * lua_newstate is inoperative in the 64-bit version of LuaJIT 2.0. This stops client code from providing a custom allocator that ignores the < 2 GB rule (which would cause JIT-code crashes).

## LuaJIT 2.0 and X-Plane
**The problem:** during normal operation, X-Plane's normal memory use may consume some or all of the free memory in the bottom 2 GB, potentially exhausting it under high scenery engine load. Plugins utilizing Lua that are loaded late (e.g. in an airplane load) or need more memory mid-run may fail allocation since this critical "bottom 2 GB" of address space is taken.
X-Plane 64-bit sometimes solves this problem by pre-grabbing the entire LuaJIT address space for itself on startup and then providing this address space to Lua-based plugins on demand. A special set of messages can be sent from plugins to X-Plane to allocate and release memory.
**You will need to modify LuaJIT to allow the custom allocator API on 64-bit builds.** Contact Ben if you need a pre-compiled static library that has the custom allocator API re-enabled.
You can use the dataref sim/operation/prefs/misc/has_lua_alloc (type int, read only) to determine whether the custom allocator message API is available. If it is, you _must_ use it.
## Sample Code to Use X-Plane's LuaJIT Allocator
    
    /* Include XPLM headers, etc. */
    
    struct lua_alloc_request_t {
    			void *	ud;
    			void *	ptr;
    			size_t	osize;
    			size_t	nsize;
    };
    
    #define		ALLOC_OPEN		0x00A110C1
    #define		ALLOC_REALLOC	0x00A110C2
    #define		ALLOC_CLOSE		0x00A110C3
    /* new in X-Plane 10.40 */
    #define		ALLOC_LOCK		0x00A110C4
    #define		ALLOC_UNLOCK		0x00A110C5
    #define		ALLOC_LOCK_RO		0x00A110C6
    
    /* lua interpreter */
    lua_State* l;
    
    static void *lj_alloc_create(void)
    {
    	struct lua_alloc_request_t r = { 0 };
    	[XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)([XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/), ALLOC_OPEN,&r);
    	return r.ud;	
    }
    
    static void  lj_alloc_destroy(void *msp)
    {
    	struct lua_alloc_request_t r = { 0 };
    	r.ud = msp;
    	[XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)([XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/), ALLOC_CLOSE,&r);
    }
    
    static void *lj_alloc_f(void *msp, void *ptr, size_t osize, size_t nsize)
    {
    	struct lua_alloc_request_t r = { 0 };
    	r.ud = msp;
    	r.ptr = ptr;
    	r.osize = osize;
    	r.nsize = nsize;
    	[XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)([XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/), ALLOC_REALLOC,&r);
    	return r.ptr;
    }
    
    void * ud;
    lua_State * l;
    
    /* when you need to init LUA... */
    
      [XPLMDataRef](/sdk/XPLMDataRef/) lua_alloc_ref = [XPLMFindDataRef](/sdk/XPLMFindDataRef/)("sim/operation/prefs/misc/has_lua_alloc");
      if(lua_alloc_ref && [XPLMGetDatai](/sdk/XPLMGetDatai/)(lua_alloc_ref))
      {
        /* X-Plane has an allocator for us - we _must_ use it. */
        ud = lj_alloc_create();
        if (ud == NULL)
        {
           /* handle error */
        }
        l = lua_newstate(lj_alloc_f, ud);
      }
      else
      {
        /* use the default allocator. */
        l = luaL_newstate();
      }
    
    /* when you need to clean up lua */
    
      [XPLMDataRef](/sdk/XPLMDataRef/) lua_alloc_ref = [XPLMFindDataRef](/sdk/XPLMFindDataRef/)("sim/operation/prefs/misc/has_lua_alloc");
      if(lua_alloc_ref && [XPLMGetDatai](/sdk/XPLMGetDatai/)(lua_alloc_ref))
      {
        lua_close(l);
        lj_alloc_destroy(ud);
      }
      else
      {
        lua_close(l);
      }
    
## Test Materials for LuaJIT 2.0.1 64-bit
The official version of _[LuaJIT](http://luajit.org/)_ does not allow custom allocators in 64-bit builds. (They did this to prevent people from accidentally allocating high memory.) To wire LuaJIT to X-Plane's allocator (which does provide low memory as needed), a modified build of LuaJIT is needed.
Here is a link to the source for LuaJIT with the custom 64-bit allocator re-enabled:__
  * Complete source code, with the modifications to allow the allocator: <https://github.com/X-Plane/XLua/tree/master/LuaJIT-2.0.4>

Windows builds are compiled with MSVC2010; the runtime has been set to /MT to avoid a DLL dependency on the MSVC runtime (which is not required for X-Plane itself!) Mac builds are compiled on OS X 10.6 and Linux on Ubuntu 10.04.
To test LuaJIT allocation, get X-Plane 10.22 beta 1 or newer; 10.22 contains 64-bit LuaJIT allocation support for all three operating systems.
## Viewing Lua Memory Usage
You can show you current total bytes allocated by the lua-allocation-service:
  * _<http://dev.x-plane.com/download/X-Plane_lua_mem.app.zip>_

To view the allocations, use _[DataRefEditor](http://www.xsquawkbox.net/xpsdk/mediawiki/DataRefEditor)_ - pick "view stats" and then filter for "lua". You will see the bytes go up and then back down when garbage collection kicks in; if the bytes go up continuously, you may have a leak!
## Memory Protection for Lua
Starting with X-Plane 10.40, you can request that X-Plane temporarily lock access to the memory allocated for your plugin for Lua use. Two new messages (ALLOC_LOCK = 0xA110C4 and ALLOC_UNLOCK = 0xA110C5) make the change, e.g.
    
    void lock_lua_memory()
    {
        [XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)([XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/), ALLOC_LOCK, NULL);
    }
    
    void unlock_lua_memory()
    {
        [XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)([XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/), ALLOC_UNLOCK, NULL);
    }
    
    void lock_read_only_lua_memory()
    {
        [XPLMSendMessageToPlugin](/sdk/XPLMSendMessageToPlugin/)([XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/), ALLOC_LOCK_RO, NULL);
    }
    
Your memory starts unlocked; when you send a lock message, the underlying VM pages allocated for use by Lua are set to not be readable or writable; any code that then accesses them will crash x-plane at the point of access.
The purpose of this lock is to detect memory corruption immediately; while the lock is on, code that is corrupting lua memory will crash with a back-trace pointing to the offending code.
Note that the lock:
  * Must be off for you to run any lua API calls, including executing Lua code, reading from the interpreter, or creating/destroying Lua contexts.
  * Locks/unlocks only your plugins memory.
