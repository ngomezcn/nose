# Writing Plugins in Languages Other Than C - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

As of X-Plane 11.30, the X-Plane SDK is only ships with official support for Delphi and C language bindings.
However, there are a number of unofficial, third-party "wrappers" for writing plugins in other language.
Note that we make every effort to keep this page up to date, but we can't make any guarantees. (You can send to additions & corrections to Tyler at X-Plane.com, though.)
  * **C++** : The [Plugin Patterns Library (PPL)](https://github.com/PhilippMuenzel/PPL) and [xplm-plus-plus](https://github.com/murrellrr/xplm-plus-plus) are two projects that provide C++ wrappers around various C APIs.
  * **Rust** : Support for everything but GUI features is available via [rust-xplm](https://github.com/samcrow/rust-xplm) (and the corresponding [xplm crate](https://docs.rs/crate/xplm/))
  * **Python** : [The Python Interface Plugin](http://www.xpluginsdk.org/python_interface_downloads.htm) is no longer being maintained, but may be useful for plugins supporting old versions of X-Plane. It only supports Python 2.7.
  * **Nim** : [XPLM-Nim](https://github.com/jpoirier/XPLM-Nim) supports older versions of the XPLM, and there's [a sample plugin](https://github.com/jpoirier/xplane-commviewer-plugin) written using it.
