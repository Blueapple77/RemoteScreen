@echo off
setlocal
rem Sestaví aplikaci RemoteScreen.app pro Mac (Apple Silicon). Pro Intel Mac změňte RID na osx-x64.
set RID=osx-arm64
cd /d "%~dp0"

if exist publish\mac-build rmdir /s /q publish\mac-build
if exist publish\RemoteScreen-Mac rmdir /s /q publish\RemoteScreen-Mac

dotnet publish Server\RemoteScreen.Server.csproj -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o publish\mac-build
if errorlevel 1 (pause & exit /b 1)

set APP=publish\RemoteScreen-Mac\RemoteScreen.app\Contents
mkdir "%APP%\MacOS"
mkdir "%APP%\Resources"
copy /y publish\mac-build\RemoteScreen "%APP%\MacOS\RemoteScreen" >nul
copy /y Server\Mac\Info.plist "%APP%\Info.plist" >nul
copy /y Server\Mac\AppIcon.icns "%APP%\Resources\AppIcon.icns" >nul
copy /y install-mac.sh publish\RemoteScreen-Mac\ >nul
rmdir /s /q publish\mac-build

echo.
echo Hotovo: slozka publish\RemoteScreen-Mac obsahuje RemoteScreen.app a install-mac.sh
echo Zkopirujte celou slozku RemoteScreen-Mac na Mac a tam v Terminalu spustte:
echo     cd cesta/k/RemoteScreen-Mac ^&^& bash install-mac.sh
pause
