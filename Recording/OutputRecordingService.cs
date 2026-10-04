using Clario_Presenter.Capture;
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

    private CaptureSessionService? _capture;
    private CaptureScaleMode _scaleMode;
    private float _adaptiveZoom;
    private Func<ClientOutputMode>? _modeProvider;
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

    public async Task StartAsync(
        CaptureSessionService capture,
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

        var inputProperties = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8,
            OutputWidth,
            OutputHeight);
        inputProperties.FrameRate.Numerator = _frameRate;
        inputProperties.FrameRate.Denominator = 1;
        inputProperties.PixelAspectRatio.Numerator = 1;
        inputProperties.PixelAspectRatio.Denominator = 1;

        var descriptor = new VideoStreamDescriptor(inputProperties);
        _mediaSource = new MediaStreamSource(descriptor) { BufferTime = TimeSpan.Zero };
        _mediaSource.Starting += MediaSource_Starting;
        _mediaSource.SampleRequested += MediaSource_SampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
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
            throw new InvalidOperationException($"Encoder 1080p{_frameRate} tidak tersedia ({preparation.FailureReason}).");
        }

        _producerTask = Task.Run(() => ProduceFramesAsync(_producerCancellation.Token));
        _transcodeTask = preparation.TranscodeAsync().AsTask();
    }

    public async Task StopAsync()
    {
        await _stopGate.WaitAsync();
        try
        {
            if (_stopCompleted) return;

            _stopping = true;
            _producerCancellation.Cancel();
            lock (_frameGate)
            {
                RecycleFrame(_pendingFrame);
                _pendingFrame = null;
                Monitor.PulseAll(_frameGate);
            }

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
                DisposeMediaObjects();
                _stopCompleted = true;
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task ProduceFramesAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        long nextFrameIndex = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RenderFrame(nextFrameIndex++);

                var nextFrameTime = TimeSpan.FromTicks(nextFrameIndex * _frameDuration.Ticks);
                var delay = nextFrameTime - clock.Elapsed;
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

    private void RenderFrame(long frameIndex)
    {
        var frame = RentFrame();
        try
        {
            using (var drawingSession = frame.RenderTarget.CreateDrawingSession())
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
                }
            }

            frame.Buffer.Length = frame.Buffer.Capacity;
            frame.RenderTarget.GetPixelBytes(frame.Buffer);
            frame.Timestamp = TimeSpan.FromTicks(frameIndex * _frameDuration.Ticks);

            lock (_frameGate)
            {
                if (_stopping)
                {
                    RecycleFrame(frame);
                    return;
                }

                RecycleFrame(_pendingFrame);
                _pendingFrame = frame;
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
            throw new InvalidOperationException("Encoder tidak mengembalikan buffer tepat waktu.");
        }

        try
        {
            var renderTarget = new CanvasRenderTarget(
                _canvasDevice,
                OutputWidth,
                OutputHeight,
                96,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Ignore);
            var buffer = new Windows.Storage.Streams.Buffer(OutputWidth * OutputHeight * BytesPerPixel)
            {
                Length = OutputWidth * OutputHeight * BytesPerPixel
            };
            return new StableCpuFrame(renderTarget, buffer);
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
        var title = mode == ClientOutputMode.SourceLost ? "Sumber terputus" : "Presentasi dijeda";
        var subtitle = mode == ClientOutputMode.SourceLost
            ? "Pilih ulang sumber di laptop presenter"
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
        var frame = TakeNextFrame();
        if (frame is null)
        {
            args.Request.Sample = null;
            return;
        }

        try
        {
            var sample = MediaStreamSample.CreateFromBuffer(frame.Buffer, frame.Timestamp);
            sample.Duration = _frameDuration;
            _outstandingFrames.TryAdd(frame, 0);
            sample.Processed += (_, _) => ReleaseOutstandingFrame(frame);
            args.Request.Sample = sample;
        }
        catch (Exception exception)
        {
            Failure = exception;
            RecycleFrame(frame);
            args.Request.Sample = null;
        }
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

        foreach (var frame in _outstandingFrames.Keys) ReleaseOutstandingFrame(frame);
        while (_availableFrames.TryTake(out var availableFrame)) availableFrame.Dispose();
        _pendingFrame?.Dispose();
        _pendingFrame = null;
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

    private sealed class StableCpuFrame(
        CanvasRenderTarget renderTarget,
        Windows.Storage.Streams.Buffer buffer) : IDisposable
    {
        public CanvasRenderTarget RenderTarget { get; } = renderTarget;
        public Windows.Storage.Streams.Buffer Buffer { get; } = buffer;
        public TimeSpan Timestamp { get; set; }

        public void Dispose() => RenderTarget.Dispose();
    }
}

public sealed record RecordingOptions(int FrameRate, int BitrateMbps)
{
    public static RecordingOptions HighQuality60Fps { get; } = new(60, 18);
}
