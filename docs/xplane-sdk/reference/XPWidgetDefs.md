# XPWidgetDefs

Source: https://developer.x-plane.com/  (mirrored locally)

# [XPWidgetDefs](/sdk/XPWidgetDefs/) API
## WIDGET DEFINITIONS
A widget is a call-back driven screen entity like a push-button, window, text entry field, etc.
Use the widget API to create widgets of various classes. You can nest them into trees of widgets to create complex user interfaces.
### [XPWidgetID](/sdk/XPWidgetID/)
    
    typedef void * [XPWidgetID](/sdk/XPWidgetID/);
A Widget ID is an opaque unique non-zero handle identifying your widget. Use 0 to specify "no widget". This type is defined as wide enough to hold a pointer. You receive a widget ID when you create a new widget and then use that widget ID to further refer to the widget.
### [XPWidgetPropertyID](/sdk/XPWidgetPropertyID/)
Properties are values attached to instances of your widgets. A property is identified by a 32-bit ID and its value is the width of a pointer.
Each widget instance may have a property or not have it. When you set a property on a widget for the first time, the property is added to the widget; it then stays there for the life of the widget.
Some property IDs are predefined by the widget package; you can make up your own property IDs as well.
Name| Value| Description  
---|---|---  
[xpProperty_Refcon](/sdk/xpProperty_Refcon/)| "0"|  A window's refcon is an opaque value used by client code to find other data based on it.  
[xpProperty_Dragging](/sdk/xpProperty_Dragging/)| "1"|  These properties are used by the utilities to implement dragging.  
[xpProperty_DragXOff](/sdk/xpProperty_DragXOff/)| "2"|   
[xpProperty_DragYOff](/sdk/xpProperty_DragYOff/)| "3"|   
[xpProperty_Hilited](/sdk/xpProperty_Hilited/)| "4"|  Is the widget highlighted? (For widgets that support this kind of thing.)  
[xpProperty_Object](/sdk/xpProperty_Object/)| "5"|  Is there a C++ object attached to this widget?  
[xpProperty_Clip](/sdk/xpProperty_Clip/)| "6"|  If this property is 1, the widget package will use OpenGL to restrict drawing to the Widget's exposed rectangle.  
[xpProperty_Enabled](/sdk/xpProperty_Enabled/)| "7"|  Is this widget enabled (for those that have a disabled state too)?  
[xpProperty_UserStart](/sdk/xpProperty_UserStart/)| "10000"|  NOTE: Property IDs 1 - 999 are reserved for the widgets library.   
  
NOTE: Property IDs 1000 - 9999 are allocated to the standard widget classes provided with the library.   
  
Properties 1000 - 1099 are for widget class 0, 1100 - 1199 for widget class 1, etc.   
  
### [XPMouseState_t](/sdk/XPMouseState_t/)
When the mouse is clicked or dragged, a pointer to this structure is passed to your widget function.
    
    typedef struct {
         int                       x;
         int                       y;
         // Mouse button number, left = 0 (right button not yet supported.
         int                       button;
         // Scroll wheel delta (button in this case would be the wheel axis number).
         int                       delta;
    } [XPMouseState_t](/sdk/XPMouseState_t/);
### [XPKeyState_t](/sdk/XPKeyState_t/)
When a key is pressed, a pointer to this struct is passed to your widget function.
    
    typedef struct {
         // The ASCII key that was pressed.  WARNING: this may be 0 for some non-ASCII key sequences.
         char                      key;
         // The flags.  Make sure to check this if you only want key-downs!
         [XPLMKeyFlags](/sdk/XPLMKeyFlags/)              flags;
         // The virtual key code for the key
         char                      vkey;
    } [XPKeyState_t](/sdk/XPKeyState_t/);
### [XPWidgetGeometryChange_t](/sdk/XPWidgetGeometryChange_t/)
This structure contains the deltas for your widget's geometry when it changes.
    
    typedef struct {
         int                       dx;
         // +Y = the widget moved up
         int                       dy;
         int                       dwidth;
         int                       dheight;
    } [XPWidgetGeometryChange_t](/sdk/XPWidgetGeometryChange_t/);
### [XPDispatchMode](/sdk/XPDispatchMode/)
The dispatching modes describe how the widgets library sends out messages. Currently there are three modes:
Name| Value| Description  
---|---|---  
[xpMode_Direct](/sdk/xpMode_Direct/)| "0"|  The message will only be sent to the target widget.  
[xpMode_UpChain](/sdk/xpMode_UpChain/)| "1"|  The message is sent to the target widget, then up the chain of parents until the message is   
handled or a parentless widget is reached.   
  
[xpMode_Recursive](/sdk/xpMode_Recursive/)| "2"|  The message is sent to the target widget and then all of its children recursively depth-first.  
[xpMode_DirectAllCallbacks](/sdk/xpMode_DirectAllCallbacks/)| "3"|  The message is sent just to the target, but goes to every callback, even if it is handled.  
[xpMode_Once](/sdk/xpMode_Once/)| "4"|  The message is only sent to the very first handler even if it is not accepted. (This is really only useful for some internal widget library functions.)  
### [XPWidgetClass](/sdk/XPWidgetClass/)
    
    typedef int [XPWidgetClass](/sdk/XPWidgetClass/);
Widget classes define predefined widget types. A widget class basically specifies from a library the widget function to be used for the widget. Most widgets can be made right from classes.
### [xpWidgetClass_None](/sdk/xpWidgetClass_None/)
    
    #define [xpWidgetClass_None](/sdk/xpWidgetClass_None/)   0
An unspecified widget class. Other widget classes are in [XPStandardWidgets](/sdk/XPStandardWidgets/).h
## WIDGET MESSAGES
### [XPWidgetMessage](/sdk/XPWidgetMessage/)
Widgets receive 32-bit messages indicating what action is to be taken or notifications of events. The list of messages may be expanded.
Name| Value| Description  
---|---|---  
[xpMsg_None](/sdk/xpMsg_None/)| "0"|  No message, should not be sent.  
[xpMsg_Create](/sdk/xpMsg_Create/)| "1"|  The create message is sent once per widget that is created with your widget function and once   
for any widget that has your widget function attached.   
  
Dispatching: Direct   
  
Param 1: 1 if you are being added as a subclass, 0 if the widget is first being created.   
  
[xpMsg_Destroy](/sdk/xpMsg_Destroy/)| "2"|  The destroy message is sent once for each message that is destroyed that has your widget function.   
  
Dispatching: Direct for all   
  
Param 1: 1 if being deleted by a recursive delete to the parent, 0 for explicit deletion.   
  
[xpMsg_Paint](/sdk/xpMsg_Paint/)| "3"|  The paint message is sent to your widget to draw itself. The paint message is the bare-bones   
message; in response you must draw yourself, draw your children, set up clipping and culling,   
check for visibility, etc. If you don't want to do all of this, ignore the paint message and   
a draw message (see below) will be sent to you.   
  
Dispatching: Direct   
  
[xpMsg_Draw](/sdk/xpMsg_Draw/)| "4"|  The draw message is sent to your widget when it is time to draw yourself. OpenGL will be set up   
to draw in 2-d global screen coordinates, but you should use the XPLM to set up OpenGL state.   
  
Dispatching: Direct   
  
[xpMsg_KeyPress](/sdk/xpMsg_KeyPress/)| "5"|  The key press message is sent once per key that is pressed. The first parameter is the type of key   
code (integer or char) and the second is the code itself. By handling this event, you consume the   
key stroke.   
  
Handling this message 'consumes' the keystroke; not handling it passes it to your parent widget.   
  
Dispatching: Up Chain   
  
Param 1: A pointer to an [XPKeyState_t](/sdk/XPKeyState_t/) structure with the keystroke.   
  
[xpMsg_KeyTakeFocus](/sdk/xpMsg_KeyTakeFocus/)| "6"|  Keyboard focus is being given to you. By handling this message you accept keyboard focus. The   
first parameter will be one if a child of yours gave up focus to you, 0 if someone set focus on   
you explicitly.   
  
Handling this message accepts focus; not handling refuses focus.   
  
Dispatching: direct   
  
Param 1: 1 if you are gaining focus because your child is giving it up, 0 if someone is explicitly   
giving you focus.   
  
[xpMsg_KeyLoseFocus](/sdk/xpMsg_KeyLoseFocus/)| "7"|  Keyboard focus is being taken away from you. The first parameter will be 1 if you are losing   
focus because another widget is taking it, or 0 if someone called the API to make you lose focus   
explicitly.   
  
Dispatching: Direct   
  
Param 1: 1 if focus is being taken by another widget, 0 if code requested to remove focus.   
  
[xpMsg_MouseDown](/sdk/xpMsg_MouseDown/)| "8"|  You receive one mousedown event per click with a mouse-state structure pointed to by parameter 1.   
By accepting this you eat the click, otherwise your parent gets it. You will not receive drag and   
mouse up messages if you do not accept the down message.   
  
Handling this message consumes the mouse click, not handling it passes it to the next widget.   
You can act 'transparent' as a window by never handling moues clicks to certain areas.   
  
Dispatching: Up chain NOTE: Technically this is direct dispatched, but the widgets library will ship   
it to each widget until one consumes the click, making it effectively "up chain".   
  
Param 1: A pointer to an [XPMouseState_t](/sdk/XPMouseState_t/) containing the mouse status.   
  
[xpMsg_MouseDrag](/sdk/xpMsg_MouseDrag/)| "9"|  You receive a series of mouse drag messages (typically one per frame in the sim) as the mouse is   
moved once you have accepted a mouse down message. Parameter one points to a mouse-state structure   
describing the mouse location. You will continue to receive these until the mouse button is   
released.   
You may receive multiple mouse state messages with the same mouse position. You will receive mouse   
drag events even if the mouse is dragged out of your current or original bounds at the time of the   
mouse down.   
  
Dispatching: Direct   
  
Param 1: A pointer to an [XPMouseState_t](/sdk/XPMouseState_t/) containing the mouse status.   
  
[xpMsg_MouseUp](/sdk/xpMsg_MouseUp/)| "10"|  The mouseup event is sent once when the mouse button is released after a drag or click. You only   
receive this message if you accept the mouseDown message. Parameter one points to a mouse state   
structure.   
  
Dispatching: Direct   
  
Param 1: A pointer to an [XPMouseState_t](/sdk/XPMouseState_t/) containing the mouse status.   
  
[xpMsg_Reshape](/sdk/xpMsg_Reshape/)| "11"|  Your geometry or a child's geometry is being changed.   
  
Dispatching: Up chain   
  
Param 1: The widget ID of the original reshaped target.   
  
Param 2: A pointer to a [XPWidgetGeometryChange_t](/sdk/XPWidgetGeometryChange_t/) struct describing the change.   
  
[xpMsg_ExposedChanged](/sdk/xpMsg_ExposedChanged/)| "12"|  Your exposed area has changed.   
  
Dispatching: Direct   
  
[xpMsg_AcceptChild](/sdk/xpMsg_AcceptChild/)| "13"|  A child has been added to you. The child's ID is passed in parameter one.   
  
Dispatching: Direct   
  
Param 1: The Widget ID of the child being added.   
  
[xpMsg_LoseChild](/sdk/xpMsg_LoseChild/)| "14"|  A child has been removed from you. The child's ID is passed in parameter one.   
  
Dispatching: Direct   
  
Param 1: The Widget ID of the child being removed.   
  
[xpMsg_AcceptParent](/sdk/xpMsg_AcceptParent/)| "15"|  You now have a new parent, or have no parent. The parent's ID is passed in, or 0 for no parent.   
  
Dispatching: Direct   
  
Param 1: The Widget ID of your parent   
  
[xpMsg_Shown](/sdk/xpMsg_Shown/)| "16"|  You or a child has been shown. Note that this does not include you being shown because your parent   
was shown, you were put in a new parent, your root was shown, etc.   
  
Dispatching: Up chain   
  
Param 1: The widget ID of the shown widget.   
  
[xpMsg_Hidden](/sdk/xpMsg_Hidden/)| "17"|  You have been hidden. See limitations above.   
  
Dispatching: Up chain   
  
Param 1: The widget ID of the hidden widget.   
  
[xpMsg_DescriptorChanged](/sdk/xpMsg_DescriptorChanged/)| "18"|  Your descriptor has changed.   
  
Dispatching: Direct   
  
[xpMsg_PropertyChanged](/sdk/xpMsg_PropertyChanged/)| "19"|  A property has changed. Param 1 contains the property ID.   
  
Dispatching: Direct   
  
Param 1: The Property ID being changed.   
  
Param 2: The new property value   
  
[xpMsg_MouseWheel](/sdk/xpMsg_MouseWheel/)| "20"|  The mouse wheel has moved.   
  
Return 1 to consume the mouse wheel move, or 0 to pass the message to a parent.   
Dispatching: Up chain   
  
Param 1: A pointer to an [XPMouseState_t](/sdk/XPMouseState_t/) containing the mouse status.   
  
[xpMsg_CursorAdjust](/sdk/xpMsg_CursorAdjust/)| "21"|  The cursor is over your widget. If you consume this message, change the [XPLMCursorStatus](/sdk/XPLMCursorStatus/)   
value to indicate the desired result, with the same rules as in [XPLMDisplay](/sdk/XPLMDisplay/).h.   
  
Return 1 to consume this message, 0 to pass it on.   
  
Dispatching: Up chain   
Param 1: A pointer to an [XPMouseState_t](/sdk/XPMouseState_t/) struct containing the mouse status.   
  
Param 2: A pointer to a [XPLMCursorStatus](/sdk/XPLMCursorStatus/) \- set this to the cursor result you desire.   
  
[xpMsg_UserStart](/sdk/xpMsg_UserStart/)| "10000"|  NOTE: Message IDs 1000 - 9999 are allocated to the standard widget classes provided with the library   
with 1000 - 1099 for widget class 0, 1100 - 1199 for widget class 1, etc. Message IDs 10,000 and   
beyond are for plugin use.   
  
## WIDGET CALLBACK FUNCTION
### [XPWidgetFunc_t](/sdk/XPWidgetFunc_t/)
    
    typedef int (* [XPWidgetFunc_t](/sdk/XPWidgetFunc_t/))(
                             [XPWidgetMessage](/sdk/XPWidgetMessage/)      inMessage,
                             [XPWidgetID](/sdk/XPWidgetID/)           inWidget,
                             intptr_t             inParam1,
                             intptr_t             inParam2);
    
This function defines your custom widget's behavior. It will be called by the widgets library to send messages to your widget. The message and widget ID are passed in, as well as two pointer-width signed parameters whose meaning varies with the message. Return 1 to indicate that you have processed the message, 0 to indicate that you have not. For any message that is not understood, return 0.
