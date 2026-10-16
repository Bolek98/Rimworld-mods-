@echo off
rem Prelozi ResearchedLoadout.dll primo do ..\Assemblies (zavri predtim RimWorld)
cd /d "%~dp0"
dotnet build "ResearchedLoadout.csproj" -c Release
if errorlevel 1 (echo. & echo CHYBA pri prekladu & pause & exit /b 1)
echo Hotovo: ..\Assemblies\ResearchedLoadout.dll
pause
