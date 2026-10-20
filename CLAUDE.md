# CLAUDE.md — Reaction Studio

A Windows desktop app (WPF, .NET 10) for making reaction Shorts. Paste a video URL: the app downloads it,
writes a reaction script, records you on webcam and mic beside the clip (or voices an AI character
instead), then edits, captions and uploads the finished Short to YouTube on a schedule.

## Commands

```powershell
dotnet build App/ReactionStudio.csproj
dotnet run --project App/ReactionStudio.csproj
"Start Reaction Studio.bat"        # runs the built exe, builds it first if missing
```

There is no test project. Verify changes by building and running the affected flow end to end.

## Layout

```
App/
  MainWindow        clip list; download (several at once), per-clip script generation, React / Commentary
  RecordWindow      webcam + mic capture beside the looping clip, with a teleprompter window
  PostWindow        post-processing: profanity blur, frames, voiceover, caption + title, word captions,
                    thumbnail, final compose, upload
  SettingsWindow    devices, schedule, ElevenLabs, character image, transformativeness controls
  Models/AppConfig  settings, saved to %APPDATA%\ReactionStudio\config.json
  Services/
    AppPaths        where clips, output, thumbnails and OAuth data live (one base folder)
    YtDlpService    downloads; FfmpegService composes, blurs, extracts frames and audio
    CodexService    `codex exec` with clip frames: caption + title, teleprompter script, profane-text boxes,
                    commentary script
    ClaudeService   `claude` CLI captions
    WhisperService  faster-whisper (Python subprocess) word timestamps; SubtitleService burns them
    ElevenLabsService / LocalTtsService   voice for commentary mode (Windows SAPI fallback)
    MotionBlurService  radial zoom blur via OpenCvSharp, driven by per-frame zoom speed
    SfxService      whoosh/impact sounds from a user folder, with a brand denylist
    StyleService    learns title and opener style from a channel's most popular Shorts (metadata only)
    YouTubeService, ScheduleService   OAuth upload and the two-per-day peak-time schedule
```

## Conventions

- External tools (`yt-dlp`, `ffmpeg`, `codex`, `claude`, Python) run as subprocesses with argument lists.
  Resolve full executable paths: GUI processes do not always inherit PATH.
- Long work runs off the UI thread and reports progress; several downloads and post jobs can run at once,
  so each job gets its own scratch folder and unique temp names.
- Every AI step has a fallback (cleaned file name for titles, Windows voice for TTS), so a failed CLI call
  degrades the result instead of stopping the post.
- Commentary mode is built to be transformative: original audio ducked or muted, continuous voiceover,
  reframes and cutaways. Keep those controls working when changing the compose step.
- Comments use the existing `// ── Section ──` headers and say why something is done.

## Gotchas

- `AppPaths.Base` is a hard-coded absolute path; change it before running on another machine.
- FFmpeg compose has a hard `-t` cap and a bounded `apad` on purpose (runaway encodes); do not switch back
  to `-shortest`.
- Only use SFX from the user's own folder; the denylist keeps recognisable, Content-ID-matched sounds out.

## Never

- Commit `client_secret.json`, the cached OAuth token folder, `config.json` (it can hold API keys), or
  downloaded clips, character art, SFX libraries and renders (`.gitignore` covers them; keep it that way).
