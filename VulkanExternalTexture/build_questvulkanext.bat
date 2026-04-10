@echo off
setlocal enabledelayedexpansion

set ROOT=%~dp0
if "%ROOT:~-1%"=="\" set ROOT=%ROOT:~0,-1%

set CPP_DIR=%ROOT%\cpp
set SHADER_DIR=%CPP_DIR%\shaders
set BUILD_DIR=%ROOT%\Build

if not exist "%CPP_DIR%\VulkanExternalTexture.cpp" (
  echo ERROR: VulkanExternalTexture.cpp not found at "%CPP_DIR%".
  exit /b 1
)

set SDK_ROOT=
if defined ANDROID_SDK_ROOT set SDK_ROOT=%ANDROID_SDK_ROOT%
if not defined SDK_ROOT if defined ANDROID_HOME set SDK_ROOT=%ANDROID_HOME%
if not defined SDK_ROOT if exist "%LOCALAPPDATA%\Android\Sdk" set SDK_ROOT=%LOCALAPPDATA%\Android\Sdk

set NDK=
if defined ANDROID_NDK set NDK=%ANDROID_NDK%
if not defined NDK if defined ANDROID_NDK_HOME set NDK=%ANDROID_NDK_HOME%
if not defined NDK if defined UNITY_EDITOR_DIR if exist "%UNITY_EDITOR_DIR%\Editor\Data\PlaybackEngines\AndroidPlayer\NDK" set NDK=%UNITY_EDITOR_DIR%\Editor\Data\PlaybackEngines\AndroidPlayer\NDK
if not defined NDK if defined SDK_ROOT if exist "%SDK_ROOT%\ndk" (
  for /f "delims=" %%d in ('dir /b /ad "%SDK_ROOT%\ndk" ^| sort /r') do (
    if exist "%SDK_ROOT%\ndk\%%d\build\cmake\android.toolchain.cmake" (
      set NDK=%SDK_ROOT%\ndk\%%d
      goto :ndk_found
    )
  )
)
:ndk_found

if not defined NDK (
  echo ERROR: ANDROID_NDK not found. Set ANDROID_NDK or ANDROID_NDK_HOME.
  exit /b 1
)

if not exist "%NDK%\build\cmake\android.toolchain.cmake" (
  echo ERROR: Android toolchain not found at "%NDK%\build\cmake\android.toolchain.cmake".
  exit /b 1
)

if defined UNITY_PLUGIN_API_DIR if not exist "%UNITY_PLUGIN_API_DIR%\IUnityGraphicsVulkan.h" set UNITY_PLUGIN_API_DIR=
if not defined UNITY_PLUGIN_API_DIR if defined UNITY_EDITOR_DIR (
  if exist "%UNITY_EDITOR_DIR%\Editor\Data\PluginAPI\IUnityGraphicsVulkan.h" set UNITY_PLUGIN_API_DIR=%UNITY_EDITOR_DIR%\Editor\Data\PluginAPI
)
if not defined UNITY_PLUGIN_API_DIR call :find_unity_plugin_api_in_root "E:\Unity\Editor"
if not defined UNITY_PLUGIN_API_DIR call :find_unity_plugin_api_in_root "D:\Program Files\Unity\Editor"
if not defined UNITY_PLUGIN_API_DIR call :find_unity_plugin_api_in_root "C:\Program Files\Unity\Editor"
if not defined UNITY_PLUGIN_API_DIR call :find_unity_plugin_api_in_root "C:\Program Files\Unity Hub\Editor"
if not defined UNITY_PLUGIN_API_DIR call :find_unity_plugin_api_in_root "D:\Program Files\Unity Hub\Editor"

if not defined UNITY_PLUGIN_API_DIR (
  echo ERROR: Unity PluginAPI not found. Set UNITY_PLUGIN_API_DIR or UNITY_EDITOR_DIR.
  exit /b 1
)

set CMAKE_EXE=
if defined UNITY_EDITOR_DIR call :find_cmake_in_root "%UNITY_EDITOR_DIR%\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\cmake"
if not defined CMAKE_EXE if defined SDK_ROOT call :find_cmake_in_root "%SDK_ROOT%\cmake"
if not defined CMAKE_EXE (
  for /f "delims=" %%p in ('where cmake 2^>nul') do (
    set CMAKE_EXE=%%p
    goto :cmake_found
  )
)
:cmake_found

if not defined CMAKE_EXE (
  echo ERROR: cmake.exe not found. Install Android SDK CMake or add cmake to PATH.
  exit /b 1
)

set GLSLC=%NDK%\shader-tools\windows-x86_64\glslc.exe
if not exist "%GLSLC%" (
  if defined SDK_ROOT if exist "%SDK_ROOT%\ndk" (
    for /f "delims=" %%d in ('dir /b /ad "%SDK_ROOT%\ndk" ^| sort /r') do (
      if exist "%SDK_ROOT%\ndk\%%d\shader-tools\windows-x86_64\glslc.exe" (
        set GLSLC=%SDK_ROOT%\ndk\%%d\shader-tools\windows-x86_64\glslc.exe
        goto :glslc_found
      )
    )
  )
)
:glslc_found

if not exist "%GLSLC%" (
  echo ERROR: glslc.exe not found. Install NDK shader-tools or set ANDROID_NDK to a valid NDK.
  exit /b 1
)

set SPV_PATH=%SHADER_DIR%\ycbcr_fullscreen.frag.spv
set HDR_PATH=%SHADER_DIR%\ycbcr_fullscreen_frag_spv.h

echo [1/3] Compile fragment shader...
"%GLSLC%" -fshader-stage=frag -o "%SPV_PATH%" "%SHADER_DIR%\ycbcr_fullscreen.frag"
if errorlevel 1 exit /b 1

echo [2/3] Generate SPIR-V header...
set PYTHON_EXE=python
set PYTHON_ARGS=
python -c "import sys; sys.exit(0)" >nul 2>nul
if errorlevel 1 (
  set PYTHON_EXE=py
  set PYTHON_ARGS=-3
)
"%PYTHON_EXE%" %PYTHON_ARGS% -c "import struct, pathlib; spv=pathlib.Path(r'%SPV_PATH%'); h=pathlib.Path(r'%HDR_PATH%'); data=spv.read_bytes(); (len(data) %% 4 == 0) or (_ for _ in ()).throw(SystemExit('SPV length not multiple of 4')); words=struct.unpack('<%%dI' %% (len(data)//4), data); chunks=[words[i:i+8] for i in range(0, len(words), 8)]; lines=['#pragma once','#include <cstdint>','static const uint32_t kYcbcrFullscreenFragSpv[] = {'] + ['    ' + ', '.join(f'0x{w:08x}' for w in chunk) + (', ' if idx < len(chunks)-1 else '') for idx, chunk in enumerate(chunks)] + ['};','static const uint32_t kYcbcrFullscreenFragSpvWordCount = sizeof(kYcbcrFullscreenFragSpv) / sizeof(uint32_t);']; h.write_text('\n'.join(lines)+'\n', encoding='utf-8')"
if errorlevel 1 exit /b 1

set TOOLCHAIN=%NDK%\build\cmake\android.toolchain.cmake
set UNITY_PLUGIN_API_DIR_ARG=
if defined UNITY_PLUGIN_API_DIR set UNITY_PLUGIN_API_DIR_ARG=-DUNITY_PLUGIN_API_DIR="%UNITY_PLUGIN_API_DIR%"

if exist "%BUILD_DIR%\CMakeCache.txt" (
  echo Cleaning build directory to avoid CMake cache mismatch...
  rd /s /q "%BUILD_DIR%"
)
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"

echo [3/3] Build libunity_vulkan_hwbuffer.so...
"%CMAKE_EXE%" -S "%CPP_DIR%" -B "%BUILD_DIR%" -G Ninja -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 -DCMAKE_BUILD_TYPE=Release -DCMAKE_TOOLCHAIN_FILE="%TOOLCHAIN%" %UNITY_PLUGIN_API_DIR_ARG%
if errorlevel 1 exit /b 1

"%CMAKE_EXE%" --build "%BUILD_DIR%" --config Release
if errorlevel 1 exit /b 1

set OUTPUT_SO=%BUILD_DIR%\libunity_vulkan_hwbuffer.so
if not exist "%OUTPUT_SO%" (
  rem Backward-compat: if the build still outputs the old name, copy/rename it.
  if exist "%BUILD_DIR%\libquestvulkanext.so" (
    set OUTPUT_SO=%BUILD_DIR%\libquestvulkanext.so
  )
)

if not exist "%OUTPUT_SO%" (
  echo ERROR: Built .so not found in "%BUILD_DIR%". Expected "libunity_vulkan_hwbuffer.so".
  exit /b 1
)

for %%I in ("%ROOT%\..") do set PROJECT_ROOT=%%~fI
set DST_DIR=%PROJECT_ROOT%\Assets\Plugins\Android\arm64-v8a
if not exist "%DST_DIR%" (
  mkdir "%DST_DIR%"
  if errorlevel 1 exit /b 1
)

echo Copying "%OUTPUT_SO%" to "%DST_DIR%\libunity_vulkan_hwbuffer.so"...
copy /Y "%OUTPUT_SO%" "%DST_DIR%\libunity_vulkan_hwbuffer.so" >nul
if errorlevel 1 (
  echo ERROR: Failed to copy .so into Unity plugin folder.
  exit /b 1
)

echo Done.
echo   Build output: "%OUTPUT_SO%"
echo   Unity plugin: "%DST_DIR%\libunity_vulkan_hwbuffer.so"
endlocal
goto :eof

:find_cmake_in_root
if not defined CMAKE_EXE if exist "%~1\3.22.1\bin\cmake.exe" (
  set CMAKE_EXE=%~1\3.22.1\bin\cmake.exe
  goto :eof
)
if not defined CMAKE_EXE if exist "%~1" (
  for /f "delims=" %%d in ('dir /b /ad "%~1" ^| sort /r') do (
    if exist "%~1\%%d\bin\cmake.exe" (
      set CMAKE_EXE=%~1\%%d\bin\cmake.exe
      goto :eof
    )
  )
)
goto :eof

:find_unity_plugin_api_in_root
if not defined UNITY_PLUGIN_API_DIR if exist "%~1" (
  for /f "delims=" %%d in ('dir /b /ad "%~1" ^| sort /r') do (
    if exist "%~1\%%d\Editor\Data\PluginAPI\IUnityInterface.h" if exist "%~1\%%d\Editor\Data\PluginAPI\IUnityGraphicsVulkan.h" (
      set UNITY_PLUGIN_API_DIR=%~1\%%d\Editor\Data\PluginAPI
      goto :eof
    )
  )
)
goto :eof
