# XPWidgetUtils

Source: https://developer.x-plane.com/  (mirrored locally)

# [XPWidgetUtils](/sdk/XPWidgetUtils/) API
## USAGE NOTES
The [XPWidgetUtils](/sdk/XPWidgetUtils/) library contains useful functions that make writing and using widgets less of a pain.
One set of functions are the widget behavior functions. These functions each add specific useful behaviors to widgets. They can be used in two manners:
  1. You can add a widget behavior function to a widget as a callback proc using the [XPAddWidgetCallback](/sdk/XPAddWidgetCallback/) function. The widget will gain that behavior. Remember that the last function you add has highest priority. You can use this to change or augment the behavior of an existing finished widget.
  2. You can call a widget function from inside your own widget function. This allows you to include useful behaviors in custom-built widgets. A number of the standard widgets get their behavior from this library. To do this, call the behavior function from your function first. If it returns 1, that means it handled the event and you don't need to; simply return 1.

## GENERAL UTILITIES
### [XPWidgetCreate_t](/sdk/XPWidgetCreate_t/)
This structure contains all of the parameters needed to create a widget. It is used with [XPUCreateWidgets](/sdk/XPUCreateWidgets/) to create widgets in bulk from an array. All parameters correspond to those of [XPCreateWidget](/sdk/XPCreateWidget/) except for the container index.
If the container index is equal to the index of a widget in the array, the widget in the array passed to [XPUCreateWidgets](/sdk/XPUCreateWidgets/) is used as the parent of this widget. Note that if you pass an index greater than your own position in the array, the parent you are requesting will not exist yet.
If the container index is [NO_PARENT](/sdk/NO_PARENT/), the parent widget is specified as NULL. If the container index is [PARAM_PARENT](/sdk/PARAM_PARENT/), the widget passed into [XPUCreateWidgets](/sdk/XPUCreateWidgets/) is used.
    
    typedef struct {
         int                       left;
         int                       top;
         int                       right;
         int                       bottom;
         int                       visible;
         const char *              descriptor;
         // Whether this widget is a root widget
         int                       isRoot;
         // The index of the widget to be contained within, or a constant
         int                       containerIndex;
         [XPWidgetClass](/sdk/XPWidgetClass/)             widgetClass;
    } [XPWidgetCreate_t](/sdk/XPWidgetCreate_t/);
### [NO_PARENT](/sdk/NO_PARENT/)
    
    #define [NO_PARENT](/sdk/NO_PARENT/)            -1
### [PARAM_PARENT](/sdk/PARAM_PARENT/)
    
    #define [PARAM_PARENT](/sdk/PARAM_PARENT/)         -2
### [XPUCreateWidgets](/sdk/XPUCreateWidgets/)
    
    WIDGET_API void       [XPUCreateWidgets](/sdk/XPUCreateWidgets/)(
                             const [XPWidgetCreate_t](/sdk/XPWidgetCreate_t/) * inWidgetDefs,
                             int                  inCount,
                             [XPWidgetID](/sdk/XPWidgetID/)           inParamParent,
                             [XPWidgetID](/sdk/XPWidgetID/) *         ioWidgets);
    
This function creates a series of widgets from a table (see XPCreateWidget_t above). Pass in an array of widget creation structures and an array of widget IDs that will receive each widget.
Widget parents are specified by index into the created widget table, allowing you to create nested widget structures. You can create multiple widget trees in one table. Generally you should create widget trees from the top down.
You can also pass in a widget ID that will be used when the widget's parent is listed as [PARAM_PARENT](/sdk/PARAM_PARENT/); this allows you to embed widgets created with [XPUCreateWidgets](/sdk/XPUCreateWidgets/) in a widget created previously.
### [XPUMoveWidgetBy](/sdk/XPUMoveWidgetBy/)
    
    WIDGET_API void       [XPUMoveWidgetBy](/sdk/XPUMoveWidgetBy/)(
                             [XPWidgetID](/sdk/XPWidgetID/)           inWidget,
                             int                  inDeltaX,
                             int                  inDeltaY);
    
Simply moves a widget by an amount, +x = right, +y = up, without resizing the widget.
## LAYOUT MANAGERS
The layout managers are widget behavior functions for handling where widgets move. Layout managers can be called from a widget function or attached to a widget later.
### [XPUFixedLayout](/sdk/XPUFixedLayout/)
    
    WIDGET_API int        [XPUFixedLayout](/sdk/XPUFixedLayout/)(
                             [XPWidgetMessage](/sdk/XPWidgetMessage/)      inMessage,
                             [XPWidgetID](/sdk/XPWidgetID/)           inWidget,
                             intptr_t             inParam1,
                             intptr_t             inParam2);
    
This function causes the widget to maintain its children in fixed position relative to itself as it is resized. Use this on the top level 'window' widget for your window.
## WIDGET PROC BEHAVIORS
These widget behavior functions add other useful behaviors to widgets. These functions cannot be attached to a widget; they must be called from your widget function.
### [XPUSelectIfNeeded](/sdk/XPUSelectIfNeeded/)
    
    WIDGET_API int        [XPUSelectIfNeeded](/sdk/XPUSelectIfNeeded/)(
                             [XPWidgetMessage](/sdk/XPWidgetMessage/)      inMessage,
                             [XPWidgetID](/sdk/XPWidgetID/)           inWidget,
                             intptr_t             inParam1,
                             intptr_t             inParam2,
                             int                  inEatClick);
    
This causes the widget to bring its window to the foreground if it is not already. inEatClick specifies whether clicks in the background should be consumed by bringing the window to the foreground.
### [XPUDefocusKeyboard](/sdk/XPUDefocusKeyboard/)
    
    WIDGET_API int        [XPUDefocusKeyboard](/sdk/XPUDefocusKeyboard/)(
                             [XPWidgetMessage](/sdk/XPWidgetMessage/)      inMessage,
                             [XPWidgetID](/sdk/XPWidgetID/)           inWidget,
                             intptr_t             inParam1,
                             intptr_t             inParam2,
                             int                  inEatClick);
    
This causes the widget to send keyboard focus back to X-Plane. This stops editing of any text fields, etc.
### [XPUDragWidget](/sdk/XPUDragWidget/)
    
    WIDGET_API int        [XPUDragWidget](/sdk/XPUDragWidget/)(
                             [XPWidgetMessage](/sdk/XPWidgetMessage/)      inMessage,
                             [XPWidgetID](/sdk/XPWidgetID/)           inWidget,
                             intptr_t             inParam1,
                             intptr_t             inParam2,
                             int                  inLeft,
                             int                  inTop,
                             int                  inRight,
                             int                  inBottom);
    
[XPUDragWidget](/sdk/XPUDragWidget/) drags the widget in response to mouse clicks. Pass in not only the event, but the global coordinates of the drag region, which might be a sub-region of your widget (for example, a title bar).
