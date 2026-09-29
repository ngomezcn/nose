# Datarefs for the CDU screen - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

X-Plane 11.35b5 adds datarefs to read the contents of the X-Plane default FMS Control and Display Unit (CDU) screen.
The CDU is limited to 16 lines of text with 24 characters per line. Note that not all CDUs use all 16 lines - Some use a combined scratchpad/message line, others have an additional line under the scratchpad for messages. Some CDUs have four line selectable rows of data, others have 6. So while you can read data from CDUs of all sizes up to 16 lines, not all CDUs will fill 16 lines with content.
## Text
The datarefs
    
    sim/cockpit2/radios/indicators/fms_cdu1_text_line0
to
    
    sim/cockpit2/radios/indicators/fms_cdu1_text_line15
and
    
    sim/cockpit2/radios/indicators/fms_cdu2_text_line0
to
    
    sim/cockpit2/radios/indicators/fms_cdu2_text_line15
contain the contents of each line as an UTF-8 string. Note that one character might need more than one byte of the dataref to display. You are expected to be able to read at least the following UTF-8 characters:
  * U+00B0 (degree sign): (0xC2 0xB0)
  * U+2610 (ballot box): (0xE2 0x98 0x90)
  * U+2190 (left arrow): (0xE2 0x86 0x90) to U+2193 (downwards arrow): (0xE2 0x86 0x93)
  * U+0394 (greek capital letter delta): (0xCE 0x94)
  * U+2B21 (white hexagon): (0xE2 0xAC 0xA1)
  * U+25C0 (left-point triangle): (0xE2 0x97 0x80)
  * U+25B6 (right-pointing triangle): (0xE2 0x96 0xb6)

More special characters might be added in future versions.
## Formatting info
The datarefs
    
    sim/cockpit2/radios/indicators/fms_cdu1_style_line0
to
    
    sim/cockpit2/radios/indicators/fms_cdu1_style_line15
and
    
    sim/cockpit2/radios/indicators/fms_cdu2_style_line0
to
    
    sim/cockpit2/radios/indicators/fms_cdu2_style_line15
contain the formatting information for each character in the line as one byte (unsigned char) with the following special meaning:
  * The highest bit is set for a text displayed in large font. So use mask (1<<7) for the bit that tells you large vs small font.
  * The second highest bit is set for a text displayed in reverse video (colored background, black text). So use mask (1<<6) for the bit that tells you to invert the colors.
  * The third highest bit is set for a text displayed flashing (text being turned an and off periodically). So use mask (1<<5) for the bit that tells you to flash.
  * The fourth highest bit is set for a text with an underscore. So use mask (1<<4) for the bit that tells you to display an underscore under the character.
  * The remaining four bits encode the color of the text (or the background for reverse video): BLACK(0),CYAN(1),RED(2),YELLOW(3),GREEN(4),MAGENTA(5),AMBER(6),WHITE(7).
