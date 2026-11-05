using Jellyfin.Plugin.IntroCommercialSkipper.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// A detected intro or outro shared between a reference episode and another episode of the same
/// season, expressed as absolute offsets into each file.
/// </summary>
/// <param name="ReferenceStart">Where the shared segment starts in the reference episode.</param>
/// <param name="ReferenceEnd">Where the shared segment ends in the reference episode.</param>
/// <param name="CandidateStart">Where the shared segment starts in the candidate episode.</param>
/// <param name="CandidateEnd">Where the shared segment ends in the candidate episode.</param>
/// <param name="BitErrorRate">Fingerprint match quality; lower is a more confident match.</param>
public sealed record IntroOutroMatch(
    TimeSpan ReferenceStart,
    TimeSpan ReferenceEnd,
    TimeSpan CandidateStart,
    TimeSpan CandidateEnd,
    double BitErrorRate);

/// <summary>
/// Finds a recurring intro or outro shared between two episodes by fingerprinting the start (or
/// end) of each and correlating the results.
/// </summary>
public class IntroOutroDetector
{
    private readonly AudioFingerprinter _fingerprinter;
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroOutroDetector"/> class.
    /// </summary>
    /// <param name="fingerprinter">Fingerprinter used to analyze each episode's audio.</param>
    /// <param name="config">Plugin configuration (thresholds, search window).</param>
    /// <param name="logger">Logger.</param>
    public IntroOutroDetector(AudioFingerprinter fingerprinter, PluginConfiguration config, ILogger logger)
    {
        _fingerprinter = fingerprinter;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Looks for a shared intro near the start of both episodes.
    /// </summary>
    /// <param name="referencePath">Path to the reference episode's media file.</param>
    /// <param name="candidatePath">Path to the candidate episode's media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The match, or null if no confident shared intro was found.</returns>
    public Task<IntroOutroMatch?> FindIntroAsync(string referencePath, string candidatePath, CancellationToken cancellationToken) =>
        FindSharedSegmentAsync(referencePath, candidatePath, fromEnd: false, cancellationToken);

    /// <summary>
    /// Looks for a shared outro/credits sequence near the end of both episodes.
    /// </summary>
    /// <param name="referencePath">Path to the reference episode's media file.</param>
    /// <param name="candidatePath">Path to the candidate episode's media file.</param>
    /// <param name="referenceDuration">Total runtime of the reference episode.</param>
    /// <param name="candidateDuration">Total runtime of the candidate episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The match, or null if no confident shared outro was found.</returns>
    public Task<IntroOutroMatch?> FindOutroAsync(
        string referencePath,
        string candidatePath,
        TimeSpan referenceDuration,
        TimeSpan candidateDuration,
        CancellationToken cancellationToken) =>
        FindSharedSegmentAsync(
            referencePath,
            candidatePath,
            fromEnd: true,
            cancellationToken,
            referenceDuration,
            candidateDuration);

    private async Task<IntroOutroMatch?> FindSharedSegmentAsync(
        string referencePath,
        string candidatePath,
        bool fromEnd,
        CancellationToken cancellationToken,
        TimeSpan referenceDuration = default,
        TimeSpan candidateDuration = default)
    {
        var windowLength = TimeSpan.FromSeconds(_config.IntroOutroSearchWindowSeconds);

        var referenceWindowStart = fromEnd
            ? ClampWindowStart(referenceDuration, windowLength)
            : TimeSpan.Zero;
        var candidateWindowStart = fromEnd
            ? ClampWindowStart(candidateDuration, windowLength)
            : TimeSpan.Zero;

        var referenceFingerprint = await _fingerprinter
            .FingerprintAsync(referencePath, referenceWindowStart, windowLength, cancellationToken)
            .ConfigureAwait(false);
        var candidateFingerprint = await _fingerprinter
            .FingerprintAsync(candidatePath, candidateWindowStart, windowLength, cancellationToken)
            .ConfigureAwait(false);

        if (referenceFingerprint is null || candidateFingerprint is null)
        {
            _logger.LogDebug(
                "Skipping {Kind} match for {Candidate}: fingerprinting failed",
                fromEnd ? "outro" : "intro",
                candidatePath);
            return null;
        }

        var minRunLengthSamples = (int)(_config.MinIntroOutroDurationSeconds / AudioFingerprint.SecondsPerSample);
        var match = FingerprintCorrelator.FindLongestMatch(
            referenceFingerprint,
            candidateFingerprint,
            _config.FingerprintMaxBitErrorRate,
            minRunLengthSamples);

        if (match is null)
        {
            return null;
        }

        var referenceStart = referenceWindowStart + TimeSpan.FromSeconds(match.StartA * AudioFingerprint.SecondsPerSample);
        var referenceEnd = referenceWindowStart + TimeSpan.FromSeconds(match.EndA * AudioFingerprint.SecondsPerSample);
        var candidateStart = candidateWindowStart + TimeSpan.FromSeconds(match.StartB * AudioFingerprint.SecondsPerSample);
        var candidateEnd = candidateWindowStart + TimeSpan.FromSeconds(match.EndB * AudioFingerprint.SecondsPerSample);

        return new IntroOutroMatch(referenceStart, referenceEnd, candidateStart, candidateEnd, match.BitErrorRate);
    }

    private static TimeSpan ClampWindowStart(TimeSpan duration, TimeSpan windowLength) =>
        duration > windowLength ? duration - windowLength : TimeSpan.Zero;
}
