# jellyfin-arib-audio-wrapper

A drop-in **ffmpeg wrapper for Jellyfin (Windows)** that fixes broken playback of
Japanese **ARIB / BS4K (`.ts` / `.m2ts`)** recordings whose audio mode switches
**2.0 ⇄ 5.1 mid-stream** (e.g. NHK 紅白歌合戦 and other music programs).

## The problem

Japanese broadcasts change the AAC audio configuration (channel layout) *within a
single elementary stream* during a program. When Jellyfin remuxes such a file it
copies the audio:

```
-codec:a:0 copy -bsf:a aac_adtstoasc
```

`aac_adtstoasc` freezes the `AudioSpecificConfig` (channel count) at the **first**
frame. The moment the broadcast switches to a different mode, the copied stream's
declared config no longer matches the actual frames, and the player (Jellyfin Web,
Fire TV, etc.) decodes with the wrong configuration → garbled / dropped / stalled
audio. This happens during **Direct Play / Remux**, so no client-side setting fixes
it.

## The fix

This wrapper sits in front of Jellyfin's bundled ffmpeg. For **`.ts` / `.m2ts`**
sources where Jellyfin would *copy* audio, it instead **re-encodes the audio to a
single constant layout** (video stays `copy` — fast, lossless), eliminating the
mid-stream change.

Which layout? It probes the audio mode at the **8-second mark** of the recording
(these recordings are made with ~8 s of pre-roll) and locks the whole file to it:

| Mode at 8 s | Output |
|-------------|--------|
| 5.1 (6ch)   | `-ac 6 -b:a 384k` (surround preserved) |
| stereo / other | `-ac 2 -b:a 256k` |
| mono        | `-ac 1 -b:a 128k` |

Surround content stays surround; stereo content stays stereo — decided per file.

Everything else (version checks, trickplay images, non-`.ts` transcodes, audio that
Jellyfin already re-encodes) is **passed through unchanged** to the real ffmpeg.

The child ffmpeg runs inside a Windows **Job Object** (`KILL_ON_JOB_CLOSE`) so it is
terminated together with the wrapper when Jellyfin stops/seeks — no orphaned
processes.

`ffprobe.exe` is a thin pass-through (Jellyfin derives the ffprobe path from the
ffmpeg folder, so it must live alongside).

## Requirements

- Jellyfin for Windows (tested on 10.11.x) with the bundled
  `C:\Program Files\Jellyfin\Server\ffmpeg.exe` / `ffprobe.exe`.
- .NET Framework C# compiler (`csc.exe`, ships with Windows) — no SDK needed.

If your bundled ffmpeg/ffprobe live elsewhere, edit the `REAL_FFMPEG` /
`REAL_FFPROBE` constants at the top of `src/wrapper.cs` and `src/ffprobe-wrap.cs`.

## Build

```powershell
.\build.ps1                       # outputs .\out\ffmpeg.exe and .\out\ffprobe.exe
```

## Install

1. Copy `out\ffmpeg.exe` and `out\ffprobe.exe` to a folder Jellyfin can read,
   e.g. `C:\ProgramData\Jellyfin\ffmpeg-wrap\`.
2. Point Jellyfin at it. On Windows the dashboard "FFmpeg path" field is usually
   read-only, so set it in
   `C:\ProgramData\Jellyfin\Server\config\encoding.xml` **while Jellyfin is
   stopped**, adding this line right after `<HardwareAccelerationType>`:

   ```xml
   <EncoderAppPath>C:\ProgramData\Jellyfin\ffmpeg-wrap\ffmpeg.exe</EncoderAppPath>
   ```

3. Start Jellyfin. Confirm in the log (`log_*.log`):

   ```
   FFmpeg: "C:\ProgramData\Jellyfin\ffmpeg-wrap\ffmpeg.exe"
   ```

The original bundled ffmpeg is never modified — Jellyfin updates keep working, and
to revert you just remove the `<EncoderAppPath>` line.

## Logs

Per-file decisions are appended to
`C:\ProgramData\Jellyfin\Server\log\ffmpeg-wrap.log`:

```
2026-06-09 07:38:58 input=...ts ch@8s=6 -> -ac 6 -b:a 384k
```

## Notes / tuning

- The 8 s probe assumes ~8 s of recording pre-roll. If a recording's margin differs
  and 8 s lands on the wrong mode, change the `8.0` threshold in
  `ProbeChannelsAt8s` (and the `%+#430` frame count if you move it much later).
- Bitrates and the channel mapping are easy to adjust in `Transform`.
