@echo off
rem Sestaví klienta pro Windows do slozky publish\client
cd /d "%~dp0"
dotnet publish Client\RemoteScreen.Client.csproj -c Release -r win-x64 --self-contained false -p:DebugType=none -o publish\client
if errorlevel 1 (pause & exit /b 1)
echo.
echo Hotovo: publish\client\RemoteScreen.exe
pause
