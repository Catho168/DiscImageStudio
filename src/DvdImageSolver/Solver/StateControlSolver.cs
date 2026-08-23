using System.Diagnostics;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed class StateControlSolver
{
    private static readonly int[] PayloadByRecordingIndex = BuildPayloadMap();
    private sealed record FrameChoice(
        int[] PayloadIndexes,
        byte[] PayloadValues,
        ModulationBoundary FinalBoundary);
    private sealed record FrameCandidate(byte[] Bytes, EfmPlusWordScore Score);
    private sealed record BatchCandidateResult(
        byte[] Payload,
        byte[] Frames,
        ModulationBoundary[] FrameInputs,
        CodewordFrameEvaluation[] FrameEvaluations,
        CodewordBlockEvaluation Evaluation);
    private sealed record LaneResult(SearchState State, int CompletedPasses);
    private sealed class AcceptancePolicy(int seed)
    {
        internal readonly Random Random = new(seed);
        internal double Temperature;
    }
    private sealed class SearchState(
        byte[] payload,
        byte[] frames,
        ModulationBoundary[] frameInputs,
        CodewordFrameEvaluation[] frameEvaluations,
        CodewordBlockEvaluation evaluation)
    {
        internal byte[] Payload = payload;
        internal byte[] Frames = frames;
        internal ModulationBoundary[] FrameInputs = frameInputs;
        internal CodewordFrameEvaluation[] FrameEvaluations = frameEvaluations;
        internal CodewordBlockEvaluation Evaluation = evaluation;
    }
    private readonly record struct BeamNode(EfmPlusPrefixState Prefix);
    private readonly record struct BeamExpansion(
        EfmPlusPrefixState Prefix,
        int ParentIndex,
        byte Value);
    private readonly record struct FrameRank(
        int FrameIndex,
        int BinaryErrors,
        int GrayError,
        int TargetWords);

    public SolverResult Solve(
        ImageConstraint constraint,
        ReadOnlySpan<byte> initialPayloads,
        uint initialLba,
        uint psnOffset,
        ModulationBoundary initialBoundary,
        SolverOptions options,
        Action<int, int, int>? progress = null)
    {
        Validate(initialPayloads, options);
        uint firstPsn = checked(initialLba + psnOffset);
        if ((firstPsn & 0xF) != 0)
        {
            throw new ArgumentException("The first PSN must be aligned to a 16-sector ECC Block.");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        CodewordGrayTarget target = CodewordGrayTarget.Create(constraint, options.BridgeBytes);
        byte[] initialPayloadCopy = initialPayloads.ToArray();
        SearchState initialState = CreateInitialState(
            initialPayloadCopy,
            firstPsn,
            initialBoundary,
            target);
        LaneResult[] lanes = new LaneResult[options.SearchLanes];
        Parallel.For(0, options.SearchLanes, laneIndex =>
        {
            int laneSeed = unchecked(options.RandomSeed + (laneIndex * 0x4F1BBCDC));
            SearchState laneState;
            if (laneIndex == 0)
            {
                laneState = CloneState(initialState);
            }
            else
            {
                byte[] restartPayload = (byte[])initialPayloadCopy.Clone();
                new Random(laneSeed).NextBytes(restartPayload);
                laneState = CreateInitialState(
                    restartPayload,
                    firstPsn,
                    initialBoundary,
                    target);
            }

            lanes[laneIndex] = RunLane(
                laneState,
                firstPsn,
                initialBoundary,
                target,
                options,
                groupedWarmup: false,
                laneIndex == 0 ? null : new AcceptancePolicy(laneSeed),
                laneIndex == 0 ? progress : null);
        });

        LaneResult bestLane = lanes[0];
        for (int laneIndex = 1; laneIndex < lanes.Length; laneIndex++)
        {
            if (IsBetter(lanes[laneIndex].State.Evaluation, bestLane.State.Evaluation))
            {
                bestLane = lanes[laneIndex];
            }
        }

        SearchState state = bestLane.State;
        byte[] bestPayload = state.Payload;
        CodewordBlockEvaluation best = state.Evaluation;
        int completed = bestLane.CompletedPasses;
        stopwatch.Stop();
        byte[] bestRecordingFrames = DvdEccBlockEncoder.BuildRecordingFrames(bestPayload, firstPsn);
        CodewordBlockEvaluation observed = target.EvaluateObserved(
            bestRecordingFrames,
            initialBoundary);
        EncodedEccBlock summary = new([], observed.FinalBoundary, firstPsn);
        return new SolverResult(
            bestPayload,
            summary,
            observed.BinaryErrors,
            target.ObservedWords,
            "all-channel-word-majority-error",
            completed,
            stopwatch.Elapsed)
        {
            GrayScore = observed.GrayError,
            GrayScoreMaximum = target.MaximumObservedGrayError,
            GrayScoreMetric = "all-channel-word-gray-error",
            ControlledScore = best.BinaryErrors,
            ControlledScoreMaximum = target.ScoredWords,
            ControlledScoreMetric = "controlled-payload-majority-error",
            ControlledGrayScore = best.GrayError,
            ControlledGrayScoreMaximum = target.MaximumGrayError,
            ControlledGrayScoreMetric = "controlled-payload-gray-error",
        };
    }

    private static SearchState CreateInitialState(
        ReadOnlySpan<byte> initialPayloads,
        uint firstPsn,
        ModulationBoundary initialBoundary,
        CodewordGrayTarget target)
    {
        byte[] payload = initialPayloads.ToArray();
        byte[] frames = DvdEccBlockEncoder.BuildRecordingFrames(payload, firstPsn);
        ModulationBoundary[] frameInputs = new ModulationBoundary[DvdEccBlockEncoder.SyncFrameCount];
        CodewordFrameEvaluation[] frameEvaluations =
            new CodewordFrameEvaluation[DvdEccBlockEncoder.SyncFrameCount];
        CodewordBlockEvaluation evaluation = target.Evaluate(
            frames,
            initialBoundary,
            frameInputs,
            frameEvaluations);
        return new SearchState(payload, frames, frameInputs, frameEvaluations, evaluation);
    }

    private static SearchState CloneState(SearchState state)
        => new(
            (byte[])state.Payload.Clone(),
            (byte[])state.Frames.Clone(),
            (ModulationBoundary[])state.FrameInputs.Clone(),
            (CodewordFrameEvaluation[])state.FrameEvaluations.Clone(),
            state.Evaluation);

    private static LaneResult RunLane(
        SearchState state,
        uint firstPsn,
        ModulationBoundary initialBoundary,
        CodewordGrayTarget target,
        SolverOptions options,
        bool groupedWarmup,
        AcceptancePolicy? acceptancePolicy,
        Action<int, int, int>? progress)
    {
        SearchState bestState = CloneState(state);
        int completed = 0;
        for (int pass = 0;
            pass < options.Iterations
                && (state.Evaluation.BinaryErrors != 0 || state.Evaluation.GrayError != 0);
            pass++)
        {
            if (acceptancePolicy is not null)
            {
                double fraction = options.Iterations <= 1
                    ? 1
                    : (double)pass / (options.Iterations - 1);
                acceptancePolicy.Temperature = Math.Max(
                    double.Epsilon,
                    (options.InitialTemperature
                        + ((options.FinalTemperature - options.InitialTemperature) * fraction))
                        / 64.0);
            }

            List<FrameRank> ranked = RankFrames(state.FrameEvaluations, target);
            if (ranked.Count == 0)
            {
                break;
            }

            int selectedCount = options.RetryFrameFraction > 0
                ? Math.Clamp(
                    (int)Math.Ceiling(ranked.Count * options.RetryFrameFraction),
                    1,
                    ranked.Count)
                : ranked.Count;
            int[] selectedFrames = ranked
                .Take(selectedCount)
                .Select(rank => rank.FrameIndex)
                .ToArray();
            bool improvedPass = TryBatchCandidates(
                state,
                selectedFrames,
                firstPsn,
                initialBoundary,
                target,
                options,
                options.BeamWidth,
                groupedAlternates: groupedWarmup,
                acceptancePolicy: acceptancePolicy);
            CaptureBest(state, ref bestState);
            for (int offset = 0; offset < selectedCount; offset += options.BatchFrames)
            {
                int count = Math.Min(options.BatchFrames, selectedCount - offset);
                int[] batch = ranked
                    .Skip(offset)
                    .Take(count)
                    .Select(rank => rank.FrameIndex)
                    .ToArray();
                bool improvedBatch = TryBatch(
                    state,
                    batch,
                    firstPsn,
                    initialBoundary,
                    target,
                    options,
                    options.BeamWidth,
                    rankedSingles: !groupedWarmup,
                    acceptancePolicy);
                improvedPass |= improvedBatch;
                CaptureBest(state, ref bestState);
            }

            completed = pass + 1;
            progress?.Invoke(
                completed,
                state.Evaluation.BinaryErrors,
                bestState.Evaluation.BinaryErrors);
            if (!improvedPass)
            {
                break;
            }
        }

        return new LaneResult(bestState, completed);
    }

    private static void CaptureBest(SearchState state, ref SearchState bestState)
    {
        if (IsBetter(state.Evaluation, bestState.Evaluation))
        {
            bestState = CloneState(state);
        }
    }

    private static bool TryBatch(
        SearchState state,
        IReadOnlyList<int> frameIndexes,
        uint firstPsn,
        ModulationBoundary initialBoundary,
        CodewordGrayTarget target,
        SolverOptions options,
        int beamWidth,
        bool rankedSingles,
        AcceptancePolicy? acceptancePolicy)
    {
        if (TryBatchCandidates(
                state,
                frameIndexes,
                firstPsn,
                initialBoundary,
                target,
                options,
                beamWidth,
                groupedAlternates: false,
                rankedSingles: rankedSingles,
                acceptancePolicy))
        {
            return true;
        }

        if (frameIndexes.Count == 1)
        {
            return options.RetryBeamWidth > beamWidth
                && TryBatchCandidates(
                    state,
                    frameIndexes,
                    firstPsn,
                    initialBoundary,
                    target,
                    options,
                    options.RetryBeamWidth,
                    groupedAlternates: false,
                    rankedSingles: rankedSingles,
                    acceptancePolicy);
        }

        int middle = frameIndexes.Count / 2;
        if (TryBatch(
                state,
                frameIndexes.Take(middle).ToArray(),
                firstPsn,
                initialBoundary,
                target,
                options,
                beamWidth,
                rankedSingles,
                acceptancePolicy))
        {
            return true;
        }

        return TryBatch(
            state,
            frameIndexes.Skip(middle).ToArray(),
            firstPsn,
            initialBoundary,
            target,
            options,
            beamWidth,
            rankedSingles,
            acceptancePolicy);
    }

    private static bool TryBatchCandidates(
        SearchState state,
        IReadOnlyList<int> frameIndexes,
        uint firstPsn,
        ModulationBoundary initialBoundary,
        CodewordGrayTarget target,
        SolverOptions options,
        int beamWidth,
        bool groupedAlternates,
        bool rankedSingles = true,
        AcceptancePolicy? acceptancePolicy = null)
    {
        bool[] framesToSolve = new bool[DvdEccBlockEncoder.SyncFrameCount];
        foreach (int frameIndex in frameIndexes)
        {
            framesToSolve[frameIndex] = true;
        }

        BatchCandidateResult?[] candidates = new BatchCandidateResult?[options.BatchCandidates];
        Parallel.For(0, options.BatchCandidates, candidateIndex =>
        {
            byte[] candidatePayload = SolveSweep(
                state.Payload,
                state.Frames,
                state.FrameInputs,
                initialBoundary,
                target,
                options,
                beamWidth,
                framesToSolve,
                frameIndexes,
                candidateIndex,
                options.BatchCandidates,
                groupedAlternates,
                rankedSingles);

            if (candidatePayload.AsSpan().SequenceEqual(state.Payload))
            {
                return;
            }

            byte[] candidateFrames = DvdEccBlockEncoder.UpdateRecordingFrames(
                state.Payload,
                candidatePayload,
                state.Frames,
                firstPsn);
            ModulationBoundary[] candidateInputs =
                new ModulationBoundary[DvdEccBlockEncoder.SyncFrameCount];
            CodewordFrameEvaluation[] candidateFrameEvaluations =
                new CodewordFrameEvaluation[DvdEccBlockEncoder.SyncFrameCount];
            CodewordBlockEvaluation candidateEvaluation = target.Evaluate(
                candidateFrames,
                initialBoundary,
                candidateInputs,
                candidateFrameEvaluations);
            candidates[candidateIndex] = new BatchCandidateResult(
                candidatePayload,
                candidateFrames,
                candidateInputs,
                candidateFrameEvaluations,
                candidateEvaluation);
        });

        BatchCandidateResult? bestCandidate = null;
        foreach (BatchCandidateResult? candidate in candidates)
        {
            if (candidate is null
                || (bestCandidate is not null
                    && !IsBetter(candidate.Evaluation, bestCandidate.Evaluation)))
            {
                continue;
            }

            bestCandidate = candidate;
        }

        if (bestCandidate is null
            || (!IsBetter(bestCandidate.Evaluation, state.Evaluation)
                && !AcceptExploratory(
                    bestCandidate.Evaluation,
                    state.Evaluation,
                    acceptancePolicy)))
        {
            return false;
        }

        state.Payload = bestCandidate.Payload;
        state.Frames = bestCandidate.Frames;
        state.FrameInputs = bestCandidate.FrameInputs;
        state.FrameEvaluations = bestCandidate.FrameEvaluations;
        state.Evaluation = bestCandidate.Evaluation;
        return true;
    }

    private static bool AcceptExploratory(
        CodewordBlockEvaluation candidate,
        CodewordBlockEvaluation current,
        AcceptancePolicy? policy)
    {
        if (policy is null)
        {
            return false;
        }

        double delta = candidate.BinaryErrors - current.BinaryErrors;
        if (delta == 0)
        {
            delta = (candidate.GrayError - current.GrayError) / 16.0;
        }

        if (delta == 0)
        {
            delta = (Math.Abs(candidate.FinalBoundary.Dsv)
                - Math.Abs(current.FinalBoundary.Dsv)) / 16.0;
        }

        return delta <= 0
            || policy.Random.NextDouble() < Math.Exp(-delta / policy.Temperature);
    }

    private static byte[] SolveSweep(
        ReadOnlySpan<byte> currentPayload,
        ReadOnlySpan<byte> currentFrames,
        IReadOnlyList<ModulationBoundary> expectedFrameInputs,
        ModulationBoundary initialBoundary,
        CodewordGrayTarget target,
        SolverOptions options,
        int beamWidth,
        bool[]? framesToSolve,
        IReadOnlyList<int> selectedFrames,
        int candidateVariant,
        int candidateVariantCount,
        bool groupedAlternates,
        bool rankedSingles)
    {
        byte[] nextPayload = currentPayload.ToArray();
        ModulationBoundary passBoundary = initialBoundary;
        EfmPlusWordScorer scorer = new();
        int selectedOrdinal = 0;
        for (int syncFrame = 0; syncFrame < DvdEccBlockEncoder.SyncFrameCount; syncFrame++)
        {
            int recordingOffset = syncFrame * EfmPlusEncoder.BytesPerSyncFrame;
            if (framesToSolve is not null && !framesToSolve[syncFrame])
            {
                passBoundary = scorer.ScoreSyncFrame(
                    currentFrames.Slice(recordingOffset, EfmPlusEncoder.BytesPerSyncFrame),
                    DvdEccBlockEncoder.SyncCategory(syncFrame),
                    passBoundary,
                    target.FrameTargets(syncFrame)).Boundary;
                continue;
            }

            ModulationBoundary expectedOutput = syncFrame + 1 < expectedFrameInputs.Count
                ? expectedFrameInputs[syncFrame + 1]
                : expectedFrameInputs.Count == 0
                    ? passBoundary
                    : scorer.ScoreSyncFrame(
                        currentFrames.Slice(recordingOffset, EfmPlusEncoder.BytesPerSyncFrame),
                        DvdEccBlockEncoder.SyncCategory(syncFrame),
                        passBoundary,
                        target.FrameTargets(syncFrame)).Boundary;
            int choiceRank = CandidateChoiceRank(
                syncFrame,
                selectedOrdinal,
                selectedFrames,
                candidateVariant,
                candidateVariantCount,
                groupedAlternates,
                rankedSingles);
            FrameChoice choice = SolveFrame(
                syncFrame,
                currentPayload,
                currentFrames,
                passBoundary,
                expectedOutput,
                PayloadByRecordingIndex,
                target,
                options,
                beamWidth,
                choiceRank);
            selectedOrdinal++;

            for (int index = 0; index < choice.PayloadIndexes.Length; index++)
            {
                nextPayload[choice.PayloadIndexes[index]] = choice.PayloadValues[index];
            }

            passBoundary = choice.FinalBoundary;
        }

        return nextPayload;
    }

    private static int CandidateChoiceRank(
        int syncFrame,
        int selectedOrdinal,
        IReadOnlyList<int> selectedFrames,
        int candidateVariant,
        int candidateVariantCount,
        bool groupedAlternates,
        bool rankedSingles)
    {
        if (candidateVariant == 0)
        {
            return 0;
        }

        if (selectedFrames.Count == 1)
        {
            return candidateVariant;
        }

        if (!groupedAlternates)
        {
            int singleVariant = candidateVariant - 1;
            bool selected = rankedSingles
                ? syncFrame == selectedFrames[singleVariant % selectedFrames.Count]
                : selectedOrdinal == singleVariant % selectedFrames.Count;
            return selected
                ? 1 + (singleVariant / selectedFrames.Count)
                : 0;
        }

        int phaseCount = Math.Min(candidateVariantCount - 1, selectedFrames.Count);
        int zeroBasedVariant = candidateVariant - 1;
        int phase = zeroBasedVariant % phaseCount;
        if ((selectedOrdinal % phaseCount) != phase)
        {
            return 0;
        }

        return 1 + (zeroBasedVariant / phaseCount);
    }

    private static List<FrameRank> RankFrames(
        IReadOnlyList<CodewordFrameEvaluation> evaluations,
        CodewordGrayTarget target)
    {
        List<FrameRank> ranked = [];
        bool hasMajorityErrors = evaluations.Any(evaluation => evaluation.BinaryErrors > 0);
        for (int frame = 0; frame < evaluations.Count; frame++)
        {
            int targetWords = CountTargets(target.FrameTargets(frame));
            CodewordFrameEvaluation evaluation = evaluations[frame];
            bool isRetryCandidate = hasMajorityErrors
                ? evaluation.BinaryErrors > 0
                : evaluation.GrayError > 0;
            if (targetWords > 0 && isRetryCandidate)
            {
                ranked.Add(new FrameRank(
                    frame,
                    evaluation.BinaryErrors,
                    evaluation.GrayError,
                    targetWords));
            }
        }

        ranked.Sort(CompareFrameRanks);
        return ranked;
    }

    private static int CountTargets(ReadOnlySpan<sbyte> targets)
    {
        int count = 0;
        foreach (sbyte target in targets)
        {
            count += target >= 0 ? 1 : 0;
        }

        return count;
    }

    private static int CompareFrameRanks(FrameRank left, FrameRank right)
    {
        int comparison = ((long)right.BinaryErrors * left.TargetWords)
            .CompareTo((long)left.BinaryErrors * right.TargetWords);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = ((long)right.GrayError * left.TargetWords)
            .CompareTo((long)left.GrayError * right.TargetWords);
        return comparison != 0 ? comparison : left.FrameIndex.CompareTo(right.FrameIndex);
    }

    private static FrameChoice SolveFrame(
        int syncFrame,
        ReadOnlySpan<byte> currentPayload,
        ReadOnlySpan<byte> currentFrames,
        ModulationBoundary input,
        ModulationBoundary expectedOutput,
        IReadOnlyList<int> payloadByRecordingIndex,
        CodewordGrayTarget target,
        SolverOptions options,
        int beamWidth,
        int choiceRank)
    {
        int recordingOffset = syncFrame * EfmPlusEncoder.BytesPerSyncFrame;
        List<int> freeOffsets = [];
        List<int> targetOffsets = [];
        for (int byteOffset = 0; byteOffset < EfmPlusEncoder.BytesPerSyncFrame; byteOffset++)
        {
            int recordingIndex = recordingOffset + byteOffset;
            if (payloadByRecordingIndex[recordingIndex] < 0)
            {
                continue;
            }

            freeOffsets.Add(byteOffset);
            if (target.DesiredAtRecordingIndex(recordingIndex) >= 0)
            {
                targetOffsets.Add(byteOffset);
            }
        }

        HashSet<int> activeOffsets = [.. targetOffsets];
        int bridgeCount = Math.Min(options.BridgeBytes, freeOffsets.Count);
        for (int index = 0; index < bridgeCount; index++)
        {
            activeOffsets.Add(freeOffsets[index]);
            activeOffsets.Add(freeOffsets[^(index + 1)]);
        }

        int[] active = activeOffsets.Order().ToArray();
        byte[] baseBytes = currentFrames
            .Slice(recordingOffset, EfmPlusEncoder.BytesPerSyncFrame)
            .ToArray();
        sbyte[] desired = target.FrameTargets(syncFrame).ToArray();
        EfmPlusWordScorer scorer = new();
        EfmPlusWordScore baseline = scorer.ScoreSyncFrame(
            baseBytes,
            DvdEccBlockEncoder.SyncCategory(syncFrame),
            input,
            desired);
        if (targetOffsets.Count == 0)
        {
            return new FrameChoice([], [], baseline.Boundary);
        }

        BeamNode[] currentNodes = new BeamNode[beamWidth];
        BeamNode[] nextNodes = new BeamNode[beamWidth];
        byte[] currentHistory = new byte[beamWidth * EfmPlusEncoder.BytesPerSyncFrame];
        byte[] nextHistory = new byte[beamWidth * EfmPlusEncoder.BytesPerSyncFrame];
        currentNodes[0] = new BeamNode(EfmPlusPrefixState.Initialize(
            input,
            DvdEccBlockEncoder.SyncCategory(syncFrame)));
        int currentCount = 1;
        List<BeamExpansion> expansions = new(beamWidth * 256);
        byte[] candidateValues = new byte[256];
        bool[] seenValues = new bool[256];

        for (int byteOffset = 0; byteOffset < EfmPlusEncoder.BytesPerSyncFrame; byteOffset++)
        {
            int recordingIndex = recordingOffset + byteOffset;
            sbyte wordTarget = desired[byteOffset];
            bool isPayload = payloadByRecordingIndex[recordingIndex] >= 0;
            bool isBridge = isPayload && activeOffsets.Contains(byteOffset) && wordTarget < 0;
            expansions.Clear();
            for (int parentIndex = 0; parentIndex < currentCount; parentIndex++)
            {
                EfmPlusPrefixState prefix = currentNodes[parentIndex].Prefix;
                int candidateCount = BuildCandidateValues(
                    baseBytes[byteOffset],
                    wordTarget,
                    isBridge,
                    prefix,
                    candidateValues,
                    seenValues);
                for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
                {
                    byte value = candidateValues[candidateIndex];
                    expansions.Add(new BeamExpansion(
                        prefix.Advance(value, wordTarget),
                        parentIndex,
                        value));
                }
            }

            expansions.Sort(CompareExpansions);
            int nextCount = Math.Min(beamWidth, expansions.Count);
            for (int index = 0; index < nextCount; index++)
            {
                BeamExpansion expansion = expansions[index];
                nextNodes[index] = new BeamNode(expansion.Prefix);
                currentHistory.AsSpan(
                    expansion.ParentIndex * EfmPlusEncoder.BytesPerSyncFrame,
                    byteOffset).CopyTo(nextHistory.AsSpan(
                        index * EfmPlusEncoder.BytesPerSyncFrame,
                        byteOffset));
                nextHistory[(index * EfmPlusEncoder.BytesPerSyncFrame) + byteOffset] = expansion.Value;
            }

            (currentNodes, nextNodes) = (nextNodes, currentNodes);
            (currentHistory, nextHistory) = (nextHistory, currentHistory);
            currentCount = nextCount;
        }

        List<FrameCandidate> frameCandidates = [new FrameCandidate(baseBytes, baseline)];
        HashSet<string> seenCandidates = [Convert.ToHexString(baseBytes)];
        for (int index = 0; index < currentCount; index++)
        {
            ReadOnlySpan<byte> candidate = currentHistory.AsSpan(
                index * EfmPlusEncoder.BytesPerSyncFrame,
                EfmPlusEncoder.BytesPerSyncFrame);
            EfmPlusWordScore score = scorer.ScoreSyncFrame(
                candidate,
                DvdEccBlockEncoder.SyncCategory(syncFrame),
                input,
                desired);
            byte[] candidateBytes = candidate.ToArray();
            if (seenCandidates.Add(Convert.ToHexString(candidateBytes)))
            {
                frameCandidates.Add(new FrameCandidate(candidateBytes, score));
            }
        }

        frameCandidates.Sort((left, right) => CompareFrameCandidates(
            left,
            right,
            expectedOutput));
        int selectedIndex = choiceRank <= 0 || frameCandidates.Count == 1
            ? 0
            : 1 + ((choiceRank - 1) % (frameCandidates.Count - 1));
        FrameCandidate selected = frameCandidates[selectedIndex];
        byte[] bestBytes = selected.Bytes;
        ModulationBoundary bestBoundary = selected.Score.Boundary;

        int[] payloadIndexes = new int[active.Length];
        byte[] payloadValues = new byte[active.Length];
        for (int index = 0; index < active.Length; index++)
        {
            int recordingIndex = recordingOffset + active[index];
            int payloadIndex = payloadByRecordingIndex[recordingIndex];
            byte scramblerByte = (byte)(currentFrames[recordingIndex] ^ currentPayload[payloadIndex]);
            payloadIndexes[index] = payloadIndex;
            payloadValues[index] = (byte)(bestBytes[active[index]] ^ scramblerByte);
        }

        return new FrameChoice(payloadIndexes, payloadValues, bestBoundary);
    }

    private static int CompareFrameCandidates(
        FrameCandidate left,
        FrameCandidate right,
        ModulationBoundary expectedOutput)
    {
        int comparison = left.Score.BinaryErrors.CompareTo(right.Score.BinaryErrors);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Score.GrayError.CompareTo(right.Score.GrayError);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = BoundaryPenalty(left.Score.Boundary, expectedOutput)
            .CompareTo(BoundaryPenalty(right.Score.Boundary, expectedOutput));
        return comparison != 0
            ? comparison
            : left.Bytes.AsSpan().SequenceCompareTo(right.Bytes);
    }

    private static int BuildCandidateValues(
        byte baseline,
        sbyte target,
        bool isBridge,
        EfmPlusPrefixState prefix,
        Span<byte> destination,
        Span<bool> seen)
    {
        seen.Clear();
        int count = 0;
        if (isBridge)
        {
            AddPair(prefix.Stream1, destination, seen, ref count);
            AddPair(prefix.Stream2, destination, seen, ref count);
            return count;
        }

        if (target < 0)
        {
            destination[0] = baseline;
            return 1;
        }

        AddTargetCandidate(prefix.Stream1, target, destination, seen, ref count);
        AddTargetCandidate(prefix.Stream2, target, destination, seen, ref count);
        return count;
    }

    private static void AddPair(
        EfmStreamPrefix stream,
        Span<byte> destination,
        Span<bool> seen,
        ref int count)
    {
        EfmPlusBinaryBytePair pair = EfmPlusBinaryBytePairs.Get(stream.State, stream.IsLand, stream.Dsv);
        AddCandidate(pair.BlackByte, destination, seen, ref count);
        AddCandidate(pair.WhiteByte, destination, seen, ref count);
    }

    private static void AddTargetCandidate(
        EfmStreamPrefix stream,
        sbyte target,
        Span<byte> destination,
        Span<bool> seen,
        ref int count)
    {
        EfmPlusBinaryBytePair pair = EfmPlusBinaryBytePairs.Get(stream.State, stream.IsLand, stream.Dsv);
        if (target <= 0)
        {
            AddCandidate(pair.BlackByte, destination, seen, ref count);
        }
        else if (target >= 16)
        {
            AddCandidate(pair.WhiteByte, destination, seen, ref count);
        }
        else
        {
            AddCandidate(pair.BlackByte, destination, seen, ref count);
            AddCandidate(pair.WhiteByte, destination, seen, ref count);
        }
    }

    private static void AddCandidate(
        byte value,
        Span<byte> destination,
        Span<bool> seen,
        ref int count)
    {
        if (seen[value])
        {
            return;
        }

        seen[value] = true;
        destination[count++] = value;
    }

    private static int CompareExpansions(BeamExpansion left, BeamExpansion right)
    {
        EfmStreamPrefix leftSelected = left.Prefix.Selected;
        EfmStreamPrefix rightSelected = right.Prefix.Selected;
        int comparison = leftSelected.BinaryErrors.CompareTo(rightSelected.BinaryErrors);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Math.Min(left.Prefix.Stream1.BinaryErrors, left.Prefix.Stream2.BinaryErrors)
            .CompareTo(Math.Min(right.Prefix.Stream1.BinaryErrors, right.Prefix.Stream2.BinaryErrors));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = leftSelected.GrayError.CompareTo(rightSelected.GrayError);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Math.Min(left.Prefix.Stream1.GrayError, left.Prefix.Stream2.GrayError)
            .CompareTo(Math.Min(right.Prefix.Stream1.GrayError, right.Prefix.Stream2.GrayError));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Math.Abs(leftSelected.Dsv).CompareTo(Math.Abs(rightSelected.Dsv));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.ParentIndex.CompareTo(right.ParentIndex);
        return comparison != 0 ? comparison : left.Value.CompareTo(right.Value);
    }

    private static bool IsBetter(
        CodewordBlockEvaluation candidate,
        CodewordBlockEvaluation currentBest)
        => candidate.BinaryErrors < currentBest.BinaryErrors
            || (candidate.BinaryErrors == currentBest.BinaryErrors
                && (candidate.GrayError < currentBest.GrayError
                    || (candidate.GrayError == currentBest.GrayError
                        && Math.Abs(candidate.FinalBoundary.Dsv)
                            < Math.Abs(currentBest.FinalBoundary.Dsv))));

    private static int BoundaryPenalty(ModulationBoundary actual, ModulationBoundary expected)
        => (actual.State == expected.State ? 0 : 1_000_000)
            + (actual.IsLand == expected.IsLand ? 0 : 100_000)
            + Math.Abs(actual.Dsv - expected.Dsv);

    private static int[] BuildPayloadMap()
    {
        int[] result = new int[208 * 182];
        Array.Fill(result, -1);
        for (int payloadIndex = 0; payloadIndex < DvdEccBlockEncoder.PayloadBytesPerBlock; payloadIndex++)
        {
            result[DvdEccBlockEncoder.PayloadRecordingFrameIndex(payloadIndex)] = payloadIndex;
        }

        return result;
    }

    private static void Validate(ReadOnlySpan<byte> initialPayloads, SolverOptions options)
    {
        if (initialPayloads.Length != DvdEccBlockEncoder.PayloadBytesPerBlock)
        {
            throw new ArgumentException("Initial payload must contain one complete 16-sector ECC Block.", nameof(initialPayloads));
        }

        if (options.Iterations < 0
            || options.BridgeBytes is < 0 or > EfmPlusEncoder.BytesPerSyncFrame
            || options.BeamWidth is < 1 or > 4096
            || options.RetryBeamWidth is < 0 or > 4096
            || double.IsNaN(options.RetryFrameFraction)
            || options.RetryFrameFraction is < 0 or > 1
            || options.BatchFrames is < 1 or > DvdEccBlockEncoder.SyncFrameCount
            || options.BatchCandidates is < 1 or > 64
            || options.SearchLanes is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Invalid state-control pass, bridge, beam, or adaptive retry option.");
        }
    }
}
