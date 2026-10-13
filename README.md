# Song Stone Loader

Source code of **Song Stone Loader**, the music tool that comes with
[Ayleid Song Stone](https://www.nexusmods.com/skyrimspecialedition/mods/195109), a Skyrim Special Edition mod by MarcsMods.

It puts the player's own music into the mod: songs, folders or .zip files dropped on its window are copied into a
music folder (`%LOCALAPPDATA%\Ayleid Song Stone\Music`), converted to Skyrim's .xwm sound format with the
Creation Kit's own ffmpeg.exe and xWMAEncode.exe, and listed in the song list the game reads
(`Data\SKSE\Plugins\StorageUtilData\SongStone\Music.json`).

It does not connect to the internet. The only programs it starts are ffmpeg.exe and xWMAEncode.exe (to convert
songs) and Explorer (the OPEN MUSIC FOLDER button). It only writes to `%LOCALAPPDATA%\Ayleid Song Stone` (music,
settings, spare versions), `Documents\Ayleid Song Stone` (backups of the player's tags and favourites), a temporary
folder while unzipping, and the mod's own folders in Skyrim's Data folder (`Sound\FX\SongStone` and
`SKSE\Plugins\StorageUtilData\SongStone`). Songs the player removes go to the Recycle Bin.

## Files

- `SongStoneLoader.cs` - the whole program (C# 5, Windows Forms, one file)
- `SongStone.ico` - its icon
- `build.bat` - builds `Song Stone Loader.exe`

## Building

Windows only. Nothing to install: it uses the C# compiler that comes with Windows (.NET Framework 4.x).

1. Download the three files into one folder.
2. Double-click `build.bat`.

It runs:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /win32icon:SongStone.ico
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:WPF\PresentationCore.dll /r:WPF\WindowsBase.dll /r:System.Xaml.dll
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll
  "/out:Song Stone Loader.exe" SongStoneLoader.cs
```

(the WPF libraries are in the `WPF` folder next to csc.exe; they play the song previews).
