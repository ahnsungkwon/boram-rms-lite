@echo off
setlocal
set "LITE_EXE=%~dp0dist\BoramRMS_Lite\BoramRms.Lite.exe"
if not exist "%LITE_EXE%" (
  echo Boram RMS Lite executable not found. Keep the complete dist folder.
  pause
  exit /b 1
)
start "" "%LITE_EXE%" %*
