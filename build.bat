@echo off
rem Builds "Song Stone Loader.exe" from SongStoneLoader.cs with the C# compiler that comes with Windows (.NET Framework 4.x).
rem No Visual Studio or downloads needed. Double-click it, or run it from any folder.
cd /d "%~dp0"
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%NET%\csc.exe" /nologo /target:winexe /optimize+ /win32icon:SongStone.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:%NET%\WPF\PresentationCore.dll" "/r:%NET%\WPF\WindowsBase.dll" /r:System.Xaml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll "/out:Song Stone Loader.exe" SongStoneLoader.cs
if errorlevel 1 (echo BUILD FAILED) else (echo Built "Song Stone Loader.exe")
pause
