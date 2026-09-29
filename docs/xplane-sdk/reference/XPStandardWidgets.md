# XPStandardWidgets

Source: https://developer.x-plane.com/  (mirrored locally)

# [XPStandardWidgets](/sdk/XPStandardWidgets/) API
## THEORY OF OPERATION
The standard widgets are widgets built into the widgets library. While you can gain access to the widget function that drives them, you generally use them by calling [XPCreateWidget](/sdk/XPCreateWidget/) and then listening for special messages, etc.
The standard widgets often send messages to themselves when the user performs an event; these messages are sent up the widget hierarchy until they are handled. So you can add a widget proc directly to a push button (for example) to intercept the message when it is clicked, or you can put one widget proc on a window for all of the push buttons in the window. Most of these messages contain the original widget ID as a parameter so you can know which widget is messaging no matter who it is sent to.
## MAIN WINDOW
The main window widget class provides a "window" as the user knows it. These windows are draggable and can be selected. Use them to create floating windows and non-modal dialogs.
### [xpWidgetClass_MainWindow](/sdk/xpWidgetClass_MainWindow/)
    
    #define [xpWidgetClass_MainWindow](/sdk/xpWidgetClass_MainWindow/) 1
### [Main Window Type Values](/sdk/Main Window Type Values/)
These type values are used to control the appearance of a main window.
Name| Value| Description  
---|---|---  
[xpMainWindowStyle_MainWindow](/sdk/xpMainWindowStyle_MainWindow/)| "0"|  The standard main window; pin stripes on XP7, metal frame on XP 6.  
[xpMainWindowStyle_Translucent](/sdk/xpMainWindowStyle_Translucent/)| "1"|  A translucent dark gray window.  
### [Main Window Properties](/sdk/Main Window Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_MainWindowType](/sdk/xpProperty_MainWindowType/)| "1100"|  This property specifies the type of window. Set to one of the main window types above.  
[xpProperty_MainWindowHasCloseBoxes](/sdk/xpProperty_MainWindowHasCloseBoxes/)| "1200"|  This property specifies whether the main window has close boxes in its corners.  
### [MainWindow Messages](/sdk/MainWindow Messages/)
Name| Value| Description  
---|---|---  
[xpMessage_CloseButtonPushed](/sdk/xpMessage_CloseButtonPushed/)| "1200"|  This message is sent when the close buttons for your window are pressed.  
## SUB WINDOW
X-Plane dialogs are divided into separate areas; the sub window widgets allow you to make these areas. Create one main window and place several subwindows inside it. Then place your controls inside the subwindows.
### [xpWidgetClass_SubWindow](/sdk/xpWidgetClass_SubWindow/)
    
    #define [xpWidgetClass_SubWindow](/sdk/xpWidgetClass_SubWindow/) 2
### [SubWindow Type Values](/sdk/SubWindow Type Values/)
These values control the appearance of the subwindow.
Name| Value| Description  
---|---|---  
[xpSubWindowStyle_SubWindow](/sdk/xpSubWindowStyle_SubWindow/)| "0"|  A panel that sits inside a main window.  
[xpSubWindowStyle_Screen](/sdk/xpSubWindowStyle_Screen/)| "2"|  A screen that sits inside a panel for showing text information.  
[xpSubWindowStyle_ListView](/sdk/xpSubWindowStyle_ListView/)| "3"|  A list view for scrolling lists.  
### [SubWindow Properties](/sdk/SubWindow Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_SubWindowType](/sdk/xpProperty_SubWindowType/)| "1200"|  This property specifies the type of window. Set to one of the subwindow types above.  
## BUTTON
The button class provides a number of different button styles and behaviors, including push buttons, radio buttons, check boxes, etc. The button label appears on or next to the button depending on the button's appearance or type.
The button's behavior is a separate property that dictates who it highlights and what kinds of messages it sends. Since behavior and type are different, you can do strange things like make check boxes that act as push buttons or push buttons with radio button behavior.
In X-Plane 6 there were no check box graphics. The result is the following behavior: in X-Plane 6 all check box and radio buttons are round (radio-button style) buttons; in X-Plane 7 they are all square (check-box style) buttons. In a future version of X-Plane, the xpButtonBehavior enums will provide the correct graphic (check box or radio button) giving the expected result.
### [xpWidgetClass_Button](/sdk/xpWidgetClass_Button/)
    
    #define [xpWidgetClass_Button](/sdk/xpWidgetClass_Button/) 3
### [Button Types](/sdk/Button Types/)
These define the visual appearance of buttons but not how they respond to the mouse.
Name| Value| Description  
---|---|---  
[xpPushButton](/sdk/xpPushButton/)| "0"|  This is a standard push button, like an 'OK' or 'Cancel' button in a dialog box.  
[xpRadioButton](/sdk/xpRadioButton/)| "1"|  A check box or radio button. Use this and the button behaviors below to get the desired behavior.  
[xpWindowCloseBox](/sdk/xpWindowCloseBox/)| "3"|  A window close box.  
[xpLittleDownArrow](/sdk/xpLittleDownArrow/)| "5"|  A small down arrow.  
[xpLittleUpArrow](/sdk/xpLittleUpArrow/)| "6"|  A small up arrow.  
### [Button Behavior Values](/sdk/Button Behavior Values/)
These define how the button responds to mouse clicks.
Name| Value| Description  
---|---|---  
[xpButtonBehaviorPushButton](/sdk/xpButtonBehaviorPushButton/)| "0"|  Standard push button behavior. The button highlights while the mouse is clicked   
over it and unhighlights when the mouse is moved outside of it or released.   
If the mouse is released over the button, the [xpMsg_PushButtonPressed](/sdk/xpMsg_PushButtonPressed/) message   
is sent.   
  
[xpButtonBehaviorCheckBox](/sdk/xpButtonBehaviorCheckBox/)| "1"|  Check box behavior. The button immediately toggles its value when the mouse is clicked and sends out a [xpMsg_ButtonStateChanged](/sdk/xpMsg_ButtonStateChanged/) message.   
  
[xpButtonBehaviorRadioButton](/sdk/xpButtonBehaviorRadioButton/)| "2"|  Radio button behavior. The button immediately sets its state to one   
and sends out a [xpMsg_ButtonStateChanged](/sdk/xpMsg_ButtonStateChanged/) message if it was not already set   
to one. You must turn off other radio buttons in a group in your code.   
  
### [Button Properties](/sdk/Button Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_ButtonType](/sdk/xpProperty_ButtonType/)| "1300"|  This property sets the visual type of button. Use one of the button types above.  
[xpProperty_ButtonBehavior](/sdk/xpProperty_ButtonBehavior/)| "1301"|  This property sets the button's behavior. Use one of the button behaviors above.  
[xpProperty_ButtonState](/sdk/xpProperty_ButtonState/)| "1302"|  This property tells whether a check box or radio button is "checked" or not. Not used for push buttons.  
### [Button Messages](/sdk/Button Messages/)
These messages are sent by the button to itself and then up the widget chain when the button is clicked. (You may intercept them by providing a widget handler for the button itself or by providing a handler in a parent widget.)
Name| Value| Description  
---|---|---  
[xpMsg_PushButtonPressed](/sdk/xpMsg_PushButtonPressed/)| "1300"|  This message is sent when the user completes a click and release in a button with   
push button behavior. Parameter   
one of the message is the widget ID of the button. This message is dispatched up the   
widget hierarchy.   
  
[xpMsg_ButtonStateChanged](/sdk/xpMsg_ButtonStateChanged/)| "1301"|  This message is sent when a button is clicked that has radio button or check box behavior   
and its value changes. (Note that if the value changes by setting a property you do not receive   
this message!) Parameter one is the widget ID of the button, parameter 2 is the new state value,   
either zero or one. This message is dispatched up the widget hierarchy.   
  
## TEXT FIELD
The text field widget provides an editable text field including mouse selection and keyboard navigation. The contents of the text field are its descriptor. (The descriptor changes as the user types.)
The text field can have a number of types, that affect the visual layout of the text field. The text field sends messages to itself so you may control its behavior.
If you need to filter keystrokes, add a new handler and intercept the key press message. Since key presses are passed by pointer, you can modify the keystroke and pass it through to the text field widget.
WARNING: in X-Plane before 7.10 (including 6.70) null characters could crash X-Plane. To prevent this, wrap this object with a filter function (more instructions can be found on the SDK website).
### [xpWidgetClass_TextField](/sdk/xpWidgetClass_TextField/)
    
    #define [xpWidgetClass_TextField](/sdk/xpWidgetClass_TextField/) 4
### [Text Field Type Values](/sdk/Text Field Type Values/)
These control the look of the text field.
Name| Value| Description  
---|---|---  
[xpTextEntryField](/sdk/xpTextEntryField/)| "0"|  A field for text entry.  
[xpTextTransparent](/sdk/xpTextTransparent/)| "3"|  A transparent text field. The user can type and the text is drawn, but no background is drawn.   
You can draw your own background by adding a widget handler and prehandling the draw message.   
  
[xpTextTranslucent](/sdk/xpTextTranslucent/)| "4"|  A translucent edit field, dark gray.  
### [Text Field Properties](/sdk/Text Field Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_EditFieldSelStart](/sdk/xpProperty_EditFieldSelStart/)| "1400"|  This is the character position the selection starts at, zero based.   
If it is the same as the end insertion point, the insertion point   
is not a selection.   
  
[xpProperty_EditFieldSelEnd](/sdk/xpProperty_EditFieldSelEnd/)| "1401"|  This is the character position of the end of the selection.  
[xpProperty_EditFieldSelDragStart](/sdk/xpProperty_EditFieldSelDragStart/)| "1402"|  This is the character position a drag was started at if the user is dragging to select text, or -1 if a drag is not in progress.  
[xpProperty_TextFieldType](/sdk/xpProperty_TextFieldType/)| "1403"|  This is the type of text field to display, from the above list.  
[xpProperty_PasswordMode](/sdk/xpProperty_PasswordMode/)| "1404"|  Set this property to 1 to password protect the field. Characters will be drawn as *s even though the descriptor will contain plain-text.  
[xpProperty_MaxCharacters](/sdk/xpProperty_MaxCharacters/)| "1405"|  The max number of characters you can enter, if limited. Zero means unlimited.  
[xpProperty_ScrollPosition](/sdk/xpProperty_ScrollPosition/)| "1406"|  The first visible character on the left. This effectively scrolls the text field.  
[xpProperty_Font](/sdk/xpProperty_Font/)| "1407"|  The font to draw the field's text with. (An [XPLMFontID](/sdk/XPLMFontID/).)  
[xpProperty_ActiveEditSide](/sdk/xpProperty_ActiveEditSide/)| "1408"|  This is the active side of the insert selection. (Internal)  
### [Text Field Messages](/sdk/Text Field Messages/)
Name| Value| Description  
---|---|---  
[xpMsg_TextFieldChanged](/sdk/xpMsg_TextFieldChanged/)| "1400"|  The text field sends this message to itself when its text changes. It sends the message up the call chain; param1 is the text field's widget ID.  
## SCROLL BAR
A standard scroll bar or slider control. The scroll bar has a minimum, maximum and current value that is updated when the user drags it. The scroll bar sends continuous messages as it is dragged.
### [xpWidgetClass_ScrollBar](/sdk/xpWidgetClass_ScrollBar/)
    
    #define [xpWidgetClass_ScrollBar](/sdk/xpWidgetClass_ScrollBar/) 5
### [Scroll Bar Type Values](/sdk/Scroll Bar Type Values/)
This defines how the scroll bar looks.
Name| Value| Description  
---|---|---  
[xpScrollBarTypeScrollBar](/sdk/xpScrollBarTypeScrollBar/)| "0"|  A standard X-Plane scroll bar (with arrows on the ends).  
[xpScrollBarTypeSlider](/sdk/xpScrollBarTypeSlider/)| "1"|  A slider, no arrows.  
### [Scroll Bar Properties](/sdk/Scroll Bar Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_ScrollBarSliderPosition](/sdk/xpProperty_ScrollBarSliderPosition/)| "1500"|  The current position of the thumb (in between the min and max, inclusive)  
[xpProperty_ScrollBarMin](/sdk/xpProperty_ScrollBarMin/)| "1501"|  The value the scroll bar has when the thumb is in the lowest position.  
[xpProperty_ScrollBarMax](/sdk/xpProperty_ScrollBarMax/)| "1502"|  The value the scroll bar has when the thumb is in the highest position.  
[xpProperty_ScrollBarPageAmount](/sdk/xpProperty_ScrollBarPageAmount/)| "1503"|  How many units to move the scroll bar when clicking next to the thumb. The scroll bar always moves one unit when the arrows are clicked.  
[xpProperty_ScrollBarType](/sdk/xpProperty_ScrollBarType/)| "1504"|  The type of scrollbar from the enums above.  
[xpProperty_ScrollBarSlop](/sdk/xpProperty_ScrollBarSlop/)| "1505"|  Used internally.  
### [Scroll Bar Messages](/sdk/Scroll Bar Messages/)
Name| Value| Description  
---|---|---  
[xpMsg_ScrollBarSliderPositionChanged](/sdk/xpMsg_ScrollBarSliderPositionChanged/)| "1500"|  The scroll bar sends this message when the slider position changes. It sends the message up the call chain; param1 is the scroll bar widget ID.  
## CAPTION
A caption is a simple widget that shows its descriptor as a string, useful for labeling parts of a window. It always shows its descriptor as its string and is otherwise transparent.
### [xpWidgetClass_Caption](/sdk/xpWidgetClass_Caption/)
    
    #define [xpWidgetClass_Caption](/sdk/xpWidgetClass_Caption/) 6
### [Caption Properties](/sdk/Caption Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_CaptionLit](/sdk/xpProperty_CaptionLit/)| "1600"|  This property specifies whether the caption is lit; use lit captions against screens.  
## GENERAL GRAPHICS
The general graphics widget can show one of many icons available from X-Plane.
### [xpWidgetClass_GeneralGraphics](/sdk/xpWidgetClass_GeneralGraphics/)
    
    #define [xpWidgetClass_GeneralGraphics](/sdk/xpWidgetClass_GeneralGraphics/) 7
### [General Graphics Types Values](/sdk/General Graphics Types Values/)
These define the icon for the general graphics.
Name| Value| Description  
---|---|---  
[xpShip](/sdk/xpShip/)| "4"|   
[xpILSGlideScope](/sdk/xpILSGlideScope/)| "5"|   
[xpMarkerLeft](/sdk/xpMarkerLeft/)| "6"|   
[xp_Airport](/sdk/xp_Airport/)| "7"|   
[xpNDB](/sdk/xpNDB/)| "8"|   
[xpVOR](/sdk/xpVOR/)| "9"|   
[xpRadioTower](/sdk/xpRadioTower/)| "10"|   
[xpAircraftCarrier](/sdk/xpAircraftCarrier/)| "11"|   
[xpFire](/sdk/xpFire/)| "12"|   
[xpMarkerRight](/sdk/xpMarkerRight/)| "13"|   
[xpCustomObject](/sdk/xpCustomObject/)| "14"|   
[xpCoolingTower](/sdk/xpCoolingTower/)| "15"|   
[xpSmokeStack](/sdk/xpSmokeStack/)| "16"|   
[xpBuilding](/sdk/xpBuilding/)| "17"|   
[xpPowerLine](/sdk/xpPowerLine/)| "18"|   
[xpVORWithCompassRose](/sdk/xpVORWithCompassRose/)| "19"|   
[xpOilPlatform](/sdk/xpOilPlatform/)| "21"|   
[xpOilPlatformSmall](/sdk/xpOilPlatformSmall/)| "22"|   
[xpWayPoint](/sdk/xpWayPoint/)| "23"|   
### [General Graphics Properties](/sdk/General Graphics Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_GeneralGraphicsType](/sdk/xpProperty_GeneralGraphicsType/)| "1700"|  This property controls the type of icon that is drawn.  
## PROGRESS INDICATOR
This widget implements a progress indicator as seen when X-Plane starts up.
### [xpWidgetClass_Progress](/sdk/xpWidgetClass_Progress/)
    
    #define [xpWidgetClass_Progress](/sdk/xpWidgetClass_Progress/) 8
### [Progress Indicator Properties](/sdk/Progress Indicator Properties/)
Name| Value| Description  
---|---|---  
[xpProperty_ProgressPosition](/sdk/xpProperty_ProgressPosition/)| "1800"|  This is the current value of the progress indicator.  
[xpProperty_ProgressMin](/sdk/xpProperty_ProgressMin/)| "1801"|  This is the minimum value, equivalent to 0% filled.  
[xpProperty_ProgressMax](/sdk/xpProperty_ProgressMax/)| "1802"|  This is the maximum value, equivalent to 100% filled.
