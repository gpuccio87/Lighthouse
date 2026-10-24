@echo off
rem Builds Lighthouse.exe with the C# compiler that ships with Windows (.NET Framework 4.8)
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll

rem 1) temporary build, used only to draw Lighthouse.ico (the icon is drawn in code)
%CSC% /out:Lighthouse.exe Lighthouse.cs Import.cs || exit /b 1
start "" /wait Lighthouse.exe --make-icon Lighthouse.ico || exit /b 1

rem 2) final build with icon and manifest
%CSC% /out:Lighthouse.exe /win32icon:Lighthouse.ico /win32manifest:Lighthouse.manifest Lighthouse.cs Import.cs || exit /b 1
echo Lighthouse.exe ready.
