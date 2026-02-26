# Video Sorter

A Windows desktop application for browsing, previewing, and managing video files.

## Features

- Browse folders and preview videos with built-in player
- Thumbnail strips for quick visual scanning
- Video metadata display (resolution, codecs, duration, subtitles)
- File operations: rename, copy, move, delete
- Side-by-side video comparison with sync controls
- 3D video support (SBS / Top-Bottom cropping)
- Audio and subtitle track selection
- Sort by name, size, duration, or creation date
- Filter files by name
- Dark theme

## Requirements

- Windows 10 or later
- [ffmpeg and ffprobe](https://ffmpeg.org/download.html) on PATH

## Installation

Download `VideoSorter.exe` from the [Releases](../../releases) page.

Ensure `ffmpeg` and `ffprobe` are available on your system PATH. These are used for extracting video metadata and generating thumbnails.

## Keyboard Shortcuts

| Key | Action |
|---|---|
| Space | Play / Pause |
| Left / Right | Seek 5 seconds |
| Ctrl + Left / Right | Seek 30 seconds |
| Shift + Left / Right | Seek 5 minutes |
| Page Up / Page Down | Previous / Next video |
| Ctrl + Delete | Delete current file |
| Ctrl + Backspace | Seek to 50% |
| Escape | Exit comparison mode |
