cmake_minimum_required(VERSION 3.22)
project(questvulkanext CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED UNITY_PLUGIN_API_DIR)
    message(WARNING "UNITY_PLUGIN_API_DIR not set – using default path relative to Unity 6000.2.10f1")
    set(UNITY_PLUGIN_API_DIR "D:/Program Files/Unity/Editor/6000.2.10f1/Editor/Data/PluginAPI")
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
