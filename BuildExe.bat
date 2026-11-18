@echo off
rem ============================================
rem  AA Image Connector build script
rem  Double-click this file to create AA_Image_Connector.exe
rem  Uses the C# compiler bundled with Windows (.NET Framework)
rem ============================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] C# compiler not found. Please enable .NET Framework 4.x.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:AA_Image_Connector.exe /win32icon:app.ico /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll AA_Image_Connector.cs
if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  pause
  exit /b 1
)

echo.
echo [OK] AA_Image_Connector.exe was created in this folder.
pause
