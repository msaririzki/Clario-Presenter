using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Clario_Presenter.Capture;

public sealed class CaptureSessionService : IDisposable
{
    private readonly CanvasDevice _canvasDevice = CanvasDevice.GetSharedDevice();
    private readonly object _captureLock = new();
    private readonly object _frameLock = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private CanvasRenderTarget? _frontBuffer;
    private CanvasRenderTarget? _backBuffer;
    private SizeInt32 _bufferSize;
    private SizeInt32 _lastSize;
    private volatile bool _isFrozen;
    private bool _disposed;

    public event EventHandler? FrameAvailable;
    public event EventHandler? SourceClosed;
    public event EventHandler<CaptureFailureEventArgs>? CaptureFailed;
    public Func<bool>? FrameHoldPredicate { get; set; }

    public bool HasFrame
    {
        get
        {
            lock (_frameLock) return _frontBuffer is not null;
        }
    }
    public SizeInt32 SourceSize
    {
        get
        {
            lock (_captureLock) return _lastSize;
        }
    }

    public bool IsFrozen
    {
        get => _isFrozen;
        set => _isFrozen = value;
    }

    public void Start(nint windowHandle) => StartItem(GraphicsCaptureItemFactory.CreateForWindow(windowHandle));

    public void StartMonitor(nint monitorHandle) => StartItem(GraphicsCaptureItemFactory.CreateForMonitor(monitorHandle));

    private void StartItem(GraphicsCaptureItem captureItem)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCaptureObjects(keepLastFrame: false);

        _item = captureItem;
        if (_item.Size.Width <= 0 || _item.Size.Height <= 0)
        {
            throw new InvalidOperationException("Jendela sumber sedang diminimalkan atau tidak memiliki ukuran yang valid.");
        }

        _lastSize = _item.Size;
        _item.Closed += Item_Closed;
        // Create on the UI dispatcher so Win2D capture copies and CanvasControl
        // rendering stay serialized on the same device thread.
        _framePool = Direct3D11CaptureFramePool.Create(
            _canvasDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _lastSize);
        _framePool.FrameArrived += FramePool_FrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        _session.StartCapture();
    }

    public void Draw(CanvasControl sender, CanvasDrawEventArgs args,
        CaptureScaleMode scaleMode = CaptureScaleMode.Fit)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width <= 0 || height <= 0) return;

        args.DrawingSession.Clear(Windows.UI.Color.FromArgb(255, 14, 17, 22));
        lock (_frameLock)
        {
            var bitmap = _frontBuffer;
            if (bitmap is null) return;

            var sourceWidth = bitmap.SizeInPixels.Width;
            var sourceHeight = bitmap.SizeInPixels.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0) return;

            var scale = scaleMode == CaptureScaleMode.Fill
                ? Math.Max(width / sourceWidth, height / sourceHeight)
                : Math.Min(width / sourceWidth, height / sourceHeight);
            var drawWidth = sourceWidth * scale;
            var drawHeight = sourceHeight * scale;
            var destination = new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
            var source = new Rect(0, 0, sourceWidth, sourceHeight);
            args.DrawingSession.DrawImage(bitmap, destination, source, 1,
                CanvasImageInterpolation.HighQualityCubic);
        }
    }

    public void Stop() => StopCaptureObjects(keepLastFrame: false);

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frameReady = false;

        try
        {
            lock (_captureLock)
            {
                if (_framePool is null || !ReferenceEquals(sender, _framePool)) return;

                var sizeChanged = false;
                SizeInt32 nextSize;

                Direct3D11CaptureFrame? newestFrame = null;
                while (true)
                {
                    var queuedFrame = sender.TryGetNextFrame();
                    if (queuedFrame is null) break;
                    newestFrame?.Dispose();
                    newestFrame = queuedFrame;
                }

                using (var frame = newestFrame)
                {
                    if (frame is null) return;
                    nextSize = frame.ContentSize;
                    if (nextSize.Width <= 0 || nextSize.Height <= 0) return;

                    sizeChanged = nextSize.Width != _lastSize.Width || nextSize.Height != _lastSize.Height;
                    if (!_isFrozen && !(FrameHoldPredicate?.Invoke() ?? false))
                    {
                        var writeTarget = GetWriteTarget(nextSize);
                        using var capturedSurface = CanvasBitmap.CreateFromDirect3D11Surface(_canvasDevice, frame.Surface);
                        using (var drawingSession = writeTarget.CreateDrawingSession())
                        {
                            drawingSession.DrawImage(capturedSurface);
                        }

                        lock (_frameLock)
                        {
                            (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
                        }

                        frameReady = true;
                    }
                }

                if (sizeChanged && _framePool is not null)
                {
                    _lastSize = nextSize;
                    _framePool.Recreate(
                        _canvasDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        _lastSize);
                }
            }

            if (frameReady) FrameAvailable?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, new CaptureFailureEventArgs("Frame capture berhenti.", exception));
        }
    }

    private void Item_Closed(GraphicsCaptureItem sender, object args) => SourceClosed?.Invoke(this, EventArgs.Empty);

    private CanvasRenderTarget GetWriteTarget(SizeInt32 size)
    {
        lock (_frameLock)
        {
            if (_frontBuffer is not null
                && _backBuffer is not null
                && _bufferSize.Width == size.Width
                && _bufferSize.Height == size.Height)
            {
                return _backBuffer;
            }
        }

        // Resource creation can wait on Win2D's device lock. Keep it outside
        // _frameLock so the XAML draw callback never participates in a lock inversion.
        var newFront = new CanvasRenderTarget(_canvasDevice, size.Width, size.Height, 96);
        var newBack = new CanvasRenderTarget(_canvasDevice, size.Width, size.Height, 96);
        CanvasRenderTarget? oldFront;
        CanvasRenderTarget? oldBack;

        lock (_frameLock)
        {
            oldFront = _frontBuffer;
            oldBack = _backBuffer;
            _frontBuffer = newFront;
            _backBuffer = newBack;
            _bufferSize = size;
        }

        oldFront?.Dispose();
        oldBack?.Dispose();
        return newBack;
    }

    private void StopCaptureObjects(bool keepLastFrame)
    {
        lock (_captureLock)
        {
            if (_item is not null) _item.Closed -= Item_Closed;
            if (_framePool is not null) _framePool.FrameArrived -= FramePool_FrameArrived;

            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
            _item = null;
            _isFrozen = false;

            if (!keepLastFrame)
            {
                lock (_frameLock)
                {
                    _frontBuffer?.Dispose();
                    _backBuffer?.Dispose();
                    _frontBuffer = null;
                    _backBuffer = null;
                    _bufferSize = default;
                }
                _lastSize = default;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopCaptureObjects(keepLastFrame: false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

public enum CaptureScaleMode
{
    Fit,
    Fill
}

public sealed class CaptureFailureEventArgs(string message, Exception exception) : EventArgs
{
    public string Message { get; } = message;
    public Exception Exception { get; } = exception;
}
