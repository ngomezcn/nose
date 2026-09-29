# SDKRawData - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

This package contains some of the raw data used to generate the SDK. It is probably not of general interest, but may be useful to programmers working with lots of datarefs, or adapting plugins to other APIs.
Parsable Data References - A parsable text file can be found in X-Plane/Resources/plugins/dataRefs.txt.
## Background
Parts of the X-plane SDK are generated automatically from data, rather than hand-coded. We have a master list of data references, which are used to build glue files that go into X-plane to export the data references, as well as the HTML documentation and also generates code for the data ref tester plugin.
The headers to the SDK are originally written in XML; a parser converts them into C headers, Delphi interfaces and HTML web pages.
## Data Ref Format
The data ref file is a unix-line-ending file with one line for each data ref. Each line contains up to 5 tab separated items. (The first three are always present, the forth and fifth may be missing or contain no text.) Those items are:
  1. The full text of the data ref.
  2. The data ref's type. This will be one of: int, int[x], float, float[x], or char[x], where x is a number indicating the dimensions of an array.
  3. the letter 'y' for a writable data ref, or 'n' for a non-writable one.
  4. A description of the data ref's type (e.g. "degrees").
  5. A description of the dataref.
  6. The description may contain tabs or spaces, but no other items will.

## XML Format
The XML files contain a number of nested tags. Each tag is either a container or an an attribute. A container contains only other containers or attributes. The text between attribute tags is its value, plain text in between containers is ignored. (This will make sense when inspecting the XML.)
A few warnings: the XML system is limited to what we needed for the XPSDK API and it is very C-centric. In particular, data types are given as C data types and translated into Pascal on the fly.
The following describes the containers and what they contain.
### api tag
This represents an entire header file. It contains zero or more component tags, as well as the following attributes:
  * cout - the relative path of the C header to create.
  * put - the relative path of the Pascal interface to create.
  * hout - the relative path of the HTML doc file to create.
  * name - the header's name.
  * include - the name (with .h extension) of a header to include.
  * desc - a description of the whole API.
  * apitoken - the name of the macro used to prefix the functions for export. If this is blank or missing, the apitoken is XPLM_DLL.
  * apilib - the name of the DLL that implements the functions for this API. If this is blank or missing, the DLL is XPLM.DLL.

### component tag
This represents one group of functions, enums, etc. It can contain the typedef, enum, callback, function, struct, define, bulkenum, cdirect, or pdirect tags. It contains the following properties:
  * name - the component's human-readable name.
  * desc - a description of the component.

### typedef tag
This represents a typedef, or aliasing of one type to another. It contains the following attributes:
  * name - the name of the new type.
  * type - the name of the original type that 'name' is now an alias for.
  * desc - a description of this new type.

### enum tag
This represents a named set of enumeration values. Enumerations contain one enumitem container for each value as well as the following attributes:
  * name - the name of the new enumerated type.
  * desc - a description of the enumeration.
  * type - the type that this enumeration is based on. If this attribute is missing, then the enumeration is anonymous; the 'name' parameter is for human-use only and not a valid type in the code.

### enumitem tag
This represents a single enumerated value and has the following attributes:
  * name - the symbolic name of the enumeration item.
  * value - the value of the attribute, a decimal integer.
  * desc - a description of the enumerated value.

### callback tag
This represents the definition of a pointer to a function for use as a callback. It contains zero or more param containers, one for each parameter, as well as the following attributes:
  * name - the callback function's name.
  * type - the callback function's return type or 'void' for a procedure.
  * desc - a description of the function.

### function tag
This represents a function or procedure in the API. It contains zero or more param containers as well as the following attributes:
  * name - the function's name.
  * type - the function's return type or 'void' for a procedure.
  * desc - a description of the function.

### param tag
This represents a single argument to a function or callback. The order of these containers within a function or callback determines the call order. This attribute containers the following tags:
  * type - the type of the argument (a C type).
  * name - the argument's name in the public headers.
  * mode - the mode of argument passing. This can be one of the following constants:
  * value - the parameter is passed by value.
  * inref - the parameter is passed in by pointer but NULL may be passed. The parameter is not modified.
  * inrefreq - the parameter is passed in by pointer but may not be NULL. The parameter is not modified.
  * outref - the parameter is passed by pointer but NULL may be passed; if not NULL a value is returned via this pointer.
  * outrefreq - the parameter is passed by pointer and a value is returned through it.
  * ioref - the parameter is passed in by pointer but NULL may be passed, otherwise the value is used and then modified.
  * iorefreq - the parameter is passed by pointer and the value is used, then it is modified.

If the mode tag is left off of a parameter, a mode of value is assumed. The mode tag must be used or not used for all param containers within a single function or callback container, it can not be used for some but not all params.
### struct tag
This container represents a structured type. It contains the folling attributes:
  * name - the name of the structure.
  * desc - a description of the structure.
  * type - the type of an item in the structure.
  * tag - the name of that item in the structure.

The type and tag attributes may be specified more than once in alternating order to build up the entire struct; the order they occur in is the struct order.
### define tag
This represents an enumerated value done via a #define. These are used for one-time enumerated value of unspecified type (as opposed to enums). It contains the following attributes:
  * name - the symbolic name of the defined symbol.
  * value - the numeric value in decimal or hex (0xXX notation).
  * desc -a description of the define.

### bulk tag
This tag contains a bulk enumeration. This is one whose values start at 0 and increase sequentially. It contains the following attributes:
  * name - the name of the enum (the type that clients use).
  * desc - a description of the enum
  * data - a comma separated list of enum symbols, in order starting at an item whose value is zero. All whitespace should be ignored.

### cdirect tag
This container directly contains raw C code to be inserted into the header.
### pdirect tag
This container directly contains raw Pascal code to be inserted into the header.
