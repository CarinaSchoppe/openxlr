cmake_minimum_required(VERSION 3.20)
project(openxlr_vst3_host LANGUAGES CXX)

set(SDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/vst3sdk")

add_library(vst3_base STATIC
  ${SDK_ROOT}/base/source/baseiids.cpp
  ${SDK_ROOT}/base/source/fbuffer.cpp
  ${SDK_ROOT}/base/source/fdebug.cpp
  ${SDK_ROOT}/base/source/fdynlib.cpp
  ${SDK_ROOT}/base/source/fobject.cpp
  ${SDK_ROOT}/base/source/fstreamer.cpp
  ${SDK_ROOT}/base/source/fstring.cpp
  ${SDK_ROOT}/base/source/timer.cpp
  ${SDK_ROOT}/base/source/updatehandler.cpp
  ${SDK_ROOT}/base/thread/source/fcondition.cpp
  ${SDK_ROOT}/base/thread/source/flock.cpp)

add_library(vst3_interfaces STATIC
  ${SDK_ROOT}/pluginterfaces/base/conststringtable.cpp
  ${SDK_ROOT}/pluginterfaces/base/coreiids.cpp
  ${SDK_ROOT}/pluginterfaces/base/funknown.cpp
  ${SDK_ROOT}/pluginterfaces/base/ustring.cpp)

add_library(vst3_common STATIC
  ${SDK_ROOT}/public.sdk/source/common/commoniids.cpp
  ${SDK_ROOT}/public.sdk/source/common/commonstringconvert.cpp
  ${SDK_ROOT}/public.sdk/source/common/memorystream.cpp
  ${SDK_ROOT}/public.sdk/source/common/openurl.cpp
  ${SDK_ROOT}/public.sdk/source/common/readfile.cpp
  ${SDK_ROOT}/public.sdk/source/common/systemclipboard_linux.cpp
  ${SDK_ROOT}/public.sdk/source/common/threadchecker_linux.cpp
  ${SDK_ROOT}/public.sdk/source/vst/vstpresetfile.cpp)

add_library(vst3_hosting STATIC
  ${SDK_ROOT}/public.sdk/source/vst/hosting/connectionproxy.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/eventlist.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/hostclasses.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/module.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/module_linux.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/parameterchanges.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/plugprovider.cpp
  ${SDK_ROOT}/public.sdk/source/vst/hosting/processdata.cpp
  ${SDK_ROOT}/public.sdk/source/vst/utility/stringconvert.cpp
  ${SDK_ROOT}/public.sdk/source/vst/vstinitiids.cpp)

foreach(target vst3_base vst3_interfaces vst3_common vst3_hosting)
  target_include_directories(${target} PUBLIC ${SDK_ROOT})
  target_compile_features(${target} PUBLIC cxx_std_17)
  target_compile_definitions(${target} PRIVATE RELEASE=1)
  set_target_properties(${target} PROPERTIES POSITION_INDEPENDENT_CODE ON)
endforeach()
target_link_libraries(vst3_common PUBLIC vst3_base vst3_interfaces)
target_link_libraries(vst3_hosting PUBLIC vst3_common dl pthread)

find_package(PkgConfig REQUIRED)
pkg_check_modules(PIPEWIRE REQUIRED IMPORTED_TARGET libpipewire-0.3)
find_package(X11 REQUIRED)

add_executable(openxlr-vst3-host ../vst3-host.cpp)
target_include_directories(openxlr-vst3-host PRIVATE ${SDK_ROOT})
target_compile_features(openxlr-vst3-host PRIVATE cxx_std_20)
target_compile_options(openxlr-vst3-host PRIVATE -O2 -g -Wall -Wextra -Werror)
target_link_libraries(openxlr-vst3-host PRIVATE vst3_hosting PkgConfig::PIPEWIRE X11::X11 dl pthread)
