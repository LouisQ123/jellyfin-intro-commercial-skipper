using System.Globalization;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// Thrown when ffmpeg could not even be launched (wrong path, not executable, missing from the
/// container/host). This is an environment problem, not a per-file one: callers must not treat it
/// the way they treat "no match found" for a given file, or a single misconfiguration would get
/// cached as a confident negative result for every file it touched.
/// </summary>
public sealed class ExternalToolNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalToolNotFoundException"/> class.
    /// </summary>
    /// <param name="toolPath">The path or command name that failed to launch.</param>
    /// <param name="inner">The underlying exception from <see cref="System.Diagnostics.Process.Start()"/>.</param>
    public ExternalToolNotFoundException(string toolPath, Exception inner)
        : base($"Could not start '{toolPath}' - check it is installed and the configured path is correct.", inner)
    {
    }
}

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
/// Computes <see cref="AudioFingerprint"/>s for an arbitrary window of a media file using ffmpeg's
/// own Chromaprint muxer.
/// </summary>
/// <remarks>
/// ffmpeg builds that enable Chromaprint support (jellyfin-ffmpeg always does) link libchromaprint
/// directly, so a single ffmpeg invocation with <c>-f chromaprint -fp_format raw</c> decodes, trims,
/// resamples and fingerprints the requested window in one process - no separate <c>fpcalc</c> binary
/// needed (and jellyfin-ffmpeg's build of libchromaprint does not ship that CLI tool at all).
/// The muxer writes the fingerprint as a flat array of native-endian uint32 values, not text.
/// </remarks>
public class AudioFingerprinter
{
    private const int SampleRate = 11025;

    private readonly ILogger _logger;
    private readonly string _ffmpegPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioFingerprinter"/> class.
    /// </summary>
    /// <param name="ffmpegPath">Path to, or bare command name of, the ffmpeg executable.</param>
    /// <param name="logger">Logger.</param>
    public AudioFingerprinter(string ffmpegPath, ILogger logger)
    {
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    /// <summary>
    /// Fingerprints a window of audio from a media file.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="startOffset">Where in the file to start fingerprinting.</param>
    /// <param name="length">How much audio to fingerprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fingerprint, or null if ffmpeg produced no usable output for this file/window.</returns>
    /// <exception cref="ExternalToolNotFoundException">ffmpeg could not be launched at all.</exception>
    public async Task<AudioFingerprint?> FingerprintAsync(
        string mediaPath,
        TimeSpan startOffset,
        TimeSpan length,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startOffset.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(length.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("chromaprint");
        psi.ArgumentList.Add("-fp_format");
        psi.ArgumentList.Add("raw");
        psi.ArgumentList.Add("-");

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ExternalToolNotFoundException(_ffmpegPath, ex);
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            try
            {
                using var rawOutput = new MemoryStream();
                var copyTask = process.StandardOutput.BaseStream.CopyToAsync(rawOutput, cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await Task.WhenAll(copyTask, stderrTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "ffmpeg exited with code {Code} fingerprinting {Path} at offset {Offset}: {Stderr}",
                        process.ExitCode,
                        mediaPath,
                        startOffset,
                        await stderrTask.ConfigureAwait(false));
                    return null;
                }

                return ParseRawFingerprint(rawOutput.ToArray());
            }
            catch (IOException ex)
            {
                // A per-file I/O hiccup (e.g. an unreadable/corrupt file), not an environment
                // misconfiguration: safe to treat as "no fingerprint for this file" and let it be cached.
                _logger.LogError(ex, "Failed to fingerprint {Path} at offset {Offset}", mediaPath, startOffset);
                return null;
            }
        }
    }

    private AudioFingerprint? ParseRawFingerprint(byte[] rawBytes)
    {
        if (rawBytes.Length < 4)
        {
            _logger.LogWarning("ffmpeg produced no usable chromaprint output ({Bytes} bytes)", rawBytes.Length);
            return null;
        }

        var sampleCount = rawBytes.Length / 4;
        var samples = new List<uint>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            samples.Add(BitConverter.ToUInt32(rawBytes, i * 4));
        }

        return new AudioFingerprint(samples);
    }
}
