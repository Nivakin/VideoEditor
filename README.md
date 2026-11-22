# VideoEditor

Record your screen, then trim it — in one Windows app.

- **Record** a monitor or a single window, with mic and/or system audio, at 30 or 60 FPS. Encoding runs on the GPU (NVIDIA, AMD or Intel), and files stay playable even if the app or PC crashes mid-recording.
- **Trim and cut**: mark In/Out points while previewing and add the parts you want to keep. Overlapping segments merge automatically.
- **Export** the kept segments to one MP4 using FFmpeg stream copy — no re-encoding, so it's near-instant and lossless.

## Requirements

- Windows 10 or 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- FFmpeg on your `PATH` (only needed for Export):

  ```powershell
  winget install Gyan.FFmpeg
  ```

  Open a new terminal afterwards so the updated `PATH` is picked up.

## Run

```powershell
git clone https://github.com/Nivakin/VideoEditor.git
cd VideoEditor
dotnet run
```

## Usage

1. Pick a **Source** (screen or window), tick **Mic** / **System audio**, and press **Record**.
2. Press **Stop**. The recording is saved to `Videos\VideoEditor\` and opens in the editor. You can also use **Open video…** to edit any MP4.
3. Scrub to a point and press **Set In**, then **Set Out**, then **Add segment**. Repeat for every part you want to keep.
4. Press **Export…** and choose a file name.

Most controls have keyboard access keys — hold **Alt** to see them.

## Known limitations

- **Cuts snap to keyframes.** Because export doesn't re-encode, each segment starts at the nearest keyframe at or before its In point, so it may include a little extra footage at the start.
- **Exclusive-fullscreen games may record black.** Switch the game to borderless windowed mode.
- Uses the default microphone and default playback device.

## Self-check

```powershell
dotnet build
(Start-Process .\bin\x64\Debug\net10.0-windows\VideoEditor.exe --selfcheck -Wait -PassThru).ExitCode
```

Exit code `0` means the segment logic passed.

## Built with

- [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) — screen and audio capture
- [FFmpeg](https://ffmpeg.org/) — lossless cutting and joining
- WPF on .NET 10
