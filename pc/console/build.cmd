@echo off
REM ============================================================
REM  build.cmd - build "DouZaiNe" (WinForms console app)
REM
REM  Uses the .NET Framework compiler shipped with Windows.
REM  No Visual Studio required.
REM
REM    build.cmd          -> DouZaiNe.exe        (windowed)
REM    build.cmd debug    -> DouZaiNeDebug.exe  (console + --selftest)
REM
REM  NOTE: keep this file pure ASCII.
REM  cmd.exe reads .cmd as GBK on a Chinese Windows, so a Chinese
REM  comment line turns into garbage commands ("'xxx' is not
REM  recognized"). Comments here are English on purpose.
REM ============================================================
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
set OUT=%HERE%..\dist\DouZaiNe

if not exist "%CSC%" (
    echo [ERROR] C# compiler not found:
    echo         %CSC%
    echo         Need .NET Framework 4.x installed.
    exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"

set TARGET=winexe
set NAME=DouZaiNe
if /i "%1"=="debug" (
    set TARGET=exe
    set NAME=DouZaiNeDebug
)

echo Compiling (%TARGET%)...
"%CSC%" /nologo /target:%TARGET% /platform:anycpu /optimize+ ^
    /win32icon:"%HERE%..\..\docs\img\icon.ico" ^
    /res:"%HERE%..\..\docs\img\icon.ico",SentinelConsole.icon.ico ^
    /out:"%OUT%\%NAME%.exe" ^
    /r:System.Windows.Forms.dll ^
    /r:System.Drawing.dll ^
    /r:System.Web.Extensions.dll ^
    /r:System.Core.dll ^
    "%HERE%SentinelConsole.cs" ^
    "%HERE%Ui.cs"

if errorlevel 1 (
    echo.
    echo [ERROR] build failed
    exit /b 1
)

REM Two copies are made on EVERY build:
REM   DouZaiNe-zh.exe   English alias for scripts / shortcuts
REM   (Chinese-named)   the one the user double-clicks
REM The Chinese filename is created by mk_zh_alias.py on purpose:
REM cmd.exe reads .cmd as GBK on a Chinese Windows, so a UTF-8
REM Chinese name written here would come out as mojibake.
REM Python walks the Win32 API and handles it fine.
if /i not "%NAME%"=="DouZaiNeDebug" (
    python "%HERE%mk_zh_alias.py"
    if errorlevel 1 (
        echo [WARN] copy step failed - the exe itself is fine
    )
)

echo.
echo OK: %OUT%\%NAME%.exe
endlocal
