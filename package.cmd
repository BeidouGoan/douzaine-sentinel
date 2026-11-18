@echo off
REM ============================================================
REM  package.cmd - build the debug APK and copy it to project root
REM
REM  Usage: double-click this file, or run "package.cmd" in cmd.
REM  Result: DouZaiMa-v1.0.apk in the project root.
REM
REM  NOTE: keep this file pure ASCII. cmd.exe reads .cmd as GBK on
REM        a Chinese Windows, so Chinese comments break the script
REM        ("'xxx' is not recognized"). Comments are English on purpose.
REM ============================================================
setlocal
cd /d "%~dp0"

if "%ANDROID_HOME%"=="" set "ANDROID_HOME=C:\Users\Admin\Android\Sdk"
set "GRADLE=C:\Users\Admin\WorkBuddy\tools\gradle\gradle-8.7\bin\gradle.bat"
set "APK_NAME=DouZaiMa-v1.0.apk"

if not exist "%GRADLE%" (
    echo [ERROR] gradle not found: %GRADLE%
    exit /b 1
)

echo [1/2] Building debug APK...
call "%GRADLE%" assembleDebug --no-daemon --console=plain
if errorlevel 1 (
    echo.
    echo [ERROR] build failed
    exit /b 1
)

if not exist "build\outputs\apk\debug\DouZaiMa-debug.apk" (
    echo [ERROR] apk not found after build
    exit /b 1
)

echo [2/2] Copying to project root...
copy /Y "build\outputs\apk\debug\DouZaiMa-debug.apk" "%APK_NAME%" >nul
if errorlevel 1 (
    echo [ERROR] copy failed
    exit /b 1
)

for %%F in ("%APK_NAME%") do echo OK: %%~fF  (%%~zF bytes)
endlocal
