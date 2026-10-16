@echo off
rem Prelozi ResearchLockedRaidGear.dll primo do ..\Assemblies (zavri predtim RimWorld)
cd /d "%~dp0"
dotnet build "ResearchLockedRaidGear.csproj" -c Release
if errorlevel 1 (echo. & echo CHYBA pri prekladu & pause & exit /b 1)
echo Hotovo: ..\Assemblies\ResearchLockedRaidGear.dll
pause
