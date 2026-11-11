using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Clario_Presenter.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Clario_Presenter.Capture;

public sealed class CaptureSessionService : IDisposable, IRecordingFrameSource
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
    private PublicWindowLayer[] _requestedLayers = [];
    private readonly Dictionary<nint, PublicWindowLayer> _layerCache = [];
    private SizeInt32 _frontBufferSize;
    private SizeInt32 _backBufferSize;
    private SizeInt32 _lastSize;
    private volatile bool _isFrozen;
    private volatile bool _desktopProtected;
    public bool IsDesktopProtected { get => _desktopProtected; set => _desktopProtected = value; }
    private bool _disposed;
    public int PublicCaptureStartCount { get; private set; }
    public int PreparedWindowCount => _layerCache.Count;

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
                foreach (var layer in _layerCache.Values) layer.Capture.IsFrozen = value;
            }
        }
    }

    public void ConfigureDesktop(DisplayBounds bounds, DisplayBounds workArea)
    {
        _desktopLayout = new DesktopCompositionLayout(bounds, workArea);
        var safeBackground = new CanvasRenderTarget(_canvasDevice, bounds.Width, bounds.Height, 96);
        using (var session = safeBackground.CreateDrawingSession())
            session.Clear(Windows.UI.Color.FromArgb(255, 14, 17, 22));
        CanvasRenderTarget? previous;
        lock (_frameLock)
        {
            previous = _frontBuffer;
            _frontBuffer = safeBackground;
            _frontBufferSize = new SizeInt32(bounds.Width, bounds.Height);
        }
        previous?.Dispose();
    }

    public bool SetProtectedWindows(IReadOnlyList<nint> windowHandles, bool retainSceneIfEmpty = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_desktopLayout is null) throw new InvalidOperationException("Layar belum siap. Pilih layar dan mulai kembali sesi.");

        var nextLayers = new List<PublicWindowLayer>();
        foreach (var handle in windowHandles)
        {
            if (!WindowCatalogService.TryGetCaptureBounds(handle, out var bounds)) continue;
            _layerCache.TryGetValue(handle, out var layer);
            if (layer is { Faulted: true })
            {
                _layerCache.Remove(handle);
                DisposeLayer(layer);
                layer = null;
            }
            if (layer is null)
            {
                layer = new PublicWindowLayer(handle, bounds);
                layer.Capture.FrameAvailable += PublicLayer_FrameAvailable;
                try
                {
                    layer.Capture.Start(handle, captureCursor: false);
                    layer.Capture.IsFrozen = _isFrozen;
                    PublicCaptureStartCount++;
                    _layerCache[handle] = layer;
                }
                catch
                {
                    layer.Capture.FrameAvailable -= PublicLayer_FrameAvailable;
                    layer.Capture.Dispose();
                    continue;
                }
            }
            lock (_frameLock) layer.Bounds = bounds;
            layer.LastRequestedTick = Environment.TickCount64;
            nextLayers.Add(layer);
        }

        lock (_frameLock)
        {
            _requestedLayers = nextLayers.ToArray();
            if (_requestedLayers.Length == 0 && !retainSceneIfEmpty) _publicLayers = [];
            PublishReadyLayers();
        }
        // Keep recently used app captures warm through Alt+Tab/private detours.
        // Release old inactive captures to bound GPU memory usage.
        PublicWindowLayer[] active;
        lock (_frameLock) active = _publicLayers;
        var removable = _layerCache.Values.Where(layer => !nextLayers.Contains(layer)
            && !active.Contains(layer)).OrderBy(layer => layer.LastRequestedTick).ToArray();
        foreach (var layer in removable)
        {
            if (_layerCache.Count <= 12 && Environment.TickCount64 - layer.LastRequestedTick < 3000) continue;
            _layerCache.Remove(layer.Handle);
            DisposeLayer(layer);
        }
        return active.Any(layer => !layer.Faulted && layer.Capture.HasFrame);
    }

    private void PublishReadyLayers()
    {
        // Do not replace the current scene by an empty/partially initialized
        // capture. Publish the next scene after every new window has a real frame.
        if (!_isFrozen && _requestedLayers.Length > 0
            && _requestedLayers.All(layer => layer.Faulted || layer.Capture.HasFrame))
            _publicLayers = _requestedLayers.Where(layer => !layer.Faulted).ToArray();
    }

    private void DisposeLayer(PublicWindowLayer layer)
    {
        layer.Capture.FrameAvailable -= PublicLayer_FrameAvailable;
        layer.Capture.Dispose();
    }

    public void ClearProtectedWindows()
    {
        PublicWindowLayer[] layers;
        lock (_frameLock)
        {
            layers = _layerCache.Values.ToArray();
            _publicLayers = [];
            _requestedLayers = [];
            _layerCache.Clear();
        }
        foreach (var layer in layers)
        {
            DisposeLayer(layer);
        }
    }

    private void PublicLayer_FrameAvailable(object? sender, EventArgs args)
    {
        lock (_frameLock) PublishReadyLayers();
        FrameAvailable?.Invoke(this, EventArgs.Empty);
    }

    public void Start(nint windowHandle, bool keepLastFrame = false, bool captureCursor = true) =>
        StartItem(GraphicsCaptureItemFactory.CreateForWindow(windowHandle), keepLastFrame, captureCursor);

    public void StartMonitor(nint monitorHandle, bool keepLastFrame = false) =>
        StartItem(GraphicsCaptureItemFactory.CreateForMonitor(monitorHandle), keepLastFrame, captureCursor: true);

    private void StartItem(GraphicsCaptureItem captureItem, bool keepLastFrame, bool captureCursor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCaptureObjects(keepLastFrame);

        _item = captureItem;
        if (_item.Size.Width <= 0 || _item.Size.Height <= 0)
        {
            throw new InvalidOperationException("Aplikasi belum dapat ditampilkan. Buka kembali jendelanya, lalu coba lagi.");
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
        _session.IsCursorCaptureEnabled = captureCursor;
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
                foreach (var layer in IsDesktopProtected ? _publicLayers : [])
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
                    var holdFrame = FrameHoldPredicate?.Invoke() ?? false;
                    if (_desktopLayout is not null && !_isFrozen && IsDesktopProtected != holdFrame)
                    {
                        IsDesktopProtected = holdFrame;
                        frameReady = true;
                    }
                    if (!_isFrozen && !holdFrame)
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
                            (_frontBufferSize, _backBufferSize) = (_backBufferSize, _frontBufferSize);
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
            CaptureFailed?.Invoke(this, new CaptureFailureEventArgs("Gambar tidak dapat diperbarui.", exception));
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
            if (_backBuffer is not null
                && _backBufferSize.Width == size.Width
                && _backBufferSize.Height == size.Height)
            {
                return _backBuffer;
            }
        }

        // Resource creation can wait on Win2D's device lock. Keep it outside
        // _frameLock so the XAML draw callback never participates in a lock inversion.
        var newBack = new CanvasRenderTarget(_canvasDevice, size.Width, size.Height, 96);
        CanvasRenderTarget? oldBack;

        lock (_frameLock)
        {
            oldBack = _backBuffer;
            _backBuffer = newBack;
            _backBufferSize = size;
        }

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
                    _frontBufferSize = default;
                    _backBufferSize = default;
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
        public long LastRequestedTick { get; set; }

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
