cmake_minimum_required(VERSION 3.15)
project(ReloadTrigger CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "X-Plane 12 solo soporta plugins de 64 bits. Compila en modo x64.")
endif()

# Reutiliza el SDK ya vendorizado en el repo de AICopilot (dos niveles
# arriba: devtools/ReloadTrigger -> AICopilot).
set(SDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/XPSDK")

add_library(ReloadTrigger SHARED
    src/reload_trigger.cpp
)

target_include_directories(ReloadTrigger PRIVATE
    ${SDK_ROOT}/CHeaders/XPLM
    ${SDK_ROOT}/CHeaders/Wrappers
)

target_compile_definitions(ReloadTrigger PRIVATE
    XPLM200 XPLM210 XPLM300 XPLM301 XPLM400
)

if(WIN32)
    target_compile_definitions(ReloadTrigger PRIVATE IBM=1 APL=0 LIN=0)
elseif(APPLE)
    target_compile_definitions(ReloadTrigger PRIVATE APL=1 IBM=0 LIN=0)
else()
    target_compile_definitions(ReloadTrigger PRIVATE LIN=1 IBM=0 APL=0)
endif()

set_target_properties(ReloadTrigger PROPERTIES
    PREFIX ""
    OUTPUT_NAME "ReloadTrigger"
)

if(WIN32)
    set_target_properties(ReloadTrigger PROPERTIES SUFFIX ".xpl")
    target_link_libraries(ReloadTrigger PRIVATE
        ${SDK_ROOT}/Libraries/Win/XPLM_64.lib
        ws2_32
    )
    target_link_options(ReloadTrigger PRIVATE -static-libgcc -static-libstdc++)
elseif(APPLE)
    set_target_properties(ReloadTrigger PROPERTIES SUFFIX ".xpl")
    target_link_libraries(ReloadTrigger PRIVATE
        "-F${SDK_ROOT}/Libraries/Mac" "-framework XPLM"
    )
    target_link_options(ReloadTrigger PRIVATE -bundle)
else()
    set_target_properties(ReloadTrigger PROPERTIES SUFFIX ".xpl")
    target_link_options(ReloadTrigger PRIVATE -static-libgcc -static-libstdc++)
endif()

if(WIN32)
    set(PLAT_DIR "win_x64")
elseif(APPLE)
    set(PLAT_DIR "mac_x64")
else()
    set(PLAT_DIR "lin_x64")
endif()

set_target_properties(ReloadTrigger PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/install/ReloadTrigger/${PLAT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/install/ReloadTrigger/${PLAT_DIR}"
)
foreach(cfg IN ITEMS Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER ${cfg} cfg_upper)
    set_target_properties(ReloadTrigger PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${cfg_upper} "${CMAKE_BINARY_DIR}/install/ReloadTrigger/${PLAT_DIR}"
        LIBRARY_OUTPUT_DIRECTORY_${cfg_upper} "${CMAKE_BINARY_DIR}/install/ReloadTrigger/${PLAT_DIR}"
    )
endforeach()
