using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DiscImageStudio.Burning;

/// <summary>A single failed WRITE. A timeout leaves the command's device-side
/// outcome uncertain; callers must never resend this range automatically.</summary>
internal sealed class BluRayWriteException : IOException
{
    internal BluRayWriteException(uint startLba, int sectors, uint timeoutSeconds,
        TimeSpan elapsed, MmcCommandResult result, Exception? innerException = null,
        BluRayWriteDiagnostics? diagnostics = null)
        : base($"蓝光 WRITE(10) 0x2A 失败：LBA={startLba:N0}，扇区={sectors}，"
            + $"超时设置={timeoutSeconds} 秒，命令耗时={elapsed.TotalSeconds:F3} 秒；"
            + new MmcCommandException(0x2a, result).Message
            + (result.Sense.Length == 0 ? " 无返回 Sense 数据。" : $" 原始 Sense={Convert.ToHexString(result.Sense)}。")
            + " 未自动重试此写入命令。" + diagnostics?.Describe(), innerException)
    {
        StartLba = startLba;
        SectorCount = sectors;
        TimeoutSeconds = timeoutSeconds;
        Elapsed = elapsed;
        HResult = result.HResult;
        Sense = result.Sense.ToArray();
        Diagnostics = diagnostics;
    }

    internal uint StartLba { get; }
    internal int SectorCount { get; }
    internal uint TimeoutSeconds { get; }
    internal TimeSpan Elapsed { get; }
    internal byte[] Sense { get; }
    internal BluRayWriteDiagnostics? Diagnostics { get; }
}

internal readonly record struct BluRayWriteTiming(uint StartLba, int SectorCount,
    TimeSpan SourceReadElapsed, TimeSpan CommandElapsed);

/// <summary>Created only after a failed WRITE. Timings include the failed attempt;
/// accepted counters and the bounded history contain successful commands only.</summary>
internal sealed record BluRayWriteDiagnostics(long AcceptedBytes, int AcceptedCommandCount,
    TimeSpan TotalSourceReadElapsed, TimeSpan TotalCommandElapsed, TimeSpan SessionElapsed,
    IReadOnlyList<BluRayWriteTiming> RecentSuccessfulCommands, BluRayWriteTiming FailedCommand)
{
    internal string Describe()
    {
        StringBuilder text = new();
        text.AppendLine();
        text.Append($"写入诊断：设备已接受 {AcceptedBytes:N0} 字节 / {AcceptedCommandCount:N0} 次命令；");
        text.Append($"累计源流读取（含阻塞及块拷贝）={TotalSourceReadElapsed.TotalSeconds:F3} 秒，");
        text.Append($"累计 WRITE 命令={TotalCommandElapsed.TotalSeconds:F3} 秒，");
        text.AppendLine($"本次写入调用={SessionElapsed.TotalSeconds:F3} 秒（耗时累计包含失败尝试）。");
        text.AppendLine($"失败块：源流读取={FailedCommand.SourceReadElapsed.TotalSeconds:F3} 秒，"
            + $"WRITE={FailedCommand.CommandElapsed.TotalSeconds:F3} 秒。");
        text.Append($"最近 {RecentSuccessfulCommands.Count} 次成功 WRITE（最多 8 次，按时间顺序）：");
        foreach (BluRayWriteTiming command in RecentSuccessfulCommands)
        {
            text.AppendLine();
            text.Append($"LBA={command.StartLba:N0}，扇区={command.SectorCount}，"
                + $"源流读取={command.SourceReadElapsed.TotalSeconds:F3} 秒，"
                + $"WRITE={command.CommandElapsed.TotalSeconds:F3} 秒");
        }
        return text.ToString();
    }
}

/// <summary>Writes sequential BD-R user data with explicit per-command timeouts.
/// The transport acknowledges accepted WRITE commands, which may still reside
/// in the drive cache. Reported throughput is device acceptance, not an
/// independently measured optical/laser position; finalization drains the cache.</summary>
internal static class BluRaySequentialWriter
{
    internal const int SectorBytes = 2048;
    internal const int SectorsPerWrite = 32;
    internal const int BytesPerWrite = SectorBytes * SectorsPerWrite;
    internal const uint WriteTimeoutSeconds = 200;
    private const uint MaximumSectors = 12_219_392;
    private const int DiagnosticHistoryCapacity = 8;

    // libburn's normal BD-R path uses 64 KiB writes and a 200000 ms timeout:
    // https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/write.c
    // https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/spc.h
    // IMAPI's documented SendCommandSendDataToDevice Timeout is in seconds.
    internal static void Write(IMmcCommandTransport transport, IStream content, uint sectorCount,
        Action<long, long>? reportProgress = null, Func<bool>? isCancellationRequested = null,
        Func<TimeSpan>? elapsed = null, Action<string>? reportDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(content);
        if (sectorCount == 0 || sectorCount % SectorsPerWrite != 0 || sectorCount > MaximumSectors)
            throw new ArgumentOutOfRangeException(nameof(sectorCount),
                "BD-R writes require complete 32-sector clusters within single-layer capacity.");
        ThrowIfCancelled(isCancellationRequested);
        long startedAt = Stopwatch.GetTimestamp();
        elapsed ??= () => Stopwatch.GetElapsedTime(startedAt);
        TimeSpan sessionStarted = elapsed();
        long length = (long)sectorCount * SectorBytes;
        content.Stat(out STATSTG stat, 1);
        if (stat.cbSize != length)
            throw new ArgumentException("蓝光流长度与计划写入的扇区数不一致。", nameof(content));

        using AlignedMmcWriteBuffer buffer = new(BytesPerWrite, transport.GetWriteTransferLimits());
        byte[] readBuffer = new byte[BytesPerWrite];
        IntPtr countPointer = Marshal.AllocHGlobal(sizeof(long));
        TimeSpan lastReport = TimeSpan.Zero;
        BluRayWriteTiming[] history = new BluRayWriteTiming[DiagnosticHistoryCapacity];
        int acceptedCommands = 0;
        long acceptedBytes = 0;
        TimeSpan sourceReadTotal = TimeSpan.Zero;
        TimeSpan commandTotal = TimeSpan.Zero;
        TimeSpan diagnosticWindowStarted = sessionStarted;
        TimeSpan diagnosticSourceRead = TimeSpan.Zero;
        TimeSpan diagnosticCommandTime = TimeSpan.Zero;
        long diagnosticAcceptedBytes = 0;
        try
        {
            // Query the current position without seeking backwards; the producer
            // stream is forward-only and must begin at its declared byte zero.
            content.Seek(0, 1, countPointer);
            if (Marshal.ReadInt64(countPointer) != 0)
                throw new ArgumentException("蓝光写入必须从源数据流的起始位置开始。", nameof(content));
            Publish(reportProgress, 0, length);
            for (uint lba = 0; lba < sectorCount; lba += SectorsPerWrite)
            {
                ThrowIfCancelled(isCancellationRequested);
                TimeSpan readStarted = elapsed();
                ReadBlock(content, readBuffer, buffer.Pointer, countPointer, isCancellationRequested);
                TimeSpan sourceReadElapsed = elapsed() - readStarted;
                sourceReadTotal += sourceReadElapsed;
                ThrowIfCancelled(isCancellationRequested);
                byte[] cdb = CreateWriteCommand(lba);
                TimeSpan commandStarted = elapsed();
                MmcCommandResult result;
                Exception? transportFailure = null;
                try { result = transport.SendData(cdb, buffer.Pointer, BytesPerWrite, WriteTimeoutSeconds); }
                catch (Exception exception) when (exception is COMException or IOException)
                {
                    transportFailure = exception;
                    result = new(exception.HResult, [], []);
                }
                TimeSpan commandEnded = elapsed();
                TimeSpan commandElapsed = commandEnded - commandStarted;
                commandTotal += commandElapsed;
                BluRayWriteTiming timing = new(lba, SectorsPerWrite, sourceReadElapsed, commandElapsed);
                if (transportFailure is not null || result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
                {
                    int historyCount = Math.Min(acceptedCommands, DiagnosticHistoryCapacity);
                    BluRayWriteTiming[] recent = new BluRayWriteTiming[historyCount];
                    for (int index = 0; index < historyCount; index++)
                        recent[index] = history[(acceptedCommands - historyCount + index) % DiagnosticHistoryCapacity];
                    BluRayWriteDiagnostics diagnostics = new(acceptedBytes, acceptedCommands,
                        sourceReadTotal, commandTotal, commandEnded - sessionStarted,
                        Array.AsReadOnly(recent), timing);
                    throw new BluRayWriteException(lba, SectorsPerWrite, WriteTimeoutSeconds,
                        commandElapsed, result, transportFailure, diagnostics);
                }

                history[acceptedCommands % DiagnosticHistoryCapacity] = timing;
                acceptedCommands++;
                acceptedBytes += BytesPerWrite;

                long completed = ((long)lba + SectorsPerWrite) * SectorBytes;
                TimeSpan now = commandEnded - sessionStarted;
                if (now - lastReport >= TimeSpan.FromMilliseconds(250) || completed == length)
                {
                    Publish(reportProgress, completed, length);
                    lastReport = now;
                }
                // Low-frequency host diagnostics run only between acknowledged
                // commands. No timer or extra device query races a pending WRITE.
                TimeSpan windowElapsed = commandEnded - diagnosticWindowStarted;
                if (reportDiagnostics is not null
                    && (windowElapsed >= TimeSpan.FromSeconds(10) || completed == length))
                {
                    PublishDiagnostics(reportDiagnostics, FormatWindowDiagnostics(windowElapsed,
                        acceptedBytes - diagnosticAcceptedBytes, acceptedBytes,
                        sourceReadTotal - diagnosticSourceRead, commandTotal - diagnosticCommandTime,
                        completed == length));
                    diagnosticWindowStarted = commandEnded;
                    diagnosticSourceRead = sourceReadTotal;
                    diagnosticCommandTime = commandTotal;
                    diagnosticAcceptedBytes = acceptedBytes;
                }
                // Cancellation cannot undo an accepted command. Check only after
                // its completion; never race another command or attempt a resend.
                ThrowIfCancelled(isCancellationRequested);
            }
        }
        finally { Marshal.FreeHGlobal(countPointer); }
    }

    private static void ReadBlock(IStream content, byte[] readBuffer, IntPtr destination, IntPtr countPointer,
        Func<bool>? isCancellationRequested)
    {
        int filled = 0;
        while (filled < BytesPerWrite)
        {
            ThrowIfCancelled(isCancellationRequested);
            int requested = BytesPerWrite - filled;
            Marshal.WriteInt32(countPointer, 0);
            content.Read(readBuffer, requested, countPointer);
            int read = Marshal.ReadInt32(countPointer);
            if (read <= 0 || read > requested)
                throw new EndOfStreamException($"蓝光流未提供完整写入块：已读取 {filled} / {BytesPerWrite} 字节，本次返回 {read} 字节。");
            Marshal.Copy(readBuffer, 0, IntPtr.Add(destination, filled), read);
            filled += read;
        }
    }

    internal static byte[] CreateWriteCommand(uint lba)
    {
        if (lba % SectorsPerWrite != 0 || lba > MaximumSectors - SectorsPerWrite)
            throw new ArgumentOutOfRangeException(nameof(lba));
        byte[] cdb = new byte[10];
        cdb[0] = 0x2a;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), lba);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), SectorsPerWrite);
        return cdb;
    }

    private static void ThrowIfCancelled(Func<bool>? isCancellationRequested)
    {
        if (isCancellationRequested?.Invoke() == true)
            throw new OperationCanceledException("蓝光刻录已取消。");
    }

    private static void Publish(Action<long, long>? reportProgress, long completed, long total)
    {
        try { reportProgress?.Invoke(completed, total); }
        catch { /* Progress observers must not interrupt a physical write. */ }
    }

    private static string FormatWindowDiagnostics(TimeSpan windowElapsed, long windowAcceptedBytes,
        long acceptedBytes, TimeSpan sourceReadElapsed, TimeSpan commandElapsed, bool finalWindow)
    {
        double seconds = windowElapsed.TotalSeconds;
        string rate = seconds > 0 ? $"{windowAcceptedBytes / seconds / 1_000_000.0:F3} MB/s" : "测量时间不足";
        string sourceShare = seconds > 0 ? $"{sourceReadElapsed.TotalSeconds / seconds * 100:F1}%" : "—";
        string commandShare = seconds > 0 ? $"{commandElapsed.TotalSeconds / seconds * 100:F1}%" : "—";
        return $"BD 写入诊断{(finalWindow ? "（最后窗口）" : string.Empty)}：窗口 {seconds:F3} 秒，"
            + $"设备接受速度={rate}；源流读取（含阻塞/拷贝）={sourceReadElapsed.TotalSeconds:F3} 秒（{sourceShare}），"
            + $"WRITE（含传输/设备等待）={commandElapsed.TotalSeconds:F3} 秒（{commandShare}）；"
            + $"累计接受={acceptedBytes:N0} 字节。源流读取耗时不是纯 CPU 生成耗时。";
    }

    private static void PublishDiagnostics(Action<string> reportDiagnostics, string message)
    {
        try { reportDiagnostics(message); }
        catch { /* Diagnostic observers must not interrupt or retry a device write. */ }
    }
}

/// <summary>One reusable unmanaged transfer buffer, aligned both to the OS page
/// boundary and the recorder's adapter requirement. No pinned-array assumption.</summary>
internal sealed class AlignedMmcWriteBuffer : IDisposable
{
    private IntPtr _allocation;
    internal IntPtr Pointer { get; private set; }

    internal AlignedMmcWriteBuffer(int length, MmcWriteTransferLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (length <= 0 || length > limits.MaximumPageAlignedBytes)
            throw new NotSupportedException($"刻录适配器的页对齐单次传输上限为 {limits.MaximumPageAlignedBytes:N0} 字节，"
                + $"无法提交 {length:N0} 字节的完整蓝光写入块。");
        // SDK docs call this both a mask and an alignment value. Aligning to
        // the next power of two above it satisfies either interpretation.
        ulong required = Math.Max((ulong)Environment.SystemPageSize, (ulong)limits.ByteAlignmentMask + 1);
        ulong alignment = 1;
        while (alignment < required) alignment <<= 1;
        if (alignment > int.MaxValue || (ulong)length + alignment - 1 > int.MaxValue)
            throw new NotSupportedException("刻录适配器报告了无法支持的缓冲区对齐要求。");
        _allocation = Marshal.AllocHGlobal(checked(length + (int)alignment - 1));
        ulong address = unchecked((ulong)_allocation.ToInt64());
        ulong aligned = (address + alignment - 1) & ~(alignment - 1);
        Pointer = new IntPtr(unchecked((long)aligned));
    }

    public void Dispose()
    {
        if (_allocation == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_allocation);
        _allocation = IntPtr.Zero;
        Pointer = IntPtr.Zero;
    }
}
