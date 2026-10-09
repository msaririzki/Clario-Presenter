using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Clario_Presenter.Services;
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
    private CanvasRenderTarget? _taskbarFrontBuffer;
    private CanvasRenderTarget? _taskbarBackBuffer;
    private DesktopCompositionLayout? _desktopLayout;
    private PublicWindowLayer[] _publicLayers = [];
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
        set
        {
            _isFrozen = value;
            lock (_frameLock)
            {
                foreach (var layer in _publicLayers) layer.Capture.IsFrozen = value;
            }
        }
    }

    public void ConfigureDesktop(DisplayBounds bounds, DisplayBounds workArea) =>
        _desktopLayout = new DesktopCompositionLayout(bounds, workArea);

    public bool SetProtectedWindows(IReadOnlyList<nint> windowHandles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_desktopLayout is null) throw new InvalidOperationException("Layout desktop belum disiapkan.");

        PublicWindowLayer[] previousLayers;
        lock (_frameLock) previousLayers = _publicLayers;
        var nextLayers = new List<PublicWindowLayer>();
        foreach (var handle in windowHandles)
        {
            if (!WindowCatalogService.TryGetCaptureBounds(handle, out var bounds)) continue;
            var layer = previousLayers.FirstOrDefault(item => item.Handle == handle);
            if (layer is null)
            {
                layer = new PublicWindowLayer(handle, bounds);
                layer.Capture.FrameAvailable += PublicLayer_FrameAvailable;
                try
                {
                    layer.Capture.Start(handle);
                    layer.Capture.IsFrozen = _isFrozen;
                }
                catch
                {
                    layer.Capture.FrameAvailable -= PublicLayer_FrameAvailable;
                    layer.Capture.Dispose();
                    continue;
                }
            }
            lock (_frameLock) layer.Bounds = bounds;
            nextLayers.Add(layer);
        }

        lock (_frameLock) _publicLayers = nextLayers.ToArray();
        foreach (var layer in previousLayers.Except(nextLayers))
        {
            layer.Capture.FrameAvailable -= PublicLayer_FrameAvailable;
            layer.Capture.Dispose();
        }
        return nextLayers.Any(layer => !layer.Faulted);
    }

    public void ClearProtectedWindows()
    {
        PublicWindowLayer[] layers;
        lock (_frameLock)
        {
            layers = _publicLayers;
            _publicLayers = [];
        }
        foreach (var layer in layers)
        {
            layer.Capture.FrameAvailable -= PublicLayer_FrameAvailable;
            layer.Capture.Dispose();
        }
    }

    private void PublicLayer_FrameAvailable(object? sender, EventArgs args) =>
        FrameAvailable?.Invoke(this, EventArgs.Empty);

    public void Start(nint windowHandle, bool keepLastFrame = false) =>
        StartItem(GraphicsCaptureItemFactory.CreateForWindow(windowHandle), keepLastFrame);

    public void StartMonitor(nint monitorHandle, bool keepLastFrame = false) =>
        StartItem(GraphicsCaptureItemFactory.CreateForMonitor(monitorHandle), keepLastFrame);

    private void StartItem(GraphicsCaptureItem captureItem, bool keepLastFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCaptureObjects(keepLastFrame);

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
        CaptureScaleMode scaleMode = CaptureScaleMode.Fit, float adaptiveZoom = 1.15f)
        => DrawCore(args.DrawingSession, (float)sender.ActualWidth, (float)sender.ActualHeight,
            scaleMode, adaptiveZoom, CanvasImageInterpolation.HighQualityCubic);

    public void Draw(CanvasDrawingSession drawingSession, float width, float height,
        CaptureScaleMode scaleMode = CaptureScaleMode.Fit, float adaptiveZoom = 1.15f)
        => DrawCore(drawingSession, width, height, scaleMode, adaptiveZoom,
            CanvasImageInterpolation.HighQualityCubic);

    public void DrawRecordingFrame(CanvasDrawingSession drawingSession, float width, float height,
        CaptureScaleMode scaleMode = CaptureScaleMode.Fit, float adaptiveZoom = 1.15f)
        => DrawCore(drawingSession, width, height, scaleMode, adaptiveZoom,
            CanvasImageInterpolation.Linear);

    private void DrawCore(CanvasDrawingSession drawingSession, float width, float height,
        CaptureScaleMode scaleMode, float adaptiveZoom, CanvasImageInterpolation interpolation)
    {
        if (width <= 0 || height <= 0) return;
        if (_desktopLayout is not null) scaleMode = CaptureScaleMode.Fit;

        drawingSession.Clear(Windows.UI.Color.FromArgb(255, 14, 17, 22));
        lock (_frameLock)
        {
            var bitmap = _frontBuffer;
            if (bitmap is null) return;

            var sourceWidth = bitmap.SizeInPixels.Width;
            var sourceHeight = bitmap.SizeInPixels.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0) return;

            var fitScale = Math.Min(width / sourceWidth, height / sourceHeight);
            var scale = scaleMode switch
            {
                CaptureScaleMode.Fill => Math.Max(width / sourceWidth, height / sourceHeight),
                CaptureScaleMode.Adaptive => fitScale * Math.Clamp(adaptiveZoom, 1f, 1.35f),
                _ => fitScale
            };
            var drawWidth = sourceWidth * scale;
            var drawHeight = sourceHeight * scale;
            var destination = new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
            var source = new Rect(0, 0, sourceWidth, sourceHeight);
            drawingSession.DrawImage(bitmap, destination, source, 1, interpolation);

            if (_desktopLayout is not null)
            {
                foreach (var layer in _publicLayers)
                {
                    if (!layer.Faulted)
                        layer.Capture.DrawPlacedWindow(drawingSession, layer.Bounds, _desktopLayout,
                            destination, interpolation);
                }

                // Keep the last safe taskbar above every public-window layer,
                // including applications that cover it in fullscreen mode.
                var taskbar = _desktopLayout.TaskbarBounds;
                if (_taskbarFrontBuffer is not null)
                {
                    var taskbarDestination = ToOutputRect(taskbar, _desktopLayout, destination);
                    drawingSession.DrawImage(_taskbarFrontBuffer, taskbarDestination,
                        new Rect(0, 0, taskbar.Width, taskbar.Height), 1, interpolation);
                }
            }
        }
    }

    private void DrawPlacedWindow(CanvasDrawingSession drawingSession, DisplayBounds window,
        DesktopCompositionLayout layout, Rect desktopDestination, CanvasImageInterpolation interpolation)
    {
        var visible = layout.ClipPublicWindow(window);
        if (visible.Width <= 0 || visible.Height <= 0) return;
        lock (_frameLock)
        {
            if (_frontBuffer is not { } bitmap) return;
            var ratioX = bitmap.SizeInPixels.Width / (double)window.Width;
            var ratioY = bitmap.SizeInPixels.Height / (double)window.Height;
            var source = new Rect((visible.Left - window.Left) * ratioX,
                (visible.Top - window.Top) * ratioY, visible.Width * ratioX, visible.Height * ratioY);
            drawingSession.DrawImage(bitmap, ToOutputRect(visible, layout, desktopDestination),
                source, 1, interpolation);
        }
    }

    private static Rect ToOutputRect(DisplayBounds region, DesktopCompositionLayout layout, Rect destination)
    {
        var mapped = layout.MapRegion(region, destination.X, destination.Y, destination.Width, destination.Height);
        return new Rect(mapped.X, mapped.Y, mapped.Width, mapped.Height);
    }

    public void Stop()
    {
        ClearProtectedWindows();
        StopCaptureObjects(keepLastFrame: false);
    }

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
                            drawingSession.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                            drawingSession.DrawImage(capturedSurface);
                        }

                        UpdateSafeTaskbar(capturedSurface);

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

    private void UpdateSafeTaskbar(CanvasBitmap capturedSurface)
    {
        if (_desktopLayout is null) return;
        var taskbar = _desktopLayout.TaskbarBounds;
        if (taskbar.Width <= 0 || taskbar.Height <= 0
            || WindowCatalogService.IsRegionCoveredByForeground(taskbar)) return;

        var writeTarget = _taskbarBackBuffer
            ?? new CanvasRenderTarget(_canvasDevice, taskbar.Width, taskbar.Height, 96);
        var unusedTarget = _taskbarFrontBuffer
            ?? new CanvasRenderTarget(_canvasDevice, taskbar.Width, taskbar.Height, 96);
        using (var session = writeTarget.CreateDrawingSession())
        {
            session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            session.DrawImage(capturedSurface, new Rect(0, 0, taskbar.Width, taskbar.Height),
                new Rect(taskbar.Left - _desktopLayout.Bounds.Left, taskbar.Top - _desktopLayout.Bounds.Top,
                    taskbar.Width, taskbar.Height));
        }
        lock (_frameLock)
        {
            _taskbarFrontBuffer = writeTarget;
            _taskbarBackBuffer = unusedTarget;
        }
    }

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
                CanvasRenderTarget? oldFront, oldBack, oldTaskbarFront, oldTaskbarBack;
                lock (_frameLock)
                {
                    oldFront = _frontBuffer;
                    oldBack = _backBuffer;
                    oldTaskbarFront = _taskbarFrontBuffer;
                    oldTaskbarBack = _taskbarBackBuffer;
                    _frontBuffer = null;
                    _backBuffer = null;
                    _taskbarFrontBuffer = null;
                    _taskbarBackBuffer = null;
                    _bufferSize = default;
                }
                // Releasing a Win2D resource may acquire its device lock. Never
                // do that while holding a frame lock needed by the recorder.
                oldFront?.Dispose();
                oldBack?.Dispose();
                oldTaskbarFront?.Dispose();
                oldTaskbarBack?.Dispose();
                _lastSize = default;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        ClearProtectedWindows();
        StopCaptureObjects(keepLastFrame: false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private sealed class PublicWindowLayer
    {
        public nint Handle { get; }
        public DisplayBounds Bounds { get; set; }
        public CaptureSessionService Capture { get; } = new();
        public bool Faulted { get; private set; }

        public PublicWindowLayer(nint handle, DisplayBounds bounds)
        {
            Handle = handle;
            Bounds = bounds;
            Capture.SourceClosed += (_, _) => Faulted = true;
            Capture.CaptureFailed += (_, _) =>
            {
                Faulted = true;
                Capture.IsFrozen = true;
            };
        }
    }
}

public enum CaptureScaleMode
{
    Adaptive,
    Fit,
    Fill
}

public sealed class CaptureFailureEventArgs(string message, Exception exception) : EventArgs
{
    public string Message { get; } = message;
    public Exception Exception { get; } = exception;
}
