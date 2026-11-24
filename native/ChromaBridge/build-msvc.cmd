@echo off
rem Baut die Chroma-Bridge mit MSVC (in der CI): x64 -> bin\x64\RzChromaSDK64.dll, x86 -> bin\x86\RzChromaSDK.dll.
rem /MT bindet die C-Laufzeit statisch ein, die DLL braucht also keine Visual-C++-Runtime beim Spieler.
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if "%VS%"=="" (echo Visual Studio mit C++-Werkzeugen nicht gefunden & exit /b 1)
cd /d "%~dp0"
call :build x64 RzChromaSDK64.dll || exit /b 1
call :build x86 RzChromaSDK.dll || exit /b 1
echo Chroma-Bridge gebaut.
exit /b 0

:build
setlocal
if not exist bin\%1 mkdir bin\%1
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" %1 >nul || exit /b 1
cl /nologo /O2 /W3 /MT /LD chroma_bridge.c /Fobin\%1\ /Febin\%1\%2 /link ws2_32.lib ole32.lib || exit /b 1
endlocal
exit /b 0
