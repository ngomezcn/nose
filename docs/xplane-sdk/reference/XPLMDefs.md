# XPLMDefs

Source: https://developer.x-plane.com/  (mirrored locally)

# [XPLMDefs](/sdk/XPLMDefs/) API
This file is contains the cross-platform and basic definitions for the X-Plane SDK.
The preprocessor macros APL, LIN and IBM must be defined to specify the compilation target; define APL to 1 to compile on Mac, IBM to 1 to compile on Windows and LIN to 1 to compile on Linux. Only one compilation target may be used at a time. You must specify these macro definitions before including [XPLMDefs](/sdk/XPLMDefs/).h or any other XPLM headers. You can do this using the -D command line option or a preprocessor header.
## DLL Definitions
These definitions control the importing and exporting of functions within the DLL.
You can prefix your five required callbacks with the PLUGIN_API macro to declare them as exported C functions. The XPLM_API macro identifies functions that are provided to you via the plugin SDK. (Link against XPLM.lib to use these functions.)
## GLOBAL DEFINITIONS
These definitions are used in all parts of the SDK.
### [XPLMPluginID](/sdk/XPLMPluginID/)
    
    typedef int [XPLMPluginID](/sdk/XPLMPluginID/);
Each plug-in is identified by a unique integer ID. This ID can be used to disable or enable a plug-in, or discover what plug-in is 'running' at the time. A plug-in ID is unique within the currently running instance of X-Plane unless plug-ins are reloaded. Plug-ins may receive a different unique ID each time they are loaded. This includes the unloading and reloading of plugins that are part of the user's aircraft.
For persistent identification of plug-ins, use [XPLMFindPluginBySignature](/sdk/XPLMFindPluginBySignature/) in XPLMUtiltiies.h .
-1 indicates no plug-in.
### [XPLM_NO_PLUGIN_ID](/sdk/XPLM_NO_PLUGIN_ID/)
    
    #define [XPLM_NO_PLUGIN_ID](/sdk/XPLM_NO_PLUGIN_ID/)    (-1)
No plugin.
### [XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/)
    
    #define [XPLM_PLUGIN_XPLANE](/sdk/XPLM_PLUGIN_XPLANE/)   (0)
X-Plane itself
### [kXPLM_Version](/sdk/kXPLM_Version/)
    
    #define [kXPLM_Version](/sdk/kXPLM_Version/)        (430)
The current XPLM revision is 4.3.0 (430).
### [XPLMKeyFlags](/sdk/XPLMKeyFlags/)
These bitfields define modifier keys in a platform independent way. When a key is pressed, a series of messages are sent to your plugin. The down flag is set in the first of these messages, and the up flag in the last. While the key is held down, messages are sent with neither flag set to indicate that the key is being held down as a repeated character.
The control flag is mapped to the control flag on Macintosh and PC. Generally X-Plane uses the control key and not the command key on Macintosh, providing a consistent interface across platforms that does not necessarily match the Macintosh user interface guidelines. There is not yet a way for plugins to access the Macintosh control keys without using #ifdefed code.
Name| Value| Description  
---|---|---  
[xplm_ShiftFlag](/sdk/xplm_ShiftFlag/)| "1"|  The shift key is down  
[xplm_OptionAltFlag](/sdk/xplm_OptionAltFlag/)| "2"|  The option or alt key is down  
[xplm_ControlFlag ](/sdk/xplm_ControlFlag /)| "4"|  The control key is down  
[xplm_DownFlag](/sdk/xplm_DownFlag/)| "8"|  The key is being pressed down  
[xplm_UpFlag](/sdk/xplm_UpFlag/)| "16"|  The key is being released  
## ASCII CONTROL KEY CODES
These definitions define how various control keys are mapped to ASCII key codes. Not all key presses generate an ASCII value, so plugin code should be prepared to see null characters come from the keyboard…this usually represents a key stroke that has no equivalent ASCII, like a page-down press. Use virtual key codes to find these key strokes.
ASCII key codes take into account modifier keys; shift keys will affect capitals and punctuation; control key combinations may have no vaild ASCII and produce NULL. To detect control-key combinations, use virtual key codes, not ASCII keys.
### [XPLM_KEY_RETURN](/sdk/XPLM_KEY_RETURN/)
    
    #define [XPLM_KEY_RETURN](/sdk/XPLM_KEY_RETURN/)      13
### [XPLM_KEY_ESCAPE](/sdk/XPLM_KEY_ESCAPE/)
    
    #define [XPLM_KEY_ESCAPE](/sdk/XPLM_KEY_ESCAPE/)      27
### [XPLM_KEY_TAB](/sdk/XPLM_KEY_TAB/)
    
    #define [XPLM_KEY_TAB](/sdk/XPLM_KEY_TAB/)         9
### [XPLM_KEY_DELETE](/sdk/XPLM_KEY_DELETE/)
    
    #define [XPLM_KEY_DELETE](/sdk/XPLM_KEY_DELETE/)      8
### [XPLM_KEY_LEFT](/sdk/XPLM_KEY_LEFT/)
    
    #define [XPLM_KEY_LEFT](/sdk/XPLM_KEY_LEFT/)        28
### [XPLM_KEY_RIGHT](/sdk/XPLM_KEY_RIGHT/)
    
    #define [XPLM_KEY_RIGHT](/sdk/XPLM_KEY_RIGHT/)       29
### [XPLM_KEY_UP](/sdk/XPLM_KEY_UP/)
    
    #define [XPLM_KEY_UP](/sdk/XPLM_KEY_UP/)          30
### [XPLM_KEY_DOWN](/sdk/XPLM_KEY_DOWN/)
    
    #define [XPLM_KEY_DOWN](/sdk/XPLM_KEY_DOWN/)        31
### [XPLM_KEY_0](/sdk/XPLM_KEY_0/)
    
    #define [XPLM_KEY_0](/sdk/XPLM_KEY_0/)           48
### [XPLM_KEY_1](/sdk/XPLM_KEY_1/)
    
    #define [XPLM_KEY_1](/sdk/XPLM_KEY_1/)           49
### [XPLM_KEY_2](/sdk/XPLM_KEY_2/)
    
    #define [XPLM_KEY_2](/sdk/XPLM_KEY_2/)           50
### [XPLM_KEY_3](/sdk/XPLM_KEY_3/)
    
    #define [XPLM_KEY_3](/sdk/XPLM_KEY_3/)           51
### [XPLM_KEY_4](/sdk/XPLM_KEY_4/)
    
    #define [XPLM_KEY_4](/sdk/XPLM_KEY_4/)           52
### [XPLM_KEY_5](/sdk/XPLM_KEY_5/)
    
    #define [XPLM_KEY_5](/sdk/XPLM_KEY_5/)           53
### [XPLM_KEY_6](/sdk/XPLM_KEY_6/)
    
    #define [XPLM_KEY_6](/sdk/XPLM_KEY_6/)           54
### [XPLM_KEY_7](/sdk/XPLM_KEY_7/)
    
    #define [XPLM_KEY_7](/sdk/XPLM_KEY_7/)           55
### [XPLM_KEY_8](/sdk/XPLM_KEY_8/)
    
    #define [XPLM_KEY_8](/sdk/XPLM_KEY_8/)           56
### [XPLM_KEY_9](/sdk/XPLM_KEY_9/)
    
    #define [XPLM_KEY_9](/sdk/XPLM_KEY_9/)           57
### [XPLM_KEY_DECIMAL](/sdk/XPLM_KEY_DECIMAL/)
    
    #define [XPLM_KEY_DECIMAL](/sdk/XPLM_KEY_DECIMAL/)     46
## VIRTUAL KEY CODES
These are cross-platform defines for every distinct keyboard press on the computer. Every physical key on the keyboard has a virtual key code. So the "two" key on the top row of the main keyboard has a different code from the "two" key on the numeric key pad. But the 'w' and 'W' character are indistinguishable by virtual key code because they are the same physical key (one with and one without the shift key).
Use virtual key codes to detect keystrokes that do not have ASCII equivalents, allow the user to map the numeric keypad separately from the main keyboard, and detect control key and other modifier-key combinations that generate ASCII control key sequences (many of which are not available directly via character keys in the SDK).
To assign virtual key codes we started with the Microsoft set but made some additions and changes. A few differences:
  1. Modifier keys are not available as virtual key codes. You cannot get distinct modifier press and release messages. Please do not try to use modifier keys as regular keys; doing so will almost certainly interfere with users' abilities to use the native X-Plane key bindings.
  2. Some keys that do not exist on both Mac and PC keyboards are removed.
  3. Do not assume that the values of these keystrokes are interchangeable with MS v-keys.

### [XPLM_VK_BACK](/sdk/XPLM_VK_BACK/)
    
    #define [XPLM_VK_BACK](/sdk/XPLM_VK_BACK/)         0x08
### [XPLM_VK_TAB](/sdk/XPLM_VK_TAB/)
    
    #define [XPLM_VK_TAB](/sdk/XPLM_VK_TAB/)          0x09
### [XPLM_VK_CLEAR](/sdk/XPLM_VK_CLEAR/)
    
    #define [XPLM_VK_CLEAR](/sdk/XPLM_VK_CLEAR/)        0x0C
### [XPLM_VK_RETURN](/sdk/XPLM_VK_RETURN/)
    
    #define [XPLM_VK_RETURN](/sdk/XPLM_VK_RETURN/)       0x0D
### [XPLM_VK_ESCAPE](/sdk/XPLM_VK_ESCAPE/)
    
    #define [XPLM_VK_ESCAPE](/sdk/XPLM_VK_ESCAPE/)       0x1B
### [XPLM_VK_SPACE](/sdk/XPLM_VK_SPACE/)
    
    #define [XPLM_VK_SPACE](/sdk/XPLM_VK_SPACE/)        0x20
### [XPLM_VK_PRIOR](/sdk/XPLM_VK_PRIOR/)
    
    #define [XPLM_VK_PRIOR](/sdk/XPLM_VK_PRIOR/)        0x21
### [XPLM_VK_NEXT](/sdk/XPLM_VK_NEXT/)
    
    #define [XPLM_VK_NEXT](/sdk/XPLM_VK_NEXT/)         0x22
### [XPLM_VK_END](/sdk/XPLM_VK_END/)
    
    #define [XPLM_VK_END](/sdk/XPLM_VK_END/)          0x23
### [XPLM_VK_HOME](/sdk/XPLM_VK_HOME/)
    
    #define [XPLM_VK_HOME](/sdk/XPLM_VK_HOME/)         0x24
### [XPLM_VK_LEFT](/sdk/XPLM_VK_LEFT/)
    
    #define [XPLM_VK_LEFT](/sdk/XPLM_VK_LEFT/)         0x25
### [XPLM_VK_UP](/sdk/XPLM_VK_UP/)
    
    #define [XPLM_VK_UP](/sdk/XPLM_VK_UP/)           0x26
### [XPLM_VK_RIGHT](/sdk/XPLM_VK_RIGHT/)
    
    #define [XPLM_VK_RIGHT](/sdk/XPLM_VK_RIGHT/)        0x27
### [XPLM_VK_DOWN](/sdk/XPLM_VK_DOWN/)
    
    #define [XPLM_VK_DOWN](/sdk/XPLM_VK_DOWN/)         0x28
### [XPLM_VK_SELECT](/sdk/XPLM_VK_SELECT/)
    
    #define [XPLM_VK_SELECT](/sdk/XPLM_VK_SELECT/)       0x29
### [XPLM_VK_PRINT](/sdk/XPLM_VK_PRINT/)
    
    #define [XPLM_VK_PRINT](/sdk/XPLM_VK_PRINT/)        0x2A
### [XPLM_VK_EXECUTE](/sdk/XPLM_VK_EXECUTE/)
    
    #define [XPLM_VK_EXECUTE](/sdk/XPLM_VK_EXECUTE/)      0x2B
### [XPLM_VK_SNAPSHOT](/sdk/XPLM_VK_SNAPSHOT/)
    
    #define [XPLM_VK_SNAPSHOT](/sdk/XPLM_VK_SNAPSHOT/)     0x2C
### [XPLM_VK_INSERT](/sdk/XPLM_VK_INSERT/)
    
    #define [XPLM_VK_INSERT](/sdk/XPLM_VK_INSERT/)       0x2D
### [XPLM_VK_DELETE](/sdk/XPLM_VK_DELETE/)
    
    #define [XPLM_VK_DELETE](/sdk/XPLM_VK_DELETE/)       0x2E
### [XPLM_VK_HELP](/sdk/XPLM_VK_HELP/)
    
    #define [XPLM_VK_HELP](/sdk/XPLM_VK_HELP/)         0x2F
### [XPLM_VK_0](/sdk/XPLM_VK_0/)
    
    #define [XPLM_VK_0](/sdk/XPLM_VK_0/)            0x30
[XPLM_VK_0](/sdk/XPLM_VK_0/) thru [XPLM_VK_9](/sdk/XPLM_VK_9/) are the same as ASCII '0' thru '9' (0x30 - 0x39)
### [XPLM_VK_1](/sdk/XPLM_VK_1/)
    
    #define [XPLM_VK_1](/sdk/XPLM_VK_1/)            0x31
### [XPLM_VK_2](/sdk/XPLM_VK_2/)
    
    #define [XPLM_VK_2](/sdk/XPLM_VK_2/)            0x32
### [XPLM_VK_3](/sdk/XPLM_VK_3/)
    
    #define [XPLM_VK_3](/sdk/XPLM_VK_3/)            0x33
### [XPLM_VK_4](/sdk/XPLM_VK_4/)
    
    #define [XPLM_VK_4](/sdk/XPLM_VK_4/)            0x34
### [XPLM_VK_5](/sdk/XPLM_VK_5/)
    
    #define [XPLM_VK_5](/sdk/XPLM_VK_5/)            0x35
### [XPLM_VK_6](/sdk/XPLM_VK_6/)
    
    #define [XPLM_VK_6](/sdk/XPLM_VK_6/)            0x36
### [XPLM_VK_7](/sdk/XPLM_VK_7/)
    
    #define [XPLM_VK_7](/sdk/XPLM_VK_7/)            0x37
### [XPLM_VK_8](/sdk/XPLM_VK_8/)
    
    #define [XPLM_VK_8](/sdk/XPLM_VK_8/)            0x38
### [XPLM_VK_9](/sdk/XPLM_VK_9/)
    
    #define [XPLM_VK_9](/sdk/XPLM_VK_9/)            0x39
### [XPLM_VK_A](/sdk/XPLM_VK_A/)
    
    #define [XPLM_VK_A](/sdk/XPLM_VK_A/)            0x41
[XPLM_VK_A](/sdk/XPLM_VK_A/) thru [XPLM_VK_Z](/sdk/XPLM_VK_Z/) are the same as ASCII 'A' thru 'Z' (0x41 - 0x5A)
### [XPLM_VK_B](/sdk/XPLM_VK_B/)
    
    #define [XPLM_VK_B](/sdk/XPLM_VK_B/)            0x42
### [XPLM_VK_C](/sdk/XPLM_VK_C/)
    
    #define [XPLM_VK_C](/sdk/XPLM_VK_C/)            0x43
### [XPLM_VK_D](/sdk/XPLM_VK_D/)
    
    #define [XPLM_VK_D](/sdk/XPLM_VK_D/)            0x44
### [XPLM_VK_E](/sdk/XPLM_VK_E/)
    
    #define [XPLM_VK_E](/sdk/XPLM_VK_E/)            0x45
### [XPLM_VK_F](/sdk/XPLM_VK_F/)
    
    #define [XPLM_VK_F](/sdk/XPLM_VK_F/)            0x46
### [XPLM_VK_G](/sdk/XPLM_VK_G/)
    
    #define [XPLM_VK_G](/sdk/XPLM_VK_G/)            0x47
### [XPLM_VK_H](/sdk/XPLM_VK_H/)
    
    #define [XPLM_VK_H](/sdk/XPLM_VK_H/)            0x48
### [XPLM_VK_I](/sdk/XPLM_VK_I/)
    
    #define [XPLM_VK_I](/sdk/XPLM_VK_I/)            0x49
### [XPLM_VK_J](/sdk/XPLM_VK_J/)
    
    #define [XPLM_VK_J](/sdk/XPLM_VK_J/)            0x4A
### [XPLM_VK_K](/sdk/XPLM_VK_K/)
    
    #define [XPLM_VK_K](/sdk/XPLM_VK_K/)            0x4B
### [XPLM_VK_L](/sdk/XPLM_VK_L/)
    
    #define [XPLM_VK_L](/sdk/XPLM_VK_L/)            0x4C
### [XPLM_VK_M](/sdk/XPLM_VK_M/)
    
    #define [XPLM_VK_M](/sdk/XPLM_VK_M/)            0x4D
### [XPLM_VK_N](/sdk/XPLM_VK_N/)
    
    #define [XPLM_VK_N](/sdk/XPLM_VK_N/)            0x4E
### [XPLM_VK_O](/sdk/XPLM_VK_O/)
    
    #define [XPLM_VK_O](/sdk/XPLM_VK_O/)            0x4F
### [XPLM_VK_P](/sdk/XPLM_VK_P/)
    
    #define [XPLM_VK_P](/sdk/XPLM_VK_P/)            0x50
### [XPLM_VK_Q](/sdk/XPLM_VK_Q/)
    
    #define [XPLM_VK_Q](/sdk/XPLM_VK_Q/)            0x51
### [XPLM_VK_R](/sdk/XPLM_VK_R/)
    
    #define [XPLM_VK_R](/sdk/XPLM_VK_R/)            0x52
### [XPLM_VK_S](/sdk/XPLM_VK_S/)
    
    #define [XPLM_VK_S](/sdk/XPLM_VK_S/)            0x53
### [XPLM_VK_T](/sdk/XPLM_VK_T/)
    
    #define [XPLM_VK_T](/sdk/XPLM_VK_T/)            0x54
### [XPLM_VK_U](/sdk/XPLM_VK_U/)
    
    #define [XPLM_VK_U](/sdk/XPLM_VK_U/)            0x55
### [XPLM_VK_V](/sdk/XPLM_VK_V/)
    
    #define [XPLM_VK_V](/sdk/XPLM_VK_V/)            0x56
### [XPLM_VK_W](/sdk/XPLM_VK_W/)
    
    #define [XPLM_VK_W](/sdk/XPLM_VK_W/)            0x57
### [XPLM_VK_X](/sdk/XPLM_VK_X/)
    
    #define [XPLM_VK_X](/sdk/XPLM_VK_X/)            0x58
### [XPLM_VK_Y](/sdk/XPLM_VK_Y/)
    
    #define [XPLM_VK_Y](/sdk/XPLM_VK_Y/)            0x59
### [XPLM_VK_Z](/sdk/XPLM_VK_Z/)
    
    #define [XPLM_VK_Z](/sdk/XPLM_VK_Z/)            0x5A
### [XPLM_VK_NUMPAD0](/sdk/XPLM_VK_NUMPAD0/)
    
    #define [XPLM_VK_NUMPAD0](/sdk/XPLM_VK_NUMPAD0/)      0x60
### [XPLM_VK_NUMPAD1](/sdk/XPLM_VK_NUMPAD1/)
    
    #define [XPLM_VK_NUMPAD1](/sdk/XPLM_VK_NUMPAD1/)      0x61
### [XPLM_VK_NUMPAD2](/sdk/XPLM_VK_NUMPAD2/)
    
    #define [XPLM_VK_NUMPAD2](/sdk/XPLM_VK_NUMPAD2/)      0x62
### [XPLM_VK_NUMPAD3](/sdk/XPLM_VK_NUMPAD3/)
    
    #define [XPLM_VK_NUMPAD3](/sdk/XPLM_VK_NUMPAD3/)      0x63
### [XPLM_VK_NUMPAD4](/sdk/XPLM_VK_NUMPAD4/)
    
    #define [XPLM_VK_NUMPAD4](/sdk/XPLM_VK_NUMPAD4/)      0x64
### [XPLM_VK_NUMPAD5](/sdk/XPLM_VK_NUMPAD5/)
    
    #define [XPLM_VK_NUMPAD5](/sdk/XPLM_VK_NUMPAD5/)      0x65
### [XPLM_VK_NUMPAD6](/sdk/XPLM_VK_NUMPAD6/)
    
    #define [XPLM_VK_NUMPAD6](/sdk/XPLM_VK_NUMPAD6/)      0x66
### [XPLM_VK_NUMPAD7](/sdk/XPLM_VK_NUMPAD7/)
    
    #define [XPLM_VK_NUMPAD7](/sdk/XPLM_VK_NUMPAD7/)      0x67
### [XPLM_VK_NUMPAD8](/sdk/XPLM_VK_NUMPAD8/)
    
    #define [XPLM_VK_NUMPAD8](/sdk/XPLM_VK_NUMPAD8/)      0x68
### [XPLM_VK_NUMPAD9](/sdk/XPLM_VK_NUMPAD9/)
    
    #define [XPLM_VK_NUMPAD9](/sdk/XPLM_VK_NUMPAD9/)      0x69
### [XPLM_VK_MULTIPLY](/sdk/XPLM_VK_MULTIPLY/)
    
    #define [XPLM_VK_MULTIPLY](/sdk/XPLM_VK_MULTIPLY/)     0x6A
### [XPLM_VK_ADD](/sdk/XPLM_VK_ADD/)
    
    #define [XPLM_VK_ADD](/sdk/XPLM_VK_ADD/)          0x6B
### [XPLM_VK_SEPARATOR](/sdk/XPLM_VK_SEPARATOR/)
    
    #define [XPLM_VK_SEPARATOR](/sdk/XPLM_VK_SEPARATOR/)    0x6C
### [XPLM_VK_SUBTRACT](/sdk/XPLM_VK_SUBTRACT/)
    
    #define [XPLM_VK_SUBTRACT](/sdk/XPLM_VK_SUBTRACT/)     0x6D
### [XPLM_VK_DECIMAL](/sdk/XPLM_VK_DECIMAL/)
    
    #define [XPLM_VK_DECIMAL](/sdk/XPLM_VK_DECIMAL/)      0x6E
### [XPLM_VK_DIVIDE](/sdk/XPLM_VK_DIVIDE/)
    
    #define [XPLM_VK_DIVIDE](/sdk/XPLM_VK_DIVIDE/)       0x6F
### [XPLM_VK_F1](/sdk/XPLM_VK_F1/)
    
    #define [XPLM_VK_F1](/sdk/XPLM_VK_F1/)           0x70
### [XPLM_VK_F2](/sdk/XPLM_VK_F2/)
    
    #define [XPLM_VK_F2](/sdk/XPLM_VK_F2/)           0x71
### [XPLM_VK_F3](/sdk/XPLM_VK_F3/)
    
    #define [XPLM_VK_F3](/sdk/XPLM_VK_F3/)           0x72
### [XPLM_VK_F4](/sdk/XPLM_VK_F4/)
    
    #define [XPLM_VK_F4](/sdk/XPLM_VK_F4/)           0x73
### [XPLM_VK_F5](/sdk/XPLM_VK_F5/)
    
    #define [XPLM_VK_F5](/sdk/XPLM_VK_F5/)           0x74
### [XPLM_VK_F6](/sdk/XPLM_VK_F6/)
    
    #define [XPLM_VK_F6](/sdk/XPLM_VK_F6/)           0x75
### [XPLM_VK_F7](/sdk/XPLM_VK_F7/)
    
    #define [XPLM_VK_F7](/sdk/XPLM_VK_F7/)           0x76
### [XPLM_VK_F8](/sdk/XPLM_VK_F8/)
    
    #define [XPLM_VK_F8](/sdk/XPLM_VK_F8/)           0x77
### [XPLM_VK_F9](/sdk/XPLM_VK_F9/)
    
    #define [XPLM_VK_F9](/sdk/XPLM_VK_F9/)           0x78
### [XPLM_VK_F10](/sdk/XPLM_VK_F10/)
    
    #define [XPLM_VK_F10](/sdk/XPLM_VK_F10/)          0x79
### [XPLM_VK_F11](/sdk/XPLM_VK_F11/)
    
    #define [XPLM_VK_F11](/sdk/XPLM_VK_F11/)          0x7A
### [XPLM_VK_F12](/sdk/XPLM_VK_F12/)
    
    #define [XPLM_VK_F12](/sdk/XPLM_VK_F12/)          0x7B
### [XPLM_VK_F13](/sdk/XPLM_VK_F13/)
    
    #define [XPLM_VK_F13](/sdk/XPLM_VK_F13/)          0x7C
### [XPLM_VK_F14](/sdk/XPLM_VK_F14/)
    
    #define [XPLM_VK_F14](/sdk/XPLM_VK_F14/)          0x7D
### [XPLM_VK_F15](/sdk/XPLM_VK_F15/)
    
    #define [XPLM_VK_F15](/sdk/XPLM_VK_F15/)          0x7E
### [XPLM_VK_F16](/sdk/XPLM_VK_F16/)
    
    #define [XPLM_VK_F16](/sdk/XPLM_VK_F16/)          0x7F
### [XPLM_VK_F17](/sdk/XPLM_VK_F17/)
    
    #define [XPLM_VK_F17](/sdk/XPLM_VK_F17/)          0x80
### [XPLM_VK_F18](/sdk/XPLM_VK_F18/)
    
    #define [XPLM_VK_F18](/sdk/XPLM_VK_F18/)          0x81
### [XPLM_VK_F19](/sdk/XPLM_VK_F19/)
    
    #define [XPLM_VK_F19](/sdk/XPLM_VK_F19/)          0x82
### [XPLM_VK_F20](/sdk/XPLM_VK_F20/)
    
    #define [XPLM_VK_F20](/sdk/XPLM_VK_F20/)          0x83
### [XPLM_VK_F21](/sdk/XPLM_VK_F21/)
    
    #define [XPLM_VK_F21](/sdk/XPLM_VK_F21/)          0x84
### [XPLM_VK_F22](/sdk/XPLM_VK_F22/)
    
    #define [XPLM_VK_F22](/sdk/XPLM_VK_F22/)          0x85
### [XPLM_VK_F23](/sdk/XPLM_VK_F23/)
    
    #define [XPLM_VK_F23](/sdk/XPLM_VK_F23/)          0x86
### [XPLM_VK_F24](/sdk/XPLM_VK_F24/)
    
    #define [XPLM_VK_F24](/sdk/XPLM_VK_F24/)          0x87
### [XPLM_VK_EQUAL](/sdk/XPLM_VK_EQUAL/)
    
    #define [XPLM_VK_EQUAL](/sdk/XPLM_VK_EQUAL/)        0xB0
The following definitions are extended and are not based on the Microsoft key set.
### [XPLM_VK_MINUS](/sdk/XPLM_VK_MINUS/)
    
    #define [XPLM_VK_MINUS](/sdk/XPLM_VK_MINUS/)        0xB1
### [XPLM_VK_RBRACE](/sdk/XPLM_VK_RBRACE/)
    
    #define [XPLM_VK_RBRACE](/sdk/XPLM_VK_RBRACE/)       0xB2
### [XPLM_VK_LBRACE](/sdk/XPLM_VK_LBRACE/)
    
    #define [XPLM_VK_LBRACE](/sdk/XPLM_VK_LBRACE/)       0xB3
### [XPLM_VK_QUOTE](/sdk/XPLM_VK_QUOTE/)
    
    #define [XPLM_VK_QUOTE](/sdk/XPLM_VK_QUOTE/)        0xB4
### [XPLM_VK_SEMICOLON](/sdk/XPLM_VK_SEMICOLON/)
    
    #define [XPLM_VK_SEMICOLON](/sdk/XPLM_VK_SEMICOLON/)    0xB5
### [XPLM_VK_BACKSLASH](/sdk/XPLM_VK_BACKSLASH/)
    
    #define [XPLM_VK_BACKSLASH](/sdk/XPLM_VK_BACKSLASH/)    0xB6
### [XPLM_VK_COMMA](/sdk/XPLM_VK_COMMA/)
    
    #define [XPLM_VK_COMMA](/sdk/XPLM_VK_COMMA/)        0xB7
### [XPLM_VK_SLASH](/sdk/XPLM_VK_SLASH/)
    
    #define [XPLM_VK_SLASH](/sdk/XPLM_VK_SLASH/)        0xB8
### [XPLM_VK_PERIOD](/sdk/XPLM_VK_PERIOD/)
    
    #define [XPLM_VK_PERIOD](/sdk/XPLM_VK_PERIOD/)       0xB9
### [XPLM_VK_BACKQUOTE](/sdk/XPLM_VK_BACKQUOTE/)
    
    #define [XPLM_VK_BACKQUOTE](/sdk/XPLM_VK_BACKQUOTE/)    0xBA
### [XPLM_VK_ENTER](/sdk/XPLM_VK_ENTER/)
    
    #define [XPLM_VK_ENTER](/sdk/XPLM_VK_ENTER/)        0xBB
### [XPLM_VK_NUMPAD_ENT](/sdk/XPLM_VK_NUMPAD_ENT/)
    
    #define [XPLM_VK_NUMPAD_ENT](/sdk/XPLM_VK_NUMPAD_ENT/)   0xBC
### [XPLM_VK_NUMPAD_EQ](/sdk/XPLM_VK_NUMPAD_EQ/)
    
    #define [XPLM_VK_NUMPAD_EQ](/sdk/XPLM_VK_NUMPAD_EQ/)    0xBD
### [XPLMFixedString150_t](/sdk/XPLMFixedString150_t/)
A container for a fixed-size string buffer of 150 characters.
    
    typedef struct {
         // The size of the struct.
         char                      buffer[150];
    } [XPLMFixedString150_t](/sdk/XPLMFixedString150_t/);
### [XPLMCursorStatus](/sdk/XPLMCursorStatus/)
[XPLMCursorStatus](/sdk/XPLMCursorStatus/) describes how you would like X-Plane to manage the cursor. See [XPLMHandleCursor_f](/sdk/XPLMHandleCursor_f/) for more info.
Name| Value| Description  
---|---|---  
[xplm_CursorDefault](/sdk/xplm_CursorDefault/)| "0"|  X-Plane manages the cursor normally, plugin does not affect the cusrsor.  
[xplm_CursorHidden](/sdk/xplm_CursorHidden/)| "1"|  X-Plane hides the cursor.  
[xplm_CursorArrow](/sdk/xplm_CursorArrow/)| "2"|  X-Plane shows the cursor as the default arrow.  
[xplm_CursorCustom](/sdk/xplm_CursorCustom/)| "3"|  X-Plane shows the cursor but lets you select an OS cursor.  
[xplm_CursorRotateSmall](/sdk/xplm_CursorRotateSmall/)| "4"|  X-Plane shows a small bi-directional knob-rotating cursor.  
[xplm_CursorRotateSmallLeft](/sdk/xplm_CursorRotateSmallLeft/)| "5"|  X-Plane shows a small counter-clockwise knob-rotating cursor.  
[xplm_CursorRotateSmallRight](/sdk/xplm_CursorRotateSmallRight/)| "6"|  X-Plane shows a small clockwise knob-rotating cursor.  
[xplm_CursorRotateMedium](/sdk/xplm_CursorRotateMedium/)| "7"|  X-Plane shows a medium bi-directional knob-rotating cursor.  
[xplm_CursorRotateMediumLeft](/sdk/xplm_CursorRotateMediumLeft/)| "8"|  X-Plane shows a medium counter-clockwise knob-rotating cursor.  
[xplm_CursorRotateMediumRight](/sdk/xplm_CursorRotateMediumRight/)| "9"|  X-Plane shows a medium clockwise knob-rotating cursor.  
[xplm_CursorRotateLarge](/sdk/xplm_CursorRotateLarge/)| "10"|  X-Plane shows a large bi-directional knob-rotating cursor.  
[xplm_CursorRotateLargeLeft](/sdk/xplm_CursorRotateLargeLeft/)| "11"|  X-Plane shows a large counter-clockwise knob-rotating cursor.  
[xplm_CursorRotateLargeRight](/sdk/xplm_CursorRotateLargeRight/)| "12"|  X-Plane shows a large clockwise knob-rotating cursor.  
[xplm_CursorUpDown](/sdk/xplm_CursorUpDown/)| "13"|  X-Plane shows an up-and-down arrows cursor.  
[xplm_CursorDown](/sdk/xplm_CursorDown/)| "14"|  X-Plane shows a down arrow cursor.  
[xplm_CursorUp](/sdk/xplm_CursorUp/)| "15"|  X-Plane shows an up arrow cursor.  
[xplm_CursorLeftRight](/sdk/xplm_CursorLeftRight/)| "16"|  X-Plane shows a left-right arrow cursor.  
[xplm_CursorLeft](/sdk/xplm_CursorLeft/)| "17"|  X-Plane shows a left arrow cursor.  
[xplm_CursorRight](/sdk/xplm_CursorRight/)| "18"|  X-Plane shows a right arrow cursor.  
[xplm_CursorButton](/sdk/xplm_CursorButton/)| "19"|  X-Plane shows a button-pushing cursor.  
[xplm_CursorHandle](/sdk/xplm_CursorHandle/)| "20"|  X-Plane shows a handle-grabbing cursor.  
[xplm_CursorFourArrows](/sdk/xplm_CursorFourArrows/)| "21"|  X-Plane shows a four-arrows cursor.  
[xplm_CursorSplitterH](/sdk/xplm_CursorSplitterH/)| "22"|  X-Plane shows a cursor to drag a horizontal splitter bar.  
[xplm_CursorSplitterV](/sdk/xplm_CursorSplitterV/)| "23"|  X-Plane shows a cursor to drag a vertical splitter bar.  
[xplm_CursorText](/sdk/xplm_CursorText/)| "24"|  X-Plane shows an I-Beam cursor for text editing.  
### [XPLMMouseStatus](/sdk/XPLMMouseStatus/)
    
    When the mouse is clicked, your mouse click routine is called repeatedly.  It is first called with the
    mouse down message.  It is then called zero or more times with the mouse-drag message, and finally it
    is called once with the mouse up message.  All of these messages will be directed to the same window;
    you are guaranteed to not receive a drag or mouse-up event without first receiving the corresponding mouse-down.
    
Name| Value| Description  
---|---|---  
[xplm_MouseDown](/sdk/xplm_MouseDown/)| "1"|   
[xplm_MouseDrag](/sdk/xplm_MouseDrag/)| "2"|   
[xplm_MouseUp](/sdk/xplm_MouseUp/)| "3"|
