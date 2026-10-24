@echo off
rem Prelozi SecularColony.dll primo do ..\Assemblies (zavri predtim RimWorld)
cd /d "%~dp0"
dotnet build "SecularColony.csproj" -c Release
if errorlevel 1 (echo. & echo CHYBA pri prekladu & pause & exit /b 1)
echo Hotovo: ..\Assemblies\SecularColony.dll
pause
