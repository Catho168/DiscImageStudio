using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

public sealed class WindowsImapiBurner : IOpticalDiscBurner
{
    private const int BlankMediaState = 0x2;

    public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
        => RunOnStaThread(() => EnumerateDevices(cancellationToken));

    public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return RunOnStaThread(() => EnumerateWriteSpeeds(
            deviceId,
            mediaKind,
            cancellationToken));
    }

    public Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return RunOnStaThread(() => Burn(request, progress, cancellationToken));
    }

    private static IReadOnlyList<OpticalBurnDevice> EnumerateDevices(
        CancellationToken cancellationToken)
    {
        dynamic master = CreateComObject("IMAPI2.MsftDiscMaster2");
        try
        {
            if (!(bool)master.IsSupportedEnvironment)
            {
                return [];
            }

            int count = (int)master.Count;
            List<OpticalBurnDevice> devices = new(count);
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = (string)master.Item(index);
                dynamic recorder = CreateRecorder(id);
                try
                {
                    string vendor = Convert.ToString(recorder.VendorId)?.Trim() ?? string.Empty;
                    string product = Convert.ToString(recorder.ProductId)?.Trim() ?? string.Empty;
                    IReadOnlyList<string> volumes = ReadStrings(recorder.VolumePathNames);
                    string drive = volumes.Count == 0 ? string.Empty : $" ({string.Join(", ", volumes)})";
                    string model = string.Join(" ", new[] { vendor, product }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                    devices.Add(new OpticalBurnDevice(
                        id,
                        (model.Length == 0 ? $"光驱 {index + 1}" : model) + drive,
                        volumes));
                }
                finally
                {
                    ReleaseComObject(recorder);
                }
            }

            return devices;
        }
        finally
        {
            ReleaseComObject(master);
        }
    }

    private static IReadOnlyList<OpticalWriteSpeed> EnumerateWriteSpeeds(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic recorder = CreateRecorder(deviceId);
        dynamic? format = null;
        try
        {
            format = mediaKind == OpticalBurnMediaKind.CdAudio
                ? CreateComObject("IMAPI2.MsftDiscFormat2TrackAtOnce")
                : CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            if (!(bool)format.IsRecorderSupported(recorder))
            {
                throw new InvalidOperationException("所选光驱不支持这种刻录方式。");
            }

            if (!(bool)format.IsCurrentMediaSupported(recorder))
            {
                throw new InvalidOperationException("请放入与所选 CD/DVD 模式兼容的可写盘片。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ReadSupportedWriteSpeeds(format);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"读取当前盘片的刻录速度失败（0x{exception.HResult:X8}）：{exception.Message}",
                exception);
        }
        finally
        {
            if (format is not null)
            {
                ReleaseComObject(format);
            }

            ReleaseComObject(recorder);
        }
    }

    private static OpticalBurnResult Burn(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string currentOperation = "初始化 Windows 刻录接口";
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OpticalBurnProgress(
            "device",
            "正在检查刻录机和空白介质…",
            0,
            request.ContentLength));
        dynamic recorder = CreateRecorder(request.DeviceId);
        dynamic? format = null;
        try
        {
            currentOperation = "创建刻录格式";
            format = request.MediaKind == OpticalBurnMediaKind.CdAudio
                ? CreateComObject("IMAPI2.MsftDiscFormat2TrackAtOnce")
                : CreateComObject("IMAPI2.MsftDiscFormat2Data");
            currentOperation = "连接刻录机";
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            currentOperation = "检查刻录机支持";
            if (!(bool)format.IsRecorderSupported(recorder))
            {
                throw new InvalidOperationException("所选光驱不支持这种刻录方式。");
            }

            currentOperation = "检查当前盘片兼容性";
            if (!(bool)format.IsCurrentMediaSupported(recorder))
            {
                throw new InvalidOperationException("当前盘片与所选 CD/DVD 模式不兼容。");
            }

            using GeneratedContentComStream content = new(
                request.ContentLength,
                request.ProduceContent,
                (completed, total) => progress?.Report(new OpticalBurnProgress(
                    "write",
                    $"正在流式刻录 {completed / (1024.0 * 1024.0):F1} / {total / (1024.0 * 1024.0):F1} MiB…",
                    completed,
                    total)),
                cancellationToken);
            progress?.Report(new OpticalBurnProgress(
                "buffer",
                "正在准备防断流缓冲区…",
                0,
                request.ContentLength));
            content.WaitUntilPrebuffered(cancellationToken);

            OpticalWriteSpeed? actualWriteSpeed;
            if (request.MediaKind == OpticalBurnMediaKind.CdAudio)
            {
                actualWriteSpeed = BurnCdAudio(
                    new DynamicCdTrackAtOnceSession(format),
                    content,
                    request,
                    cancellationToken,
                    operation => currentOperation = operation,
                    speed => ReportWriteSpeed(progress, speed, request));
            }
            else
            {
                currentOperation = "写入 DVD 数据";
                Action<OpticalWriteSpeed> reportDvdWriteSpeed =
                    speed => ReportWriteSpeed(progress, speed, request);
                actualWriteSpeed = BurnDvdData(
                    format,
                    content,
                    request,
                    cancellationToken,
                    reportDvdWriteSpeed);
            }

            progress?.Report(new OpticalBurnProgress(
                "finalize",
                "刻录完成，正在关闭会话…",
                request.ContentLength,
                request.ContentLength));
            stopwatch.Stop();
            return new OpticalBurnResult(
                request.DeviceId,
                request.MediaKind,
                request.ContentLength,
                stopwatch.Elapsed,
                actualWriteSpeed);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"Windows 刻录接口在“{currentOperation}”时返回错误 "
                + $"0x{exception.HResult:X8}：{exception.Message}",
                exception);
        }
        finally
        {
            if (format is not null)
            {
                ReleaseComObject(format);
            }

            ReleaseComObject(recorder);
        }
    }

    internal static OpticalWriteSpeed BurnCdAudio(
        ICdTrackAtOnceSession format,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<string>? reportOperation = null,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null)
    {
        reportOperation?.Invoke("设置 CD 会话关闭方式");
        format.DoNotFinalizeMedia = false;
        bool prepared = false;
        Exception? operationException = null;
        try
        {
            reportOperation?.Invoke("准备并锁定 CD 介质");
            format.PrepareMedia();
            prepared = true;
            if (request.WriteSpeed is not null)
            {
                reportOperation?.Invoke("设置 CD 刻录速度");
                format.SetWriteSpeed(
                    request.WriteSpeed.SectorsPerSecond,
                    request.WriteSpeed.RotationTypeIsPureCav);
            }

            OpticalWriteSpeed actualWriteSpeed = new(
                format.CurrentWriteSpeed,
                format.CurrentRotationTypeIsPureCav);
            actualWriteSpeed.Validate();
            reportWriteSpeed?.Invoke(actualWriteSpeed);
            // IMAPI enables buffer-underrun-free recording by default. Avoid changing
            // the prepared-only property because some drives reject the redundant set.
            reportOperation?.Invoke("检查 CD 是否为空白盘");
            if (format.NumberOfExistingTracks != 0)
            {
                throw new InvalidOperationException("CD 不是空白盘，已取消刻录。");
            }

            reportOperation?.Invoke("检查 CD 可用容量");
            long requiredSectors = request.ContentLength / 2352;
            if (format.FreeSectorsOnMedia < requiredSectors)
            {
                throw new InvalidOperationException("CD 剩余容量不足。");
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                format.CancelAddTrack);
            reportOperation?.Invoke("写入 CD 音轨");
            format.AddAudioTrack(content);
            return actualWriteSpeed;
        }
        catch (Exception exception)
        {
            operationException = exception;
            throw;
        }
        finally
        {
            if (prepared)
            {
                if (operationException is null)
                {
                    reportOperation?.Invoke("关闭 CD 刻录会话");
                    format.ReleaseMedia();
                }
                else
                {
                    TryReleaseCdMedia(format);
                }
            }
        }
    }

    private static void TryReleaseCdMedia(ICdTrackAtOnceSession format)
    {
        try
        {
            format.ReleaseMedia();
        }
        catch
        {
            // Preserve the first failure. Some drives automatically unprepare the
            // media after a write error, making ReleaseMedia fail with 0xC0AA0502.
        }
    }

    private static OpticalWriteSpeed BurnDvdData(
        dynamic format,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null)
    {
        int mediaState = (int)format.CurrentMediaStatus;
        if ((mediaState & BlankMediaState) == 0)
        {
            throw new InvalidOperationException("DVD 不是空白盘，已取消刻录。");
        }

        long requiredSectors = request.ContentLength / 2048;
        if ((long)format.FreeSectorsOnMedia < requiredSectors)
        {
            throw new InvalidOperationException("DVD 剩余容量不足。");
        }

        format.BufferUnderrunFreeDisabled = false;
        format.ForceMediaToBeClosed = true;
        format.ForceOverwrite = false;
        if (request.WriteSpeed is not null)
        {
            format.SetWriteSpeed(
                request.WriteSpeed.SectorsPerSecond,
                request.WriteSpeed.RotationTypeIsPureCav);
        }

        OpticalWriteSpeed actualWriteSpeed = new(
            Convert.ToInt32(format.CurrentWriteSpeed),
            Convert.ToBoolean(format.CurrentRotationTypeIsPureCAV));
        actualWriteSpeed.Validate();
        reportWriteSpeed?.Invoke(actualWriteSpeed);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            TryCancel(format, cdAudio: false));
        format.Write(content);
        return actualWriteSpeed;
    }

    private static void ReportWriteSpeed(
        IProgress<OpticalBurnProgress>? progress,
        OpticalWriteSpeed speed,
        OpticalBurnRequest request)
    {
        progress?.Report(new OpticalBurnProgress(
            "speed",
            $"刻录机已采用 {speed.GetMultiplier(request.MediaKind):0.#}×"
                + $"（{speed.GetMegabytesPerSecond(request.MediaKind):0.0} MB/s）…",
            0,
            request.ContentLength));
    }

    private static IReadOnlyList<OpticalWriteSpeed> ReadSupportedWriteSpeeds(dynamic format)
    {
        List<OpticalWriteSpeed> result = [];
        try
        {
            if (format.SupportedWriteSpeedDescriptors is Array descriptors)
            {
                foreach (object? item in descriptors)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    try
                    {
                        dynamic descriptor = item;
                        int sectorsPerSecond = Convert.ToInt32(descriptor.WriteSpeed);
                        if (sectorsPerSecond > 0)
                        {
                            result.Add(new OpticalWriteSpeed(
                                sectorsPerSecond,
                                Convert.ToBoolean(descriptor.RotationTypeIsPureCAV)));
                        }
                    }
                    finally
                    {
                        ReleaseComObject(item);
                    }
                }
            }
        }
        catch (COMException)
        {
            // Older drives may expose only the simple speed list. Fall back below.
        }

        if (result.Count == 0 && format.SupportedWriteSpeeds is Array speeds)
        {
            foreach (object? item in speeds)
            {
                int sectorsPerSecond = Convert.ToInt32(item);
                if (sectorsPerSecond > 0)
                {
                    result.Add(new OpticalWriteSpeed(sectorsPerSecond, false));
                }
            }
        }

        return result
            .Distinct()
            .OrderBy(speed => speed.SectorsPerSecond)
            .ThenBy(speed => speed.RotationTypeIsPureCav)
            .ToArray();
    }

    private static void TryCancel(dynamic format, bool cdAudio)
    {
        try
        {
            if (cdAudio)
            {
                format.CancelAddTrack();
            }
            else
            {
                format.CancelWrite();
            }
        }
        catch
        {
            // The operation may already be finishing; the producer token is also cancelled.
        }
    }

    private static dynamic CreateRecorder(string id)
    {
        dynamic recorder = CreateComObject("IMAPI2.MsftDiscRecorder2");
        recorder.InitializeDiscRecorder(id);
        return recorder;
    }

    private static dynamic CreateComObject(string programmaticId)
    {
        Type type = Type.GetTypeFromProgID(programmaticId, throwOnError: false)
            ?? throw new PlatformNotSupportedException(
                "Windows Image Mastering API 2 (IMAPI2) 不可用。");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"无法创建 Windows 刻录组件 {programmaticId}。");
    }

    private static IReadOnlyList<string> ReadStrings(object? value)
    {
        if (value is not Array array)
        {
            return [];
        }

        List<string> result = [];
        foreach (object? item in array)
        {
            string? text = Convert.ToString(item)?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> action)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "DiscImageStudio.IMAPI2",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
