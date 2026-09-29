# Testing in X-Plane - X-Plane Developer

Source: https://developer.x-plane.com/  (mirrored locally)

This document outlines how to test X-Plane via CLI commands and telnet.
Laminar Research develops tests via telnet, then passes them to X-Plane via script.
### Using telnet
Launch X-Plane with the CLI flag
    
    --testing
Then connect to the sim using the CLI command
    
    telnet localhost 49000
### List of Commands
#### acf <string>
Loads the aircraft at the specified path (be sure to use quotes). Ex: acf "Aircraft/Laminar Research/Cessna 172SP/Cessna_172SP_G1000.acf"
#### move lle <latitude> <longitude> <elevation in meters>
Reposition your plane at an exact lat/lon/elevation. Note: aircraft will be initialized with 0 airspeed, so this is best used on the ground if possible.
#### move <ICAO> <optional ramp start>
Reposition the aircraft at the specified airport. Defaults to runway 0, unless a ramp start is specified.
#### command <command> <optional time in seconds>
Execute the specified command for the specified duration, in seconds. See Commands.txt for the full list of commands. Note that the test runner is incredibly fast, so it’s recommended to add a
    
    wait
(see below) after each command.
#### Datarefs
##### dref set <dataref> <value>
Set a dataref & value. See DataRefs.txt for the full list of datarefs.
##### expect <dataref> <conditional> <value>
Checks that the specified dataref meets an expected value. Ex. expect sim/flightmodel/engine/ENGN_running[0] == 1
#### wait <time in seconds, can be a decimal>
Waits the specified time before executing the next line. Important to use after commands to ensure it fully completes before moving on. Waiting longer times, such as multiple minutes, after a move or aircraft load can be useful to ensure all aspects of a flight stabilize.
#### camera dump
Outputs current camera location to telnet.
#### camera <additional params>
Sets specific camera location. (Recommended to get the exact command from camera dump.)
#### quit
Quit the sim. Also useful to reset parameters between tests.
### Writing & Running tests
Test files must include the following header:
    
    A
    1000
    CLI_SCRIPT
Save your test script as a .txt or .test file.
Use the command line argument
    
    --script=[file name]
To launch X-Plane and run your test script. Note that X-Plane will use the existing preferences at launch. If there are syntax errors in your script, X-Plane will print the error in the terminal (Mac only) and log.txt for investigation. The results of the test run will be output in the same places as well. Finally, tests that fail any “expect <dataref>” lines will automatically save a screenshot at the fail point in the main X-Plane directory.
### Example Script
[Download a sample script here](http://developer.x-plane.com/wp-content/uploads/2021/03/script.txt).
