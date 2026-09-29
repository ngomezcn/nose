cmake_minimum_required(VERSION 3.15)
project(AICopilot CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ---------------------------------------------------------------------------
# X-Plane 12 solo soporta plugins de 64 bits.
# ---------------------------------------------------------------------------
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "X-Plane 12 solo soporta plugins de 64 bits. Compila en modo x64.")
endif()

set(SDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/third_party/XPSDK")

# El connector: solo el puente. La logica y la UI viven en src/core
# (AICopilotCore.exe), que no pasa por CMake sino por dotnet publish --
# ver tools/build_and_deploy.ps1, que compila los dos y despliega ambos.
add_library(AICopilot SHARED
    src/connector/plugin.cpp
    src/connector/PipeServer.cpp
    src/connector/DatarefRegistry.cpp
)

target_include_directories(AICopilot PRIVATE
    ${SDK_ROOT}/CHeaders/XPLM
    ${SDK_ROOT}/CHeaders/Wrappers
    src/connector
)

# Habilita toda la API moderna del SDK (hasta la 4.0, la que trae X-Plane 12).
target_compile_definitions(AICopilot PRIVATE
    XPLM200 XPLM210 XPLM300 XPLM301 XPLM400
)

# XPLM_API / etc esperan estas macros de plataforma definidas ANTES de
# incluir los headers del SDK.
if(WIN32)
    # NOMINMAX: sin esto, <windows.h> define min/max como macros y rompe
    # cualquier std::max/std::min (los usamos en Holds.h y DatarefRegistry).
    # WIN32_LEAN_AND_MEAN no se pone aqui aunque tambien nos interese: lo
    # define ya XPLMDefs.h del SDK, y ponerlo en la linea de comandos daba
    # un C4005 de redefinicion en cada fichero. Los headers propios lo
    # definen con guarda #ifndef.
    target_compile_definitions(AICopilot PRIVATE IBM=1 APL=0 LIN=0 NOMINMAX)
elseif(APPLE)
    target_compile_definitions(AICopilot PRIVATE APL=1 IBM=0 LIN=0)
else()
    target_compile_definitions(AICopilot PRIVATE LIN=1 IBM=0 APL=0)
endif()

set_target_properties(AICopilot PROPERTIES
    PREFIX ""              # nada de "lib" delante
    OUTPUT_NAME "AICopilot"
)

if(WIN32)
    set_target_properties(AICopilot PROPERTIES SUFFIX ".xpl")
    # Ya no hace falta ws2_32: el enlace con el core es un named pipe
    # (kernel32, que ya entra por defecto), no un socket UDP.
    target_link_libraries(AICopilot PRIVATE
        ${SDK_ROOT}/Libraries/Win/XPLM_64.lib
        opengl32
    )
    target_link_options(AICopilot PRIVATE -static-libgcc -static-libstdc++)
elseif(APPLE)
    set_target_properties(AICopilot PROPERTIES SUFFIX ".xpl")
    target_link_libraries(AICopilot PRIVATE
        "-F${SDK_ROOT}/Libraries/Mac" "-framework XPLM"
    )
    target_link_options(AICopilot PRIVATE -bundle)
else()
    set_target_properties(AICopilot PROPERTIES SUFFIX ".xpl")
    target_link_options(AICopilot PRIVATE -static-libgcc -static-libstdc++)
endif()

# Salida directa a un layout listo para copiar a Resources/plugins:
#   build/install/AICopilot/<plat>_x64/AICopilot.xpl
if(WIN32)
    set(PLAT_DIR "win_x64")
elseif(APPLE)
    set(PLAT_DIR "mac_x64")
else()
    set(PLAT_DIR "lin_x64")
endif()

set_target_properties(AICopilot PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/install/AICopilot/${PLAT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/install/AICopilot/${PLAT_DIR}"
)
# Con generadores multi-config (Visual Studio, Ninja Multi-Config) CMake
# añade una subcarpeta por config (.../win_x64/Release/) salvo que se fije
# también la variante por config -- se fija para que la ruta de salida sea
# siempre .../win_x64/AICopilot.xpl, tal y como documenta el README.
foreach(cfg IN ITEMS Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER ${cfg} cfg_upper)
    set_target_properties(AICopilot PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${cfg_upper} "${CMAKE_BINARY_DIR}/install/AICopilot/${PLAT_DIR}"
        LIBRARY_OUTPUT_DIRECTORY_${cfg_upper} "${CMAKE_BINARY_DIR}/install/AICopilot/${PLAT_DIR}"
    )
endforeach()
