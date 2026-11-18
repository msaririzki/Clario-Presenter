using Clario_Presenter.Audio;
using Clario_Presenter.Capture;
using Clario_Presenter.Camera;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;
using Windows.UI;

namespace Clario_Presenter.Recording;

/// <summary>
/// Records the same retained frame that Clario draws to the client output.
/// Each sample owns an independent BGRA buffer until Media Foundation has
/// finished with it, preventing partially rendered or reused GPU surfaces
/// from appearing as one-frame black flashes in the resulting video.
/// </summary>
public sealed class OutputRecordingService : IAsyncDisposable
{
    public const uint OutputWidth = 1920;
    public const uint OutputHeight = 1080;

    private const int MaximumFrameCount = 12;
    private const uint BytesPerPixel = 4;

    private readonly object _frameGate = new();
    private readonly ConcurrentBag<StableCpuFrame> _availableFrames = [];
    private readonly ConcurrentDictionary<StableCpuFrame, byte> _outstandingFrames = new();
    private readonly CanvasDevice _canvasDevice = CanvasDevice.GetSharedDevice();
    private readonly CancellationTokenSource _producerCancellation = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);

    private IRecordingFrameSource? _capture;
    private CaptureScaleMode _scaleMode;
    private float _adaptiveZoom;
    private Func<ClientOutputMode>? _modeProvider;
    private CameraCaptureService? _camera;
    private CameraOverlayOptions? _cameraOptions;
    private CameraRectangle _cameraBounds;
    private readonly IRecordingAudioSource? _suppliedAudio;
    private IRecordingAudioSource? _audio;
    private long _clockStart;
    private long _nextAudioFrame;
    private long _audioEndFrame = long.MaxValue;
    private readonly object _audioPrivacyGate = new();
    private readonly Queue<(long Start, long End)> _audioPrivacyIntervals = new();
    private long? _audioPrivacyStarted;
    private StableCpuFrame? _pendingFrame;
    private MediaStreamSource? _mediaSource;
    private IRandomAccessStream? _outputStream;
    private Task? _producerTask;
    private Task? _transcodeTask;
    private TimeSpan _frameDuration;
    private uint _frameRate;
    private int _allocatedFrameCount;
    private volatile bool _stopping;
    private bool _stopCompleted;
    private bool _disposed;

    public bool IsRecording => _transcodeTask is { IsCompleted: false } && !_stopping;
    public Task Completion => _transcodeTask ?? Task.CompletedTask;
    public Exception? Failure { get; private set; }
    public string? CameraWarning => _camera?.Warning;
    public bool IncludesCamera => _cameraOptions?.Enabled == true;
    public bool IncludesAudio => _audio is not null;
    public string? AudioWarning => _audio?.Warning;

    internal void SetPreviewMeters(bool enabled)
    {
        if (_audio is RecordingAudioSource audio) audio.MeteringEnabled = enabled;
    }

    internal AudioLevelReading? ReadPreviewLevel(bool loopback) =>
        (_audio as RecordingAudioSource)?.ReadLevel(loopback);

    internal void DrawPreviewCamera(CanvasDrawingSession session)
    {
        if (_camera is not null && _cameraOptions is { Enabled: true })
            CameraOverlayRenderer.Draw(session, _camera, _cameraBounds, _cameraOptions.Mirror);
    }

    public OutputRecordingService() { }

    internal OutputRecordingService(IRecordingAudioSource audio) => _suppliedAudio = audio;

    public async Task StartAsync(
        IRecordingFrameSource capture,
        CaptureScaleMode scaleMode,
        float adaptiveZoom,
        Func<ClientOutputMode> modeProvider,
        IRandomAccessStream outputStream,
        RecordingOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transcodeTask is not null) throw new InvalidOperationException("Perekam sudah digunakan.");

        options ??= RecordingOptions.HighQuality60Fps;
        _capture = capture;
        _scaleMode = scaleMode;
        _adaptiveZoom = adaptiveZoom;
        _modeProvider = modeProvider;
        _frameRate = (uint)Math.Clamp(options.FrameRate, 30, 60);
        _frameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / _frameRate);
        _outputStream = outputStream;
        _outputStream.Size = 0;
        _outputStream.Seek(0);

        _cameraOptions = options.Camera;
        if (_cameraOptions?.Enabled == true)
        {
            _cameraBounds = CameraOverlayLayout.GetBounds(OutputWidth, OutputHeight, _cameraOptions);
            _camera = new CameraCaptureService();
            // Start on the UI thread so Windows can request camera access.
            // Do not create an apparently successful camera recording without
            // first receiving a real frame from the selected device.
            await _camera.StartAsync(_cameraOptions.DeviceId, recordingBounds: _cameraBounds);
        }

        var inputProperties = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8,
            OutputWidth,
            OutputHeight);
        inputProperties.FrameRate.Numerator = _frameRate;
        inputProperties.FrameRate.Denominator = 1;
        inputProperties.PixelAspectRatio.Numerator = 1;
        inputProperties.PixelAspectRatio.Denominator = 1;

        var descriptor = new VideoStreamDescriptor(inputProperties);
        var audioOptions = options.Audio;
        _audio = _suppliedAudio ?? (audioOptions is { Enabled: true }
            ? new RecordingAudioSource(audioOptions, AudioShouldBeMuted)
            : null);
        if (_audio is not null)
        {
            await _audio.StartAsync(_producerCancellation.Token);
            var pcm = AudioEncodingProperties.CreatePcm(RecordingAudioFormat.SampleRate, RecordingAudioFormat.Channels, 16);
            _mediaSource = new MediaStreamSource(descriptor, new AudioStreamDescriptor(pcm));
        }
        else
        {
            _mediaSource = new MediaStreamSource(descriptor);
        }
        _mediaSource.BufferTime = TimeSpan.Zero;
        _mediaSource.Starting += MediaSource_Starting;
        _mediaSource.SampleRequested += MediaSource_SampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = _audio is null
            ? null
            : AudioEncodingProperties.CreateAac(RecordingAudioFormat.SampleRate, RecordingAudioFormat.Channels, 192_000);
        profile.Video.Width = OutputWidth;
        profile.Video.Height = OutputHeight;
        profile.Video.Bitrate = (uint)Math.Clamp(options.BitrateMbps, 8, 32) * 1_000_000;
        profile.Video.FrameRate.Numerator = _frameRate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;

        var transcoder = new MediaTranscoder
        {
            HardwareAccelerationEnabled = true,
            AlwaysReencode = true
        };
        var preparation = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
            _mediaSource,
            outputStream,
            profile);

        if (!preparation.CanTranscode)
        {
            throw new InvalidOperationException($"Perangkat belum dapat merekam 1080p {_frameRate} fps. Coba pilih 30 fps di Pengaturan.");
        }

        _clockStart = Stopwatch.GetTimestamp();
        _audio?.StartClock(_clockStart);
        SetAudioPrivacy(_modeProvider() is ClientOutputMode.Privacy or ClientOutputMode.SourceLost);
        _producerTask = Task.Run(() => ProduceFramesAsync(_producerCancellation.Token));
        _transcodeTask = preparation.TranscodeAsync().AsTask();
    }

    public async Task StopAsync()
    {
        await _stopGate.WaitAsync();
        try
        {
            if (_stopCompleted) return;

            SignalEndOfStream();

            if (_producerTask is not null)
            {
                try
                {
                    await _producerTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected when recording is stopped normally.
                }
            }

            try
            {
                if (_transcodeTask is not null) await _transcodeTask;
                if (_outputStream is not null) await _outputStream.FlushAsync();
            }
            finally
            {
                try
                {
                    if (_audio is not null) await _audio.Completion;
                    if (_camera is not null) await _camera.DisposeAsync();
                }
                finally
                {
                    _camera = null;
                    DisposeMediaObjects();
                    _stopCompleted = true;
                }
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task ProduceFramesAsync(CancellationToken cancellationToken)
    {
        long nextFrameIndex = 0;

        try
        {
            // Only the producer uses this target. The encoder reads the copied
            // CPU buffer, which remains reserved until the sample is processed.
            using var renderTarget = new CanvasRenderTarget(
                _canvasDevice,
                OutputWidth,
                OutputHeight,
                96,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Ignore);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Keep timestamps tied to real elapsed time. When a slower GPU
                // misses a 60 fps slot, skip that timestamp instead of making
                // the final recording run faster than the live presentation.
                var elapsedFrameIndex = Stopwatch.GetElapsedTime(_clockStart).Ticks / _frameDuration.Ticks;
                var frameIndex = Math.Max(nextFrameIndex, elapsedFrameIndex);

                if (!RefreshPendingFrameTimestamp(frameIndex))
                {
                    RenderFrame(renderTarget, frameIndex);
                }

                nextFrameIndex = frameIndex + 1;

                var nextFrameTime = TimeSpan.FromTicks(nextFrameIndex * _frameDuration.Ticks);
                var delay = nextFrameTime - Stopwatch.GetElapsedTime(_clockStart);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception exception)
        {
            Failure = exception;
            SignalEndOfStream();
        }
    }

    private bool RefreshPendingFrameTimestamp(long frameIndex)
    {
        lock (_frameGate)
        {
            if (_pendingFrame is null) return false;

            // Media Foundation has not requested the previous frame yet. Do not
            // waste another GPU render/readback; keep that sample aligned with
            // the newest real-time slot and let the encoder consume it first.
            _pendingFrame.Timestamp = TimeSpan.FromTicks(frameIndex * _frameDuration.Ticks);
            return true;
        }
    }

    private void RenderFrame(CanvasRenderTarget renderTarget, long frameIndex)
    {
        var frame = RentFrame();
        try
        {
            using (var drawingSession = renderTarget.CreateDrawingSession())
            {
                // Media Foundation interprets packed RGB rows bottom-up. Render
                // the intermediate target upside-down so the encoded MP4 is upright.
                drawingSession.Transform = Matrix3x2.CreateScale(1, -1)
                    * Matrix3x2.CreateTranslation(0, OutputHeight);
                var mode = _modeProvider?.Invoke() ?? ClientOutputMode.SourceLost;
                if (_capture is not { HasFrame: true }
                    || mode is ClientOutputMode.Privacy or ClientOutputMode.SourceLost)
                {
                    DrawPrivacySlate(drawingSession, mode);
                }
                else
                {
                    _capture.DrawRecordingFrame(
                        drawingSession,
                        OutputWidth,
                        OutputHeight,
                        _scaleMode,
                        _adaptiveZoom);

                    DrawPreviewCamera(drawingSession);
                }
            }

            frame.Buffer.Length = frame.Buffer.Capacity;
            renderTarget.GetPixelBytes(frame.Buffer);
            frame.Timestamp = TimeSpan.FromTicks(frameIndex * _frameDuration.Ticks);

            lock (_frameGate)
            {
                if (_stopping)
                {
                    RecycleFrame(frame);
                    return;
                }

                if (_pendingFrame is null)
                {
                    _pendingFrame = frame;
                }
                else
                {
                    // Defensive fallback for a rare producer/request race.
                    _pendingFrame.Timestamp = frame.Timestamp;
                    RecycleFrame(frame);
                }
                Monitor.Pulse(_frameGate);
            }
        }
        catch
        {
            RecycleFrame(frame);
            throw;
        }
    }

    private StableCpuFrame RentFrame()
    {
        if (_availableFrames.TryTake(out var reusable)) return reusable;
        if (Interlocked.Increment(ref _allocatedFrameCount) > MaximumFrameCount)
        {
            Interlocked.Decrement(ref _allocatedFrameCount);
            throw new InvalidOperationException("Perangkat tidak dapat memproses rekaman tepat waktu. Coba pilih 30 fps di Pengaturan.");
        }

        try
        {
            var buffer = new Windows.Storage.Streams.Buffer(OutputWidth * OutputHeight * BytesPerPixel)
            {
                Length = OutputWidth * OutputHeight * BytesPerPixel
            };
            return new StableCpuFrame(buffer);
        }
        catch
        {
            Interlocked.Decrement(ref _allocatedFrameCount);
            throw;
        }
    }

    private static void DrawPrivacySlate(CanvasDrawingSession drawingSession, ClientOutputMode mode)
    {
        drawingSession.Clear(Color.FromArgb(255, 14, 17, 22));
        var title = mode == ClientOutputMode.SourceLost ? "Tampilan terputus" : "Presentasi dijeda";
        var subtitle = mode == ClientOutputMode.SourceLost
            ? "Pilih kembali layar atau aplikasi di perangkat presenter"
            : "Mohon tunggu sebentar";

        using var titleFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Display",
            FontSize = 38,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        using var subtitleFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 20,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };

        drawingSession.DrawText(
            title,
            new Rect(0, 455, OutputWidth, 75),
            Color.FromArgb(255, 245, 247, 250),
            titleFormat);
        drawingSession.DrawText(
            subtitle,
            new Rect(0, 530, OutputWidth, 48),
            Color.FromArgb(255, 150, 158, 174),
            subtitleFormat);
    }

    private void MediaSource_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
        args.Request.SetActualStartPosition(TimeSpan.Zero);

    private void MediaSource_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (args.Request.StreamDescriptor is AudioStreamDescriptor)
        {
            _ = SupplyAudioAsync(args.Request);
            return;
        }

        var frame = TakeNextFrame();
        if (frame is null)
        {
            args.Request.Sample = null;
            return;
        }

        var tracked = false;
        try
        {
            var sample = MediaStreamSample.CreateFromBuffer(frame.Buffer, frame.Timestamp);
            sample.Duration = _frameDuration;
            tracked = _outstandingFrames.TryAdd(frame, 0);
            sample.Processed += (_, _) => ReleaseOutstandingFrame(frame);
            args.Request.Sample = sample;
        }
        catch (Exception exception)
        {
            Failure = exception;
            if (tracked) ReleaseOutstandingFrame(frame);
            else RecycleFrame(frame);
            args.Request.Sample = null;
        }
    }

    private bool AudioShouldBeMuted() => _capture is not { HasFrame: true }
        || _modeProvider?.Invoke() is not (ClientOutputMode.Live or ClientOutputMode.Frozen);

    internal void SetAudioPrivacy(bool enabled)
    {
        if (_audio is null || _clockStart == 0) return;
        lock (_audioPrivacyGate)
        {
            if (enabled == _audioPrivacyStarted.HasValue) return;
            var frame = RecordingAudioFormat.FramesAt(Stopwatch.GetElapsedTime(_clockStart));
            if (enabled) _audioPrivacyStarted = frame;
            else if (_audioPrivacyStarted is long start)
            {
                _audioPrivacyIntervals.Enqueue((start, frame));
                _audioPrivacyStarted = null;
            }
        }
    }

    private void SuppressPrivateAudio(long firstFrame, Span<short> samples)
    {
        lock (_audioPrivacyGate)
        {
            while (_audioPrivacyIntervals.TryPeek(out var interval) && interval.End <= firstFrame)
                _audioPrivacyIntervals.Dequeue();

            foreach (var interval in _audioPrivacyIntervals)
                MuteOverlap(firstFrame, samples, interval.Start, interval.End);
            if (_audioPrivacyStarted is long start)
                MuteOverlap(firstFrame, samples, start, long.MaxValue);
        }
    }

    private static void MuteOverlap(long firstFrame, Span<short> pcm, long start, long end)
    {
        const int channels = RecordingAudioFormat.Channels;
        var packetFrames = pcm.Length / channels;
        var from = (int)Math.Clamp(start - firstFrame, 0, packetFrames);
        var to = (int)Math.Clamp(end - firstFrame, 0, packetFrames);
        if (to > from) pcm.Slice(from * channels, (to - from) * channels).Clear();
    }

    private async Task SupplyAudioAsync(MediaStreamSourceSampleRequest request)
    {
        var deferral = request.GetDeferral();
        try
        {
            const int packetFrames = RecordingAudioFormat.PacketFrames;
            var firstFrame = Interlocked.Add(ref _nextAudioFrame, packetFrames) - packetFrames;
            // The capture timestamps already use the video clock. A short lead
            // lets USB and playback packets reach the mixer before they are read.
            var due = RecordingAudioFormat.PacketTime(firstFrame + packetFrames) + TimeSpan.FromMilliseconds(80);
            var wait = due - Stopwatch.GetElapsedTime(_clockStart);
            if (wait > TimeSpan.Zero && !_stopping)
            {
                try { await Task.Delay(wait, _producerCancellation.Token); }
                catch (OperationCanceledException) when (_stopping) { }
            }

            var frames = (int)Math.Clamp(Volatile.Read(ref _audioEndFrame) - firstFrame, 0, packetFrames);
            if (frames == 0 || _audio is null)
            {
                request.Sample = null;
                return;
            }

            var samples = new short[frames * RecordingAudioFormat.Channels];
            if (!AudioShouldBeMuted())
            {
                _audio.Read(firstFrame, samples);
                SuppressPrivateAudio(firstFrame, samples);
            }
            var pcm = new byte[samples.Length * sizeof(short)];
            System.Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            using var writer = new DataWriter();
            writer.WriteBytes(pcm);
            var sample = MediaStreamSample.CreateFromBuffer(writer.DetachBuffer(), RecordingAudioFormat.PacketTime(firstFrame));
            sample.Duration = RecordingAudioFormat.PacketTime(frames);
            request.Sample = sample;
        }
        catch (Exception exception)
        {
            Failure = exception;
            request.Sample = null;
            SignalEndOfStream();
        }
        finally { deferral.Complete(); }
    }

    private StableCpuFrame? TakeNextFrame()
    {
        lock (_frameGate)
        {
            while (!_stopping && _pendingFrame is null) Monitor.Wait(_frameGate);
            if (_stopping) return null;
            var frame = _pendingFrame;
            _pendingFrame = null;
            return frame;
        }
    }

    private void ReleaseOutstandingFrame(StableCpuFrame frame)
    {
        if (_outstandingFrames.TryRemove(frame, out _)) RecycleFrame(frame);
    }

    private void RecycleFrame(StableCpuFrame? frame)
    {
        if (frame is not null && !_disposed) _availableFrames.Add(frame);
    }

    private void SignalEndOfStream()
    {
        _stopping = true;
        var endFrame = _clockStart == 0 ? 0 : RecordingAudioFormat.FramesAt(Stopwatch.GetElapsedTime(_clockStart));
        Interlocked.CompareExchange(ref _audioEndFrame, endFrame, long.MaxValue);
        _producerCancellation.Cancel();
        lock (_frameGate)
        {
            RecycleFrame(_pendingFrame);
            _pendingFrame = null;
            Monitor.PulseAll(_frameGate);
        }
    }

    private void DisposeMediaObjects()
    {
        if (_mediaSource is not null)
        {
            _mediaSource.Starting -= MediaSource_Starting;
            _mediaSource.SampleRequested -= MediaSource_SampleRequested;
        }
        _mediaSource = null;

        _outstandingFrames.Clear();
        _availableFrames.Clear();
        _pendingFrame = null;
        _audio = null;
        _capture = null;
        _modeProvider = null;
        _outputStream?.Dispose();
        _outputStream = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            await StopAsync();
        }
        finally
        {
            _disposed = true;
            DisposeMediaObjects();
            _producerCancellation.Dispose();
        }
    }

    private sealed class StableCpuFrame(Windows.Storage.Streams.Buffer buffer)
    {
        public Windows.Storage.Streams.Buffer Buffer { get; } = buffer;
        public TimeSpan Timestamp { get; set; }
    }
}

public sealed record RecordingOptions(int FrameRate, int BitrateMbps, CameraOverlayOptions? Camera = null,
    RecordingAudioOptions? Audio = null)
{
    public static RecordingOptions HighQuality60Fps { get; } = new(60, 18);
}
