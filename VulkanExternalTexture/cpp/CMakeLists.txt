cmake_minimum_required(VERSION 3.22)
project(questvulkanext CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED UNITY_PLUGIN_API_DIR AND DEFINED ENV{UNITY_PLUGIN_API_DIR})
    set(UNITY_PLUGIN_API_DIR "$ENV{UNITY_PLUGIN_API_DIR}")
endif()

if(NOT DEFINED UNITY_PLUGIN_API_DIR AND DEFINED ENV{UNITY_EDITOR_DIR})
    set(_unity_plugin_api_from_editor "$ENV{UNITY_EDITOR_DIR}/Editor/Data/PluginAPI")
    if(EXISTS "${_unity_plugin_api_from_editor}/IUnityGraphicsVulkan.h")
        set(UNITY_PLUGIN_API_DIR "${_unity_plugin_api_from_editor}")
    endif()
endif()

if(NOT DEFINED UNITY_PLUGIN_API_DIR OR NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityGraphicsVulkan.h")
    message(FATAL_ERROR "UNITY_PLUGIN_API_DIR is not set to a valid Unity PluginAPI directory.")
endif()

add_library(questvulkanext SHARED ${CMAKE_SOURCE_DIR}/VulkanExternalTexture.cpp)

# Unity expects the native plugin base name `unity_vulkan_hwbuffer` (see C# DllImport),
# so the produced .so must be `libunity_vulkan_hwbuffer.so`.
set_target_properties(questvulkanext PROPERTIES OUTPUT_NAME "unity_vulkan_hwbuffer")

target_include_directories(questvulkanext
    PRIVATE
        ${UNITY_PLUGIN_API_DIR}
        ${ANDROID_NDK}/sources/android/native_app_glue
)

target_compile_definitions(questvulkanext PRIVATE
    UNITY_HAS_VULKAN=1
)

find_library(log-lib log)
find_library(android-lib android)
find_library(vulkan-lib vulkan)
find_library(cppshared-lib c++_shared)

target_link_libraries(questvulkanext
    ${log-lib}
    ${android-lib}
    ${vulkan-lib}
    ${cppshared-lib}
)
