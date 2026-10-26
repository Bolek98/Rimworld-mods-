@echo off
rem Prida hlidac do Po spusteni Windows (spusti se sam po zapnuti PC).
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup')+'\Workshop hlidac.lnk'); $s.TargetPath='%~dp0Spustit_hlidac.bat'; $s.WorkingDirectory='%~dp0'; $s.WindowStyle=7; $s.Save()"
echo Hotovo - hlidac se spusti sam po zapnuti PC.
pause
