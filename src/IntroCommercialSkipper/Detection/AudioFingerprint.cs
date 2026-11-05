using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// A Chromaprint raw fingerprint: one 32-bit integer per ~1/8 second of audio, each bit
/// representing a feature of that audio frame. Two frames of the same underlying audio produce
/// near-identical integers; unrelated audio produces integers that differ in roughly half their
/// bits.
/// </summary>
/// <param name="Samples">One fingerprint value per frame, in chronological order.</param>
public sealed record AudioFingerprint(IReadOnlyList<uint> Samples)
{
    /// <summary>
    /// The duration, in seconds, each fingerprint sample represents. Fixed by Chromaprint's
    /// algorithm (not configurable), used to convert sample offsets back to timestamps.
    /// </summary>
    public const double SecondsPerSample = 1d / 8d;
}

/// <summary>
/// Computes <see cref="AudioFingerprint"/>s for an arbitrary window of a media file.
/// </summary>
/// <remarks>
/// fpcalc has no seek flag, so for every window (including one starting at 0) ffmpeg decodes,
/// trims and resamples the requested window to raw mono 16-bit PCM and pipes it directly into
/// fpcalc's stdin; this is also what keeps the two tools consistent with each other regardless of
/// the source container/codec.
/// </remarks>
public class AudioFingerprinter
{
    private readonly ILogger _logger;
    private readonly string _ffmpegPath;
    private readonly string _fpcalcPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioFingerprinter"/> class.
    /// </summary>
    /// <param name="ffmpegPath">Path to, or bare command name of, the ffmpeg executable.</param>
    /// <param name="fpcalcPath">Path to, or bare command name of, the fpcalc executable.</param>
    /// <param name="logger">Logger.</param>
    public AudioFingerprinter(string ffmpegPath, string fpcalcPath, ILogger logger)
    {
        _ffmpegPath = ffmpegPath;
        _fpcalcPath = fpcalcPath;
        _logger = logger;
    }

    /// <summary>
    /// Fingerprints a window of audio from a media file.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="startOffset">Where in the file to start fingerprinting.</param>
    /// <param name="length">How much audio to fingerprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fingerprint, or null if either tool failed or produced no usable output.</returns>
    public async Task<AudioFingerprint?> FingerprintAsync(
        string mediaPath,
        TimeSpan startOffset,
        TimeSpan length,
        CancellationToken cancellationToken)
    {
        const int SampleRate = 11025;

        var ffmpegPsi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ffmpegPsi.ArgumentList.Add("-ss");
        ffmpegPsi.ArgumentList.Add(startOffset.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        ffmpegPsi.ArgumentList.Add("-t");
        ffmpegPsi.ArgumentList.Add(length.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        ffmpegPsi.ArgumentList.Add("-i");
        ffmpegPsi.ArgumentList.Add(mediaPath);
        ffmpegPsi.ArgumentList.Add("-vn");
        ffmpegPsi.ArgumentList.Add("-ac");
        ffmpegPsi.ArgumentList.Add("1");
        ffmpegPsi.ArgumentList.Add("-ar");
        ffmpegPsi.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        ffmpegPsi.ArgumentList.Add("-f");
        ffmpegPsi.ArgumentList.Add("s16le");
        ffmpegPsi.ArgumentList.Add("-");

        var fpcalcPsi = new ProcessStartInfo(_fpcalcPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        fpcalcPsi.ArgumentList.Add("-raw");
        fpcalcPsi.ArgumentList.Add("-length");
        fpcalcPsi.ArgumentList.Add(((int)length.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        fpcalcPsi.ArgumentList.Add("-format");
        fpcalcPsi.ArgumentList.Add("s16le");
        fpcalcPsi.ArgumentList.Add("-rate");
        fpcalcPsi.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        fpcalcPsi.ArgumentList.Add("-channels");
        fpcalcPsi.ArgumentList.Add("1");
        fpcalcPsi.ArgumentList.Add("-");

        try
        {
            using var ffmpeg = Process.Start(ffmpegPsi);
            using var fpcalc = Process.Start(fpcalcPsi);
            if (ffmpeg is null || fpcalc is null)
            {
                return null;
            }

            var pumpTask = PumpAsync(ffmpeg, fpcalc, cancellationToken);
            var stdoutTask = fpcalc.StandardOutput.ReadToEndAsync(cancellationToken);
            var ffmpegStderrTask = ffmpeg.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(
                pumpTask,
                stdoutTask,
                ffmpegStderrTask,
                ffmpeg.WaitForExitAsync(cancellationToken),
                fpcalc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

            if (fpcalc.ExitCode != 0)
            {
                _logger.LogWarning(
                    "fpcalc exited with code {Code} for {Path} at offset {Offset}",
                    fpcalc.ExitCode,
                    mediaPath,
                    startOffset);
                return null;
            }

            return ParseRawFingerprint(await stdoutTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Failed to fingerprint {Path} at offset {Offset}", mediaPath, startOffset);
            return null;
        }
    }

    private static async Task PumpAsync(Process source, Process destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.StandardOutput.BaseStream
                .CopyToAsync(destination.StandardInput.BaseStream, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The downstream process (fpcalc) can close its stdin early once it has read enough
            // samples; that shows up here as a broken pipe, which is expected and not an error.
        }
        finally
        {
            destination.StandardInput.Close();
        }
    }

    private AudioFingerprint? ParseRawFingerprint(string fpcalcOutput)
    {
        // fpcalc -raw output looks like:
        //   DURATION=1234
        //   FINGERPRINT=123,456,-789,...
        var line = fpcalcOutput
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("FINGERPRINT=", StringComparison.Ordinal));

        if (line is null)
        {
            _logger.LogWarning("fpcalc produced no FINGERPRINT line");
            return null;
        }

        var values = line["FINGERPRINT=".Length..].Split(',');
        var samples = new List<uint>(values.Length);
        foreach (var value in values)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
            {
                samples.Add(unchecked((uint)signed));
            }
        }

        return samples.Count == 0 ? null : new AudioFingerprint(samples);
    }
}
