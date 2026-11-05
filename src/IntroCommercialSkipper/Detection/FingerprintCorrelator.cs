using System.Numerics;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// The longest run of matching audio found between two fingerprints, expressed as sample ranges
/// local to each fingerprint's own window (callers add back whatever offset that window started
/// at within the file to get absolute timestamps).
/// </summary>
/// <param name="StartA">Start sample index within fingerprint A.</param>
/// <param name="EndA">End sample index (exclusive) within fingerprint A.</param>
/// <param name="StartB">Start sample index within fingerprint B.</param>
/// <param name="EndB">End sample index (exclusive) within fingerprint B.</param>
/// <param name="BitErrorRate">Average fraction of mismatched bits over the matched run (0 = identical, 0.5 = unrelated).</param>
public sealed record FingerprintMatch(int StartA, int EndA, int StartB, int EndB, double BitErrorRate);

/// <summary>
/// Finds the longest run of near-identical audio shared between two Chromaprint fingerprints.
/// Pure sample-index math with no I/O, kept separate from <see cref="AudioFingerprinter"/> so the
/// matching logic can be reasoned about (and unit tested) independently of shelling out to ffmpeg/fpcalc.
/// </summary>
public static class FingerprintCorrelator
{
    private const int BitsPerSample = 32;

    /// <summary>
    /// Finds the best-aligned, longest shared run of audio between two fingerprints.
    /// </summary>
    /// <param name="a">First fingerprint.</param>
    /// <param name="b">Second fingerprint.</param>
    /// <param name="maxBitErrorRate">
    /// Maximum average fraction of mismatched bits (0-1) for a run to be considered a real match.
    /// </param>
    /// <param name="minRunLengthSamples">Minimum length, in samples, for a run to be reported.</param>
    /// <returns>The best matching run, or null if nothing cleared the thresholds.</returns>
    public static FingerprintMatch? FindLongestMatch(
        AudioFingerprint a,
        AudioFingerprint b,
        double maxBitErrorRate,
        int minRunLengthSamples)
    {
        var bestOffset = FindBestAlignment(a.Samples, b.Samples);
        if (bestOffset is null)
        {
            return null;
        }

        var offset = bestOffset.Value;

        // At this offset, sample i of A lines up with sample (i - offset) of B.
        var overlapStartA = Math.Max(0, offset);
        var overlapEndA = Math.Min(a.Samples.Count, b.Samples.Count + offset);
        if (overlapEndA <= overlapStartA)
        {
            return null;
        }

        var perSampleError = new double[overlapEndA - overlapStartA];
        for (var i = overlapStartA; i < overlapEndA; i++)
        {
            var bIndex = i - offset;
            var distance = BitOperations.PopCount(a.Samples[i] ^ b.Samples[bIndex]);
            perSampleError[i - overlapStartA] = (double)distance / BitsPerSample;
        }

        var run = FindLongestLowErrorRun(perSampleError, maxBitErrorRate);
        if (run is null || run.Value.Length < minRunLengthSamples)
        {
            return null;
        }

        var (runStart, length, avgError) = run.Value;
        var startA = overlapStartA + runStart;
        var endA = startA + length;
        var startB = startA - offset;
        var endB = endA - offset;

        return new FingerprintMatch(startA, endA, startB, endB, avgError);
    }

    /// <summary>
    /// Slides B against A across every possible offset and returns the offset with the lowest
    /// average Hamming distance over the overlapping region, provided the overlap is large enough
    /// to be meaningful. O(n*m) in the sample counts, which is fine here: callers only fingerprint
    /// a few minutes of audio (a few thousand samples) at a time, not whole files.
    /// </summary>
    private static int? FindBestAlignment(IReadOnlyList<uint> a, IReadOnlyList<uint> b)
    {
        const int MinOverlapSamples = 16; // 2 seconds at 8 samples/sec; shorter overlaps are too noisy to trust.

        int? bestOffset = null;
        var bestAvgError = double.MaxValue;

        for (var offset = -(b.Count - 1); offset <= a.Count - 1; offset++)
        {
            var overlapStart = Math.Max(0, offset);
            var overlapEnd = Math.Min(a.Count, b.Count + offset);
            var overlapLength = overlapEnd - overlapStart;
            if (overlapLength < MinOverlapSamples)
            {
                continue;
            }

            long totalDistance = 0;
            for (var i = overlapStart; i < overlapEnd; i++)
            {
                totalDistance += BitOperations.PopCount(a[i] ^ b[i - offset]);
            }

            var avgError = (double)totalDistance / overlapLength / BitsPerSample;
            if (avgError < bestAvgError)
            {
                bestAvgError = avgError;
                bestOffset = offset;
            }
        }

        return bestOffset;
    }

    /// <summary>
    /// Within a per-sample error series, finds the longest contiguous run whose average error is
    /// at or below <paramref name="maxBitErrorRate"/>, tolerating brief noisy spikes (gaps of up to
    /// <see cref="GapToleranceSamples"/> samples above a looser per-sample threshold) so a handful
    /// of corrupted frames inside an otherwise-matching intro don't split it into fragments.
    /// </summary>
    private static (int Start, int Length, double AverageError)? FindLongestLowErrorRun(
        double[] perSampleError,
        double maxBitErrorRate)
    {
        // Deliberately classify per-sample against maxBitErrorRate itself, not a looser multiple of
        // it: unrelated audio has a Hamming distance centered on half the bits (around 0.5 error),
        // so any threshold meaningfully above maxBitErrorRate starts admitting unrelated samples
        // often enough that GapToleranceSamples below can bridge long fake runs out of pure noise.
        const int GapToleranceSamples = 2;

        var isLow = new bool[perSampleError.Length];
        for (var i = 0; i < perSampleError.Length; i++)
        {
            isLow[i] = perSampleError[i] <= maxBitErrorRate;
        }

        (int Start, int Length)? best = null;
        var i2 = 0;
        while (i2 < isLow.Length)
        {
            if (!isLow[i2])
            {
                i2++;
                continue;
            }

            var start = i2;
            var end = i2;
            while (end < isLow.Length)
            {
                if (isLow[end])
                {
                    end++;
                    continue;
                }

                var gapEnd = end;
                while (gapEnd < isLow.Length && !isLow[gapEnd] && gapEnd - end < GapToleranceSamples)
                {
                    gapEnd++;
                }

                if (gapEnd < isLow.Length && isLow[gapEnd])
                {
                    end = gapEnd + 1;
                    continue;
                }

                break;
            }

            var length = end - start;
            if (best is null || length > best.Value.Length)
            {
                best = (start, length);
            }

            i2 = Math.Max(end, i2 + 1);
        }

        if (best is null)
        {
            return null;
        }

        var sum = 0d;
        for (var i = best.Value.Start; i < best.Value.Start + best.Value.Length; i++)
        {
            sum += perSampleError[i];
        }

        var average = sum / best.Value.Length;
        return average <= maxBitErrorRate ? (best.Value.Start, best.Value.Length, average) : null;
    }
}
