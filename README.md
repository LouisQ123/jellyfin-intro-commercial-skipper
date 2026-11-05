# Intro & Commercial Skipper for Jellyfin

A Jellyfin plugin that detects:

- **Recurring intros/outros** in TV shows and anime, by fingerprinting episode audio
  (Chromaprint) and correlating it against other episodes of the same season.
- **Commercial breaks** in recorded broadcast content, by importing a [Comskip](https://github.com/erihiga/Comskip)
  `.edl` file next to the media if one exists, or (optionally) running a built-in black-frame +
  silence heuristic scan when no `.edl` is available.

Detected segments are exposed through Jellyfin's native media segment system (`IMediaSegmentProvider`,
added in Jellyfin 10.9), so clients that support it show the normal built-in "Skip Intro" / "Skip
Outro" / "Skip Commercial" buttons - no custom web client script injection.

## How it fits into Jellyfin

This plugin does **not** run its own background scan. Jellyfin already has one: the built-in
**"Extract Media Segments"** scheduled task (Dashboard → Scheduled Tasks → Library) calls every
enabled media segment provider for every item in your library, on a timer (every 12 hours by
default) or on demand. This plugin registers itself as one of those providers.

For a library to use it:

1. Install and enable the plugin (see below).
2. In Dashboard → Libraries → (your library) → Manage Library → Advanced, make sure
   "Intro & Commercial Skipper" is enabled in the media segment provider list for that library.
3. Run "Extract Media Segments" from Dashboard → Scheduled Tasks (or just wait for it to run on
   its own schedule).

The first analysis of a file shells out to `ffmpeg` (using its built-in Chromaprint muxer - no
separate `fpcalc` binary needed), which costs real CPU time; results are cached (keyed by file size
+ modified time) so re-running the task doesn't redo the work unless the file actually changed. If
ffmpeg can't even be launched (wrong path), that failure is *not* cached, so fixing the path and
re-running the task retries properly instead of being stuck on a stale "nothing found" result.

## Detection approach and its limits

**Intro/outro detection** compares a new episode's start/end audio against up to
`MaxReferenceEpisodesPerSeason` other episodes in the same season, looking for the longest run of
near-identical Chromaprint fingerprint data. This works well for shows with a literal repeated
intro/credits sequence (most TV, almost all anime). It will not find anything in a season where
every episode's cold open is different, and a single mismatched episode (e.g. a clip/recap
special) just won't get a result - it doesn't produce a *wrong* one.

**Commercial detection** strongly prefers a Comskip `.edl` file (same path as the media file, with
a `.edl` extension) - Comskip's logo detection and frame-differencing is meaningfully better than
anything this plugin does on its own. The built-in heuristic fallback (off by default - see
Configuration) only looks for moments where the picture goes black *and* the audio goes silent at
the same time, and treats a gap of plausible commercial-pod length between two such moments as a
break. It is a last resort, not a replacement for Comskip, and will both miss real breaks and
occasionally flag a deliberate black-screen-with-silence moment in actual content.

## Requirements

- Jellyfin server 10.11.x by default (this plugin targets the `MediaBrowser.Controller`/
  `MediaBrowser.Model` ABI as of that version - see **Targeting a different Jellyfin version**
  below to build for 12.x or an older 10.x server instead).
- An ffmpeg build with Chromaprint support. By default this plugin uses whatever ffmpeg Jellyfin
  itself is already configured to use (Dashboard > Playback > Transcoding), so nothing needs
  configuring separately in the common case; jellyfin-ffmpeg (what Jellyfin's own Docker image
  bundles) always includes Chromaprint support. No separate `fpcalc` binary is needed. Only set an
  override in this plugin's own settings if you need it to use a *different* ffmpeg than Jellyfin does.

## Building

```bash
cd src/IntroCommercialSkipper
dotnet build -c Release
```

The compiled `IntroCommercialSkipper.dll` will be in `bin/Release/net9.0/` (or `net10.0/` when
building against Jellyfin 12.x - see below).

### Targeting a different Jellyfin version

The plugin ABI this relies on (`IMediaSegmentProvider`, `MediaSegmentDto`, `MediaSegmentType`) was
added in Jellyfin 10.9 and its exact shape has moved around since (for example `MediaSegmentType`
moved namespaces between 10.x and the 12.x line). Build against the `Jellyfin.Controller` /
`Jellyfin.Model` / `Jellyfin.Database.Implementations` NuGet package version matching your actual
server:

```bash
dotnet build -c Release -p:JellyfinVersion=12.4.0
```

The target framework is picked automatically from `JellyfinVersion` (`net10.0` for 12.x, `net9.0`
otherwise); override it explicitly with `-p:TargetFramework=net8.0` for an older 10.x release that
needs it. The code itself may need small adjustments if the ABI differs further - the project is
small enough that this is usually a quick fix.

## Installing

1. Build the plugin (above), or download `IntroCommercialSkipper.dll` from a release.
2. Copy it into a new folder under Jellyfin's plugin directory, e.g.
   `<jellyfin-data>/plugins/IntroCommercialSkipper/IntroCommercialSkipper.dll`.
3. Restart Jellyfin.
4. Configure it from Dashboard → Plugins → Intro & Commercial Skipper.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| Detect recurring intros/outros | on | Enables the fingerprint-based intro/outro detector |
| Detect commercial breaks | on | Enables Comskip `.edl` import |
| Use built-in heuristic when no `.edl` is found | **off** | Enables the black-frame/silence fallback - see limits above |
| Minimum intro/outro duration | 8s | Shorter matches are discarded as noise |
| Search window per episode | 300s | How much of the start/end of each episode is fingerprinted |
| Max fingerprint bit-error rate | 0.35 | Lower = stricter match required |
| Reference episodes compared per season | 3 | How many sibling episodes to try before giving up |
| Heuristic black/silence/threshold settings | - | Tuning for the commercial fallback's ffmpeg filters |
| ffmpeg path override | blank = use Jellyfin's own ffmpeg | Only needed to point at a *different* ffmpeg than Jellyfin itself uses |

## Project layout

```
src/IntroCommercialSkipper/
  Plugin.cs                          Plugin entry point + config page registration
  PluginServiceRegistrator.cs        Wires services into Jellyfin's DI container
  Configuration/                     Settings model + config page (HTML/JS)
  Data/                              Segment types + on-disk detection cache
  Detection/
    AudioFingerprint.cs              Shells out to ffmpeg's Chromaprint muxer for a fingerprint
    FingerprintCorrelator.cs         Pure sample-index matching between two fingerprints
    IntroOutroDetector.cs            Intro/outro-specific use of the correlator
    ComskipEdlParser.cs              Reads an adjacent Comskip .edl file
    BlackSilenceCommercialDetector.cs  Heuristic fallback commercial detector
    SegmentDetectionService.cs       Orchestrates the above for one media item
  MediaSegments/
    SkipperMediaSegmentProvider.cs   The IMediaSegmentProvider Jellyfin calls
```

## License

GPL-3.0-only (matching Jellyfin's own license, which this plugin links against).
