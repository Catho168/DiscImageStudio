using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

internal static class BluRaySequentialWriterTests
{
    internal static void Run()
    {
        TestOrderedWritesAndPartialReads();
        TestFailureNeverRetries();
        TestBoundedFailureTimingDiagnostics();
        TestThrownTransportFailureDiagnostics();
        TestLowFrequencyLiveDiagnostics();
        TestDiagnosticObserverCannotStopWrites();
        TestCancellation();
        TestBoundsAndTruncatedStreams();
        TestAlignmentAndVtable();
        Console.WriteLine("blu-ray-sequential-writer: passed (aligned 64 KiB WRITE(10), 200 s timeout, partial reads, bounded failure timings, low-frequency diagnostics, no retry, cancellation; mock transport only)");
    }

    private static void TestOrderedWritesAndPartialReads()
    {
        byte[] data = MakeData(BluRaySequentialWriter.BytesPerWrite * 3);
        FakeStream source = new(data) { MaximumRead = 997 };
        FakeTransport transport = new();
        List<(long Completed, long Total)> progress = [];
        BluRaySequentialWriter.Write(transport, source, 96,
            (completed, total) => progress.Add((completed, total)));
        Require(transport.Commands.Count == 3 && source.ReadCount > 3, "partial IStream reads fill complete blocks before submission");
        for (int i = 0; i < transport.Commands.Count; i++)
        {
            WriteCommand command = transport.Commands[i];
            Require(command.Cdb.Length == 10 && command.Cdb[0] == 0x2a && command.Cdb[1] == 0,
                "the writer sends ordinary WRITE(10), without streaming or forced-unit-access bits");
            Require(BinaryPrimitives.ReadUInt32BigEndian(command.Cdb.AsSpan(2, 4)) == i * 32U
                && BinaryPrimitives.ReadUInt16BigEndian(command.Cdb.AsSpan(7, 2)) == 32,
                "device addresses are contiguous and complete recording-unit blocks");
            Require(command.Timeout == 200, "the first and every subsequent command use an explicit 200-second timeout");
            Require(command.Bytes.SequenceEqual(data.AsSpan(i * BluRaySequentialWriter.BytesPerWrite,
                BluRaySequentialWriter.BytesPerWrite).ToArray()), "exact source bytes survive short reads and aligned buffering");
            Require(command.Pointer == transport.Commands[0].Pointer,
                "one stable aligned data allocation is reused across commands");
        }
        Require(progress[0] == (0, data.Length) && progress[^1] == (data.Length, data.Length),
            "progress counts device-confirmed complete commands, with initial and final reports");
        Require(progress.All(p => p.Completed % BluRaySequentialWriter.BytesPerWrite == 0),
            "progress never counts an unsubmitted source fragment");
        Require(transport.ReadCommands == 0, "writing does not format, reserve, close, or probe the device through unrelated commands");

        // A broken UI observer is not a reason to interrupt accepted device data.
        FakeTransport observerFailure = new();
        BluRaySequentialWriter.Write(observerFailure, new FakeStream(data), 96,
            (_, _) => throw new InvalidOperationException("broken UI"));
        Require(observerFailure.Commands.Count == 3, "progress exceptions cannot stop writing");
    }

    private static void TestFailureNeverRetries()
    {
        byte[] sense = new byte[18];
        sense[0] = 0x70;
        sense[2] = 2;
        sense[12] = 4;
        sense[13] = 8;
        foreach (int failure in new[] { unchecked((int)0xc0aa020d), BluRayMmcCommands.CommandHasSenseData })
        {
            FakeTransport transport = new() { FailureAt = 0, FailureResult = new(failure, sense, []) };
            BluRayWriteException error = Throws<BluRayWriteException>(() => BluRaySequentialWriter.Write(
                transport, new FakeStream(MakeData(BluRaySequentialWriter.BytesPerWrite * 3)), 96));
            Require(transport.Commands.Count == 1, "timeout and busy sense failures are never automatically resent");
            Require(error.StartLba == 0 && error.SectorCount == 32 && error.TimeoutSeconds == 200
                && error.HResult == failure && error.Sense.SequenceEqual(sense),
                "failed command diagnostics preserve exact range, timeout, HRESULT, and sense");
            Require(error.Message.Contains("WRITE(10) 0x2A", StringComparison.Ordinal)
                && error.Message.Contains("200", StringComparison.Ordinal)
                && error.Elapsed >= TimeSpan.Zero, "diagnostics identify the command and measured command duration");
            Require(error.Diagnostics is { AcceptedBytes: 0, AcceptedCommandCount: 0 }
                && error.Diagnostics.RecentSuccessfulCommands.Count == 0,
                "a failed first WRITE has no accepted data or invented successful history");
        }
        FakeTransport secondFailure = new()
        {
            FailureAt = 1,
            FailureResult = new(unchecked((int)0xc0aa020d), [], []),
        };
        BluRayWriteException second = Throws<BluRayWriteException>(() => BluRaySequentialWriter.Write(
            secondFailure, new FakeStream(MakeData(BluRaySequentialWriter.BytesPerWrite * 3)), 96));
        Require(secondFailure.Commands.Count == 2 && second.StartLba == 32,
            "a later failure preserves acknowledged earlier data and does not resend or skip ahead");
    }

    private static void TestBoundedFailureTimingDiagnostics()
    {
        TimeSpan now = TimeSpan.Zero;
        int reads = 0;
        int commands = 0;
        FakeStream source = new(MakeData(BluRaySequentialWriter.BytesPerWrite * 12))
        {
            AfterRead = () => now += TimeSpan.FromMilliseconds(++reads * 100),
        };
        byte[] sense = new byte[18];
        FakeTransport transport = new()
        {
            FailureAt = 10,
            FailureResult = new(unchecked((int)0xc0aa020d), sense, []),
            AfterSend = () => now += TimeSpan.FromMilliseconds(++commands == 11 ? 7 : commands * 10),
        };
        BluRayWriteException error = Throws<BluRayWriteException>(() => BluRaySequentialWriter.Write(
            transport, source, 384,
            (completed, _) => { if (completed == 0) now += TimeSpan.FromMilliseconds(250); },
            elapsed: () => now));
        BluRayWriteDiagnostics diagnostics = error.Diagnostics
            ?? throw new InvalidOperationException("Expected failed WRITE timing snapshot.");
        Require(transport.Commands.Count == 11 && reads == 11
            && source.Position == BluRaySequentialWriter.BytesPerWrite * 11
            && transport.ReadCommands == 0,
            "failure diagnostics do not retry, read ahead, or issue extra device commands");
        Require(error.StartLba == 320 && error.Elapsed == TimeSpan.FromMilliseconds(7)
            && error.HResult == unchecked((int)0xc0aa020d) && error.Sense.SequenceEqual(sense),
            "a fast failed command retains its original LBA, timing, HRESULT, and zero-filled sense");
        Require(diagnostics.AcceptedBytes == 10L * BluRaySequentialWriter.BytesPerWrite
            && diagnostics.AcceptedCommandCount == 10,
            "accepted totals include all successes and exclude the uncertain failed WRITE");
        Require(diagnostics.TotalSourceReadElapsed == TimeSpan.FromMilliseconds(6600)
            && diagnostics.TotalCommandElapsed == TimeSpan.FromMilliseconds(557)
            && diagnostics.SessionElapsed == TimeSpan.FromMilliseconds(7407),
            "source reads and device commands have separate cumulative timings including the failed attempt; observer time belongs only to session time");
        Require(diagnostics.FailedCommand == new BluRayWriteTiming(320, 32,
            TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(7)),
            "the failed block's source read is distinguished from its fast device failure");
        Require(diagnostics.RecentSuccessfulCommands.Count == 8,
            "the snapshot remains bounded after more than eight successful writes");
        for (int i = 0; i < diagnostics.RecentSuccessfulCommands.Count; i++)
        {
            int command = i + 2;
            Require(diagnostics.RecentSuccessfulCommands[i] == new BluRayWriteTiming((uint)command * 32, 32,
                TimeSpan.FromMilliseconds((command + 1) * 100), TimeSpan.FromMilliseconds((command + 1) * 10)),
                "the ring preserves only the newest successes in chronological order with their own timings");
        }
        Require(error.Message.Contains("最近 8 次成功 WRITE", StringComparison.Ordinal)
            && error.Message.Contains("累计源流读取", StringComparison.Ordinal)
            && error.Message.Contains("累计 WRITE 命令", StringComparison.Ordinal),
            "the bounded snapshot is attached to the propagated error text for normal failure logs");
    }

    private static void TestThrownTransportFailureDiagnostics()
    {
        TimeSpan now = TimeSpan.Zero;
        COMException original = new("Device disconnected", unchecked((int)0x8007048f));
        FakeTransport transport = new()
        {
            FailureAt = 1,
            SendFailure = original,
            AfterSend = () => now += TimeSpan.FromMilliseconds(7),
        };
        FakeStream source = new(MakeData(BluRaySequentialWriter.BytesPerWrite * 3))
        {
            MaximumRead = BluRaySequentialWriter.BytesPerWrite / 2,
            AfterRead = () => now += TimeSpan.FromMilliseconds(20),
        };
        BluRayWriteException error = Throws<BluRayWriteException>(() => BluRaySequentialWriter.Write(
            transport, source, 96, elapsed: () => now));
        BluRayWriteDiagnostics diagnostics = error.Diagnostics
            ?? throw new InvalidOperationException("Expected thrown transport failure timing snapshot.");
        Require(transport.Commands.Count == 2 && ReferenceEquals(error.InnerException, original)
            && error.HResult == original.HResult && error.Sense.Length == 0,
            "a throwing transport keeps the original exception and HRESULT without fabricated sense or retries");
        Require(diagnostics.AcceptedCommandCount == 1 && diagnostics.RecentSuccessfulCommands.Count == 1
            && diagnostics.TotalSourceReadElapsed == TimeSpan.FromMilliseconds(80)
            && diagnostics.TotalCommandElapsed == TimeSpan.FromMilliseconds(14)
            && diagnostics.SessionElapsed == TimeSpan.FromMilliseconds(94),
            "all partial reads contribute to each block's read time independently of throwing command time");
    }

    private static void TestLowFrequencyLiveDiagnostics()
    {
        TimeSpan now = TimeSpan.Zero;
        FakeStream source = new(MakeData(BluRaySequentialWriter.BytesPerWrite * 5))
        {
            AfterRead = () => now += TimeSpan.FromSeconds(1),
        };
        FakeTransport transport = new() { AfterSend = () => now += TimeSpan.FromSeconds(3) };
        List<(TimeSpan At, string Message)> diagnostics = [];
        BluRaySequentialWriter.Write(transport, source, 160, elapsed: () => now,
            reportDiagnostics: message => diagnostics.Add((now, message)));
        Require(transport.Commands.Count == 5 && source.ReadCount == 5 && transport.ReadCommands == 0,
            "periodic diagnostics issue no additional commands or source reads");
        Require(diagnostics.Count == 2 && diagnostics[0].At == TimeSpan.FromSeconds(12)
            && diagnostics[1].At == TimeSpan.FromSeconds(20),
            "diagnostics wait at least ten seconds between ordinary reports and flush the final shorter window");
        string first = diagnostics[0].Message;
        string last = diagnostics[1].Message;
        Require(first.Contains($"窗口 {12.0:F3} 秒", StringComparison.Ordinal)
            && first.Contains($"设备接受速度={0.016384:F3} MB/s", StringComparison.Ordinal)
            && first.Contains($"={3.0:F3} 秒（{25.0:F1}%）", StringComparison.Ordinal)
            && first.Contains($"={9.0:F3} 秒（{75.0:F1}%）", StringComparison.Ordinal)
            && first.Contains($"累计接受={3L * BluRaySequentialWriter.BytesPerWrite:N0} 字节", StringComparison.Ordinal),
            "the first diagnostic reports its accepted-byte rate, separate read/command durations and shares, and cumulative bytes");
        Require(!first.Contains("最后窗口", StringComparison.Ordinal)
            && last.Contains("最后窗口", StringComparison.Ordinal)
            && last.Contains($"窗口 {8.0:F3} 秒", StringComparison.Ordinal)
            && last.Contains($"={2.0:F3} 秒（{25.0:F1}%）", StringComparison.Ordinal)
            && last.Contains($"={6.0:F3} 秒（{75.0:F1}%）", StringComparison.Ordinal)
            && last.Contains($"累计接受={5L * BluRaySequentialWriter.BytesPerWrite:N0} 字节", StringComparison.Ordinal),
            "the last report resets the window's counters while retaining cumulative accepted bytes");
        Require(first.Contains("含阻塞/拷贝", StringComparison.Ordinal)
            && first.Contains("含传输/设备等待", StringComparison.Ordinal)
            && first.Contains("不是纯 CPU 生成耗时", StringComparison.Ordinal),
            "diagnostic labels state the measured boundaries instead of claiming CPU generation or laser speed");

        now = TimeSpan.Zero;
        diagnostics.Clear();
        FakeStream exactSource = new(MakeData(BluRaySequentialWriter.BytesPerWrite * 2))
        {
            AfterRead = () => now += TimeSpan.FromSeconds(1),
        };
        FakeTransport exactTransport = new() { AfterSend = () => now += TimeSpan.FromSeconds(4) };
        BluRaySequentialWriter.Write(exactTransport, exactSource, 64, elapsed: () => now,
            reportDiagnostics: message => diagnostics.Add((now, message)));
        Require(diagnostics.Count == 1 && diagnostics[0].At == TimeSpan.FromSeconds(10)
            && diagnostics[0].Message.Contains("最后窗口", StringComparison.Ordinal),
            "completion exactly at the ten-second boundary produces one report rather than a duplicate final report");
    }

    private static void TestDiagnosticObserverCannotStopWrites()
    {
        TimeSpan now = TimeSpan.Zero;
        int callbacks = 0;
        List<long> progress = [];
        FakeStream source = new(MakeData(BluRaySequentialWriter.BytesPerWrite * 3))
        {
            AfterRead = () => now += TimeSpan.FromSeconds(4),
        };
        FakeTransport transport = new() { AfterSend = () => now += TimeSpan.FromSeconds(2) };
        BluRaySequentialWriter.Write(transport, source, 96,
            (completed, _) => progress.Add(completed), elapsed: () => now,
            reportDiagnostics: _ => { callbacks++; throw new InvalidOperationException("broken diagnostics observer"); });
        Require(callbacks == 2 && transport.Commands.Count == 3 && transport.ReadCommands == 0
            && progress[^1] == 3L * BluRaySequentialWriter.BytesPerWrite,
            "a throwing periodic or final diagnostic observer cannot interrupt writes, create retries, or suppress final accepted-byte progress");
    }

    private static void TestCancellation()
    {
        FakeTransport beforeStart = new();
        Throws<OperationCanceledException>(() => BluRaySequentialWriter.Write(beforeStart,
            new FakeStream(MakeData(BluRaySequentialWriter.BytesPerWrite)), 32, isCancellationRequested: () => true));
        Require(beforeStart.LimitQueries == 0 && beforeStart.Commands.Count == 0,
            "pre-cancellation performs no transport or device action");

        bool cancelled = false;
        FakeTransport afterCommand = new() { AfterSend = () => cancelled = true };
        Throws<OperationCanceledException>(() => BluRaySequentialWriter.Write(afterCommand,
            new FakeStream(MakeData(BluRaySequentialWriter.BytesPerWrite * 3)), 96,
            isCancellationRequested: () => cancelled));
        Require(afterCommand.Commands.Count == 1, "cancellation waits for an in-flight command and sends no successor");

        cancelled = false;
        FakeTransport whileReading = new();
        FakeStream source = new(MakeData(BluRaySequentialWriter.BytesPerWrite))
        {
            MaximumRead = 1024,
            AfterRead = () => cancelled = true,
        };
        Throws<OperationCanceledException>(() => BluRaySequentialWriter.Write(whileReading,
            source, 32, isCancellationRequested: () => cancelled));
        Require(whileReading.Commands.Count == 0, "cancellation while filling a block does not submit a partial WRITE");
    }

    private static void TestBoundsAndTruncatedStreams()
    {
        foreach (uint count in new[] { 0U, 1U, 31U, 33U, 12_219_424U, uint.MaxValue })
        {
            FakeTransport transport = new();
            Throws<ArgumentOutOfRangeException>(() => BluRaySequentialWriter.Write(transport,
                new FakeStream([]), count));
            Require(transport.Commands.Count == 0 && transport.LimitQueries == 0,
                "invalid sector counts fail before transport access");
        }
        FakeTransport wrongLength = new();
        Throws<ArgumentException>(() => BluRaySequentialWriter.Write(wrongLength, new FakeStream([1, 2, 3]), 32));
        Require(wrongLength.Commands.Count == 0, "the declared stream length must exactly match the planned sectors");

        FakeStream truncated = new(MakeData(4096)) { DeclaredLength = BluRaySequentialWriter.BytesPerWrite };
        FakeTransport shortInput = new();
        Throws<EndOfStreamException>(() => BluRaySequentialWriter.Write(shortInput, truncated, 32));
        Require(shortInput.Commands.Count == 0, "a short source never sends a partially filled block");

        FakeStream advanced = new(MakeData(BluRaySequentialWriter.BytesPerWrite)) { Position = 1 };
        FakeTransport startedInput = new();
        Throws<ArgumentException>(() => BluRaySequentialWriter.Write(startedInput, advanced, 32));
        Require(startedInput.Commands.Count == 0, "an already-consumed stream cannot silently shift disc addresses");

        byte[] last = BluRaySequentialWriter.CreateWriteCommand(12_219_392 - 32);
        Require(BinaryPrimitives.ReadUInt32BigEndian(last.AsSpan(2, 4)) == 12_219_360,
            "the complete physical capacity is addressable without shrinking the disc");
        Throws<ArgumentOutOfRangeException>(() => BluRaySequentialWriter.CreateWriteCommand(12_219_392));
    }

    private static void TestAlignmentAndVtable()
    {
        foreach (uint alignment in new[] { 0U, 1U, 3U, 511U, 4095U, 4096U, 65535U })
        {
            using AlignedMmcWriteBuffer buffer = new(BluRaySequentialWriter.BytesPerWrite,
                new(alignment, BluRaySequentialWriter.BytesPerWrite));
            ulong address = unchecked((ulong)buffer.Pointer.ToInt64());
            Require(address % (uint)Environment.SystemPageSize == 0 && (address & alignment) == 0,
                "the actual unmanaged pointer satisfies OS-page and recorder-mask alignment");
        }
        FakeTransport smallAdapter = new() { Limits = new(0, 32768) };
        Throws<NotSupportedException>(() => BluRaySequentialWriter.Write(smallAdapter,
            new FakeStream(MakeData(BluRaySequentialWriter.BytesPerWrite)), 32));
        Require(smallAdapter.Commands.Count == 0, "adapter limits are enforced before the first irreversible write");
        Throws<NotSupportedException>(() => new AlignedMmcWriteBuffer(65536, new(uint.MaxValue, 65536)));

        string[] methods = typeof(IDiscRecorder2ExCommands).GetMethods()
            .OrderBy(method => method.MetadataToken).Select(method => method.Name).ToArray();
        Require(methods.Length == 18 && methods[0] == "SendCommandNoData"
            && methods[1] == "SendCommandSendDataToDevice" && methods[2] == "SendCommandGetDataFromDevice"
            && methods[15] == "GetByteAlignmentMask" && methods[16] == "GetMaximumNonPageAlignedTransferSize"
            && methods[17] == "GetMaximumPageAlignedTransferSize",
            "transfer-limit queries occupy the documented full IDiscRecorder2Ex vtable slots");
    }

    private static byte[] MakeData(int count)
    {
        byte[] data = new byte[count];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 37 + i / 97) & 255);
        return data;
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD WRITE(10): " + message);
    }

    private sealed record WriteCommand(byte[] Cdb, byte[] Bytes, uint Timeout, IntPtr Pointer);

    private sealed class FakeTransport : IMmcCommandTransport
    {
        internal List<WriteCommand> Commands { get; } = [];
        internal int LimitQueries { get; private set; }
        internal int ReadCommands { get; private set; }
        internal MmcWriteTransferLimits Limits { get; init; } = new(4095, 65536);
        internal int FailureAt { get; init; } = -1;
        internal MmcCommandResult FailureResult { get; init; } = new(unchecked((int)0xc0aa020d), [], []);
        internal Exception? SendFailure { get; init; }
        internal Action? AfterSend { get; init; }
        public MmcWriteTransferLimits GetWriteTransferLimits() { LimitQueries++; return Limits; }
        public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
        {
            Require(bufferLength == 65536 && bufferLength <= Limits.MaximumPageAlignedBytes,
                "mock sees a supported complete block");
            ulong address = unchecked((ulong)buffer.ToInt64());
            Require(address % (uint)Environment.SystemPageSize == 0 && (address & Limits.ByteAlignmentMask) == 0,
                "the pointer submitted to the transport is aligned");
            byte[] data = new byte[bufferLength];
            Marshal.Copy(buffer, data, 0, data.Length);
            Commands.Add(new(cdb.ToArray(), data, timeoutSeconds, buffer));
            AfterSend?.Invoke();
            if (Commands.Count - 1 == FailureAt && SendFailure is not null) throw SendFailure;
            return Commands.Count - 1 == FailureAt ? FailureResult : new(0, new byte[18], []);
        }
        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            ReadCommands++;
            throw new InvalidOperationException("No read/format/close commands belong in the write loop.");
        }
    }

    private sealed class FakeStream(byte[] data) : IStream
    {
        internal int MaximumRead { get; init; } = int.MaxValue;
        internal long? DeclaredLength { get; init; }
        internal int Position { get; set; }
        internal int ReadCount { get; private set; }
        internal Action? AfterRead { get; init; }
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int count = Math.Min(Math.Min(cb, MaximumRead), data.Length - Position);
            Array.Copy(data, Position, pv, 0, count);
            Position += count;
            ReadCount++;
            Marshal.WriteInt32(pcbRead, count);
            AfterRead?.Invoke();
        }
        public void Stat(out STATSTG pstatstg, int grfStatFlag)
            => pstatstg = new() { cbSize = DeclaredLength ?? data.Length, type = 2 };
        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            if (dlibMove != 0 || dwOrigin != 1) throw new NotSupportedException("The writer must not rewind or skip input.");
            Marshal.WriteInt64(plibNewPosition, Position);
        }
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
        public void SetSize(long libNewSize) => throw new NotSupportedException();
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void Clone(out IStream ppstm) => throw new NotSupportedException();
    }
}
