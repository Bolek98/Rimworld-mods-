@echo off
rem Prida widget do Po spusteni Windows - objevi se sam po zapnuti PC.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup')+'\Workshop widget.lnk'); $s.TargetPath='wscript.exe'; $s.Arguments='\"%~dp0Spustit_widget.vbs\"'; $s.WorkingDirectory='%~dp0'; $s.Save()"
echo Hotovo - widget se ukaze sam po zapnuti PC.
pause
