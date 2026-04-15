@echo off
echo Starting SpawnDev.RTC Demo...
echo.
echo Signal Server: https://localhost:5571
echo Blazor Demo:   will open in browser automatically
echo.
echo Open multiple browser tabs at the same URL to test multi-peer chat.
echo Join the same room name in each tab.
echo.

start "SignalServer" dotnet run --project SpawnDev.RTC.SignalServer\SpawnDev.RTC.SignalServer.csproj
timeout /t 2 /nobreak >nul
start "BlazorDemo" dotnet run --project SpawnDev.RTC.Demo\SpawnDev.RTC.Demo.csproj --launch-profile https

echo.
echo Both servers starting. Open browser to the Blazor Demo URL.
echo Navigate to Chat Room, enter a room name, and grant camera/mic permission.
echo Open a second tab to the same URL and join the same room.
echo.
pause
