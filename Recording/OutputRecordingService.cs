using Microsoft.Graphics.Canvas;
using System.Collections.Concurrent;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace Clario_Presenter.Recording;

/// <summary>
/// Records the client output window itself. This keeps presenter notes and
/// private desktop applications outside the recording by design.
/// </summary>
public sealed class OutputRecordingService : IAsyncDisposable
{
    public const uint OutputWidth = 1920;
    public const uint OutputHeight = 1080;
    public const uint OutputFrameRate = 60;
    public const uint OutputBitrate = 18_000_000;

    private static readonly TimeSpan FrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / OutputFrameRate);

    private readonly object _frameGate = new();
    private readonly ConcurrentDictionary<Direct3D11CaptureFrame, byte> _outstandingFrames = new();
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly SemaphoreSlim _stopGate = new(1, 1);

    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private Direct3D11CaptureFrame? _pendingFrame;
    private MediaStreamSource? _mediaSource;
    private IRandomAccessStream? _outputStream;
    private Task? _transcodeTask;
    private TimeSpan? _firstFrameTime;
    private long _lastFrameSlot = -1;
    private volatile bool _stopping;
    private bool _stopCompleted;
    private bool _disposed;

    public bool IsRecording => _transcodeTask is { IsCompleted: false } && !_stopping;
    public Task Completion => _transcodeTask ?? Task.CompletedTask;

    public async Task StartAsync(nint outputWindowHandle, IRandomAccessStream outputStream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transcodeTask is not null)
        {
            throw new InvalidOperationException("Perekam sudah digunakan.");
        }

        _outputStream = outputStream;
        // A save picker can return an existing file after the user confirms
        // replacement. Truncate it first so an older, larger MP4 cannot leave
        // stale bytes after the newly finalized movie.
        _outputStream.Size = 0;
        _outputStream.Seek(0);
        _captureItem = Capture.GraphicsCaptureItemFactory.CreateForWindow(outputWindowHandle);
        _captureItem.Closed += CaptureItem_Closed;
        var sourceSize = _captureItem.Size;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            throw new InvalidOperationException("Output klien belum memiliki ukuran yang valid.");
        }

        var inputProperties = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8,
            (uint)sourceSize.Width,
            (uint)sourceSize.Height);
        inputProperties.FrameRate.Numerator = OutputFrameRate;
        inputProperties.FrameRate.Denominator = 1;
        inputProperties.PixelAspectRatio.Numerator = 1;
        inputProperties.PixelAspectRatio.Denominator = 1;

        var descriptor = new VideoStreamDescriptor(inputProperties);
        _mediaSource = new MediaStreamSource(descriptor)
        {
            BufferTime = TimeSpan.Zero
        };
        _mediaSource.Starting += MediaSource_Starting;
        _mediaSource.SampleRequested += MediaSource_SampleRequested;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            8,
            sourceSize);
        _framePool.FrameArrived += FramePool_FrameArrived;
        _captureSession = _framePool.CreateCaptureSession(_captureItem);
        // The source capture has already drawn the presenter cursor into the
        // output. Do not add a second cursor from the client monitor itself.
        _captureSession.IsCursorCaptureEnabled = false;
        _captureSession.StartCapture();

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = OutputWidth;
        profile.Video.Height = OutputHeight;
        profile.Video.Bitrate = OutputBitrate;
        profile.Video.FrameRate.Numerator = OutputFrameRate;
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
            StopCapture();
            throw new InvalidOperationException($"Encoder 1080p60 tidak tersedia ({preparation.FailureReason}).");
        }

        _transcodeTask = preparation.TranscodeAsync().AsTask();
    }

    public async Task StopAsync()
    {
        await _stopGate.WaitAsync();
        try
        {
            if (_stopCompleted) return;

            lock (_frameGate)
            {
                _stopping = true;
                _pendingFrame?.Dispose();
                _pendingFrame = null;
                Monitor.PulseAll(_frameGate);
            }

            StopCapture();

            try
            {
                if (_transcodeTask is not null)
                {
                    await _transcodeTask;
                }

                if (_outputStream is not null)
                {
                    await _outputStream.FlushAsync();
                }
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

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? newestFrame = null;
        try
        {
            while (true)
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null) break;
                newestFrame?.Dispose();
                newestFrame = frame;
            }

            if (newestFrame is null) return;

            lock (_frameGate)
            {
                if (_stopping)
                {
                    newestFrame.Dispose();
                    return;
                }

                _pendingFrame?.Dispose();
                _pendingFrame = newestFrame;
                newestFrame = null;
                Monitor.Pulse(_frameGate);
            }
        }
        finally
        {
            newestFrame?.Dispose();
        }
    }

    private void MediaSource_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        args.Request.SetActualStartPosition(TimeSpan.Zero);
    }

    private void MediaSource_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        Direct3D11CaptureFrame? frame;
        TimeSpan timestamp;

        while (true)
        {
            frame = TakeNextFrame();
            if (frame is null)
            {
                args.Request.Sample = null;
                return;
            }

            var sourceTime = frame.SystemRelativeTime;
            _firstFrameTime ??= sourceTime;
            var elapsedTicks = Math.Max(0, (sourceTime - _firstFrameTime.Value).Ticks);
            var frameSlot = elapsedTicks / FrameDuration.Ticks;

            // Windows Graphics Capture follows the monitor refresh rate. On a
            // 120/144/165 Hz display it can therefore deliver far more frames
            // than the requested recording profile. Keep only one frame per
            // 1/60-second slot so the MP4 is genuinely 60 fps.
            if (frameSlot <= _lastFrameSlot)
            {
                frame.Dispose();
                continue;
            }

            _lastFrameSlot = frameSlot;
            timestamp = TimeSpan.FromTicks(frameSlot * FrameDuration.Ticks);
            break;
        }

        try
        {
            var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timestamp);
            sample.Duration = FrameDuration;
            _outstandingFrames.TryAdd(frame, 0);
            sample.Processed += (_, _) => ReleaseFrame(frame);
            args.Request.Sample = sample;
        }
        catch
        {
            frame.Dispose();
            args.Request.Sample = null;
        }
    }

    private Direct3D11CaptureFrame? TakeNextFrame()
    {
        lock (_frameGate)
        {
            while (!_stopping && _pendingFrame is null)
            {
                Monitor.Wait(_frameGate);
            }

            if (_stopping) return null;
            var frame = _pendingFrame;
            _pendingFrame = null;
            return frame;
        }
    }

    private void ReleaseFrame(Direct3D11CaptureFrame frame)
    {
        if (_outstandingFrames.TryRemove(frame, out _)) frame.Dispose();
    }

    private void CaptureItem_Closed(GraphicsCaptureItem sender, object args)
    {
        lock (_frameGate)
        {
            _stopping = true;
            _pendingFrame?.Dispose();
            _pendingFrame = null;
            Monitor.PulseAll(_frameGate);
        }
    }

    private void StopCapture()
    {
        if (_framePool is not null) _framePool.FrameArrived -= FramePool_FrameArrived;
        if (_captureItem is not null) _captureItem.Closed -= CaptureItem_Closed;
        _captureSession?.Dispose();
        _captureSession = null;
        _framePool?.Dispose();
        _framePool = null;
        _captureItem = null;
    }

    private void DisposeMediaObjects()
    {
        if (_mediaSource is not null)
        {
            _mediaSource.Starting -= MediaSource_Starting;
            _mediaSource.SampleRequested -= MediaSource_SampleRequested;
        }
        _mediaSource = null;

        foreach (var frame in _outstandingFrames.Keys) ReleaseFrame(frame);
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
        }
    }
}
