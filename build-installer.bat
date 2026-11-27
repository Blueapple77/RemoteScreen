@echo off
setlocal
rem Sestaví instalátor klienta: publish\RemoteScreen-Setup-<verze>.exe
cd /d "%~dp0"

set OUT=publish\client-installer
if exist "%OUT%" rmdir /s /q "%OUT%"

echo [1/2] Sestavuji klienta (vcetne .NET, aby bezel i bez nainstalovaneho .NET)...
dotnet publish Client\RemoteScreen.Client.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%OUT%"
if errorlevel 1 goto fail

call :findiscc
if defined ISCC goto compile

echo.
echo Pro vytvoreni instalatoru je potreba Inno Setup 6 (zdarma).
choice /m "Nainstalovat Inno Setup ted pres winget"
if errorlevel 2 goto noiscc
winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
call :findiscc
if not defined ISCC goto noiscc

:compile
echo.
echo [2/2] Vytvarim instalator...
"%ISCC%" /Q installer\RemoteScreen.iss
if errorlevel 1 goto fail
rmdir /s /q "%OUT%"
echo.
echo Hotovo: instalator je ve slozce publish (RemoteScreen-Setup-*.exe)
pause
exit /b 0

:findiscc
set ISCC=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
exit /b 0

:noiscc
echo.
echo Inno Setup 6 nebyl nalezen. Stahnete ho z https://jrsoftware.org/isdl.php ,
echo nainstalujte a spustte tento skript znovu.
pause
exit /b 1

:fail
echo.
echo Sestaveni selhalo.
pause
exit /b 1
