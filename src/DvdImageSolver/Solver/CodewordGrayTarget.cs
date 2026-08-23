using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

internal readonly record struct CodewordBlockEvaluation(
    int BinaryErrors,
    int GrayError,
    ModulationBoundary FinalBoundary);

internal readonly record struct CodewordFrameEvaluation(
    int BinaryErrors,
    int GrayError,
    ModulationBoundary FinalBoundary);

internal sealed class CodewordGrayTarget
{
    private readonly sbyte[] _desiredLandCounts;
    private readonly sbyte[] _observedLandCounts;

    private CodewordGrayTarget(
        sbyte[] desiredLandCounts,
        int scoredWords,
        sbyte[] observedLandCounts,
        int observedWords)
    {
        _desiredLandCounts = desiredLandCounts;
        _observedLandCounts = observedLandCounts;
        ScoredWords = scoredWords;
        ObservedWords = observedWords;
    }

    internal int ScoredWords { get; }

    internal int MaximumGrayError => checked(ScoredWords * 16);

    internal int ObservedWords { get; }

    internal int MaximumObservedGrayError => checked(ObservedWords * 16);

    internal static CodewordGrayTarget Create(ImageConstraint constraint, int bridgeBytes = 0)
    {
        if (bridgeBytes is < 0 or > EfmPlusEncoder.BytesPerSyncFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(bridgeBytes));
        }

        sbyte[] desired = new sbyte[208 * 182];
        Array.Fill(desired, (sbyte)-1);
        int scoredWords = 0;
        bool binaryImageTargets = constraint.ImageMapping is not null;
        for (int payloadIndex = 0; payloadIndex < DvdEccBlockEncoder.PayloadBytesPerBlock; payloadIndex++)
        {
            int channelOffset = DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex);
            int samples = 0;
            int landSamples = 0;
            sbyte tieBreaker = -1;
            int tieBreakerDistance = int.MaxValue;
            for (int bit = 0; bit < 16; bit++)
            {
                sbyte target = constraint.TargetAt(channelOffset + bit);
                if (target < 0)
                {
                    continue;
                }

                samples++;
                landSamples += target;
                int centerDistance = Math.Abs(bit - 8);
                if (centerDistance < tieBreakerDistance)
                {
                    tieBreaker = target;
                    tieBreakerDistance = centerDistance;
                }
            }

            if (samples == 0)
            {
                continue;
            }

            int recordingIndex = DvdEccBlockEncoder.PayloadRecordingFrameIndex(payloadIndex);
            desired[recordingIndex] = binaryImageTargets
                ? checked((sbyte)(landSamples * 2 == samples
                    ? tieBreaker * 16
                    : landSamples * 2 > samples ? 16 : 0))
                : checked((sbyte)(((landSamples * 16) + (samples / 2)) / samples));
            scoredWords++;
        }

        if (bridgeBytes > 0)
        {
            List<int>[] payloadWordsBySyncFrame = Enumerable.Range(
                    0,
                    DvdEccBlockEncoder.SyncFrameCount)
                .Select(_ => new List<int>())
                .ToArray();
            for (int payloadIndex = 0; payloadIndex < DvdEccBlockEncoder.PayloadBytesPerBlock; payloadIndex++)
            {
                int recordingIndex = DvdEccBlockEncoder.PayloadRecordingFrameIndex(payloadIndex);
                payloadWordsBySyncFrame[recordingIndex / EfmPlusEncoder.BytesPerSyncFrame]
                    .Add(recordingIndex);
            }

            foreach (List<int> payloadWords in payloadWordsBySyncFrame)
            {
                int count = Math.Min(bridgeBytes, payloadWords.Count);
                for (int index = 0; index < count; index++)
                {
                    ClearTarget(payloadWords[index], desired, ref scoredWords);
                    ClearTarget(payloadWords[^(index + 1)], desired, ref scoredWords);
                }
            }
        }

        sbyte[] observed = constraint.ChannelWordTargets.ToArray();
        int observedWords = observed.Count(target => target >= 0);
        return new CodewordGrayTarget(desired, scoredWords, observed, observedWords);
    }

    private static void ClearTarget(int recordingIndex, sbyte[] desired, ref int scoredWords)
    {
        if (desired[recordingIndex] < 0)
        {
            return;
        }

        desired[recordingIndex] = -1;
        scoredWords--;
    }

    internal ReadOnlySpan<sbyte> FrameTargets(int syncFrameIndex)
        => _desiredLandCounts.AsSpan(
            syncFrameIndex * EfmPlusEncoder.BytesPerSyncFrame,
            EfmPlusEncoder.BytesPerSyncFrame);

    internal sbyte DesiredAtRecordingIndex(int recordingIndex) => _desiredLandCounts[recordingIndex];

    internal CodewordBlockEvaluation Evaluate(
        ReadOnlySpan<byte> recordingFrames,
        ModulationBoundary initialBoundary,
        ModulationBoundary[]? syncFrameInputs = null,
        CodewordFrameEvaluation[]? syncFrameEvaluations = null)
        => EvaluateCore(
            recordingFrames,
            initialBoundary,
            observeAllWords: false,
            syncFrameInputs,
            syncFrameEvaluations);

    internal CodewordBlockEvaluation EvaluateObserved(
        ReadOnlySpan<byte> recordingFrames,
        ModulationBoundary initialBoundary)
        => EvaluateCore(
            recordingFrames,
            initialBoundary,
            observeAllWords: true,
            syncFrameInputs: null,
            syncFrameEvaluations: null);

    private CodewordBlockEvaluation EvaluateCore(
        ReadOnlySpan<byte> recordingFrames,
        ModulationBoundary initialBoundary,
        bool observeAllWords,
        ModulationBoundary[]? syncFrameInputs,
        CodewordFrameEvaluation[]? syncFrameEvaluations)
    {
        if (recordingFrames.Length != _desiredLandCounts.Length)
        {
            throw new ArgumentException("Unexpected recording-frame byte count.", nameof(recordingFrames));
        }

        if (syncFrameInputs is not null && syncFrameInputs.Length != DvdEccBlockEncoder.SyncFrameCount)
        {
            throw new ArgumentException("Unexpected Sync Frame trace length.", nameof(syncFrameInputs));
        }

        if (syncFrameEvaluations is not null
            && syncFrameEvaluations.Length != DvdEccBlockEncoder.SyncFrameCount)
        {
            throw new ArgumentException(
                "Unexpected Sync Frame evaluation length.",
                nameof(syncFrameEvaluations));
        }

        EfmPlusWordScorer scorer = new();
        ModulationBoundary boundary = initialBoundary;
        int binaryErrors = 0;
        int grayError = 0;
        for (int syncFrame = 0; syncFrame < DvdEccBlockEncoder.SyncFrameCount; syncFrame++)
        {
            if (syncFrameInputs is not null)
            {
                syncFrameInputs[syncFrame] = boundary;
            }

            int recordingOffset = syncFrame * EfmPlusEncoder.BytesPerSyncFrame;
            int channelWordOffset = syncFrame * (EfmPlusEncoder.BitsPerSyncFrame / 16);
            ReadOnlySpan<sbyte> desired = observeAllWords
                ? _observedLandCounts.AsSpan(
                    channelWordOffset + 2,
                    EfmPlusEncoder.BytesPerSyncFrame)
                : FrameTargets(syncFrame);
            EfmPlusWordScore score = scorer.ScoreSyncFrame(
                recordingFrames.Slice(recordingOffset, EfmPlusEncoder.BytesPerSyncFrame),
                DvdEccBlockEncoder.SyncCategory(syncFrame),
                boundary,
                desired,
                observeAllWords ? _observedLandCounts[channelWordOffset] : (sbyte)-1,
                observeAllWords ? _observedLandCounts[channelWordOffset + 1] : (sbyte)-1);
            if (syncFrameEvaluations is not null)
            {
                syncFrameEvaluations[syncFrame] = new CodewordFrameEvaluation(
                    score.BinaryErrors,
                    score.GrayError,
                    score.Boundary);
            }

            binaryErrors += score.BinaryErrors;
            grayError += score.GrayError;
            boundary = score.Boundary;
        }

        return new CodewordBlockEvaluation(binaryErrors, grayError, boundary);
    }
}
