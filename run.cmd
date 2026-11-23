@echo off
cd /d "%~dp0"
echo Customer account 1: http://localhost:5001
echo Customer account 2: http://localhost:5003
echo Customer account 3: http://localhost:5004
echo Admin website:    http://localhost:5002
powershell -NoProfile -Command "$busy = netstat -ano -p tcp | Select-String '^\s*TCP\s+\S+:(5001|5002|5003|5004)\s+\S+\s+LISTENING\s+\d+\s*$'; if ($busy) { Write-Host 'A required port is already in use. Do not start a second server.'; $busy | ForEach-Object { Write-Host $_ }; exit 1 }"
if errorlevel 1 (
    echo Open the running site using the links above, or stop its server before starting again.
    pause
    exit /b 1
)
dotnet watch --project CourtBook.csproj
pause
