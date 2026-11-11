using Microsoft.Graphics.Canvas;
using System.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Clario_Presenter.Camera;

public sealed record CameraDevice(string Id, string Name);

/// <summary>Reads the newest camera frame, retaining one independent bitmap for preview and recording.</summary>
public sealed class CameraCaptureService : IAsyncDisposable
{
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly object _frameGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private CanvasBitmap? _bitmap;
    private long _lastFrameTick;
    private int _processing;
    private volatile bool _stopping;
    private bool _disposed;
    private string? _failureMessage;

    public event EventHandler? FrameAvailable;
    public string DeviceName { get; private set; } = string.Empty;
    public string? Warning => Volatile.Read(ref _failureMessage)
        ?? (Volatile.Read(ref _lastFrameTick) > 0
            && Environment.TickCount64 - Volatile.Read(ref _lastFrameTick) > 3000
                ? "Kamera tidak mengirim gambar" : null);

    public static async Task<IReadOnlyList<CameraDevice>> GetDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Select(device => new CameraDevice(device.Id, device.Name)).ToArray();
    }

    public async Task StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null) throw new InvalidOperationException("Kamera sudah aktif.");
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new InvalidOperationException("Pilih kamera di Pengaturan sebelum merekam.");
            var devices = await GetDevicesAsync();
            var selected = devices.FirstOrDefault(device => device.Id == deviceId)
                ?? throw new InvalidOperationException("Kamera terpilih belum terhubung. Hubungkan kamera, lalu klik Perbarui daftar di Pengaturan.");
            DeviceName = selected.Name;
            cancellationToken.ThrowIfCancellationRequested();

            // SharedReadOnly also works with cameras exposed by another app.
            // CPU frames guarantee an explicit, independently owned SoftwareBitmap.
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = selected.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            });
            cancellationToken.ThrowIfCancellationRequested();
            var source = _capture.FrameSources.Values
                .Where(item => item.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(item => item.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 1)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Gambar dari kamera ini belum dapat digunakan. Pilih kamera lain di Pengaturan.");

            // The camera overlay needs at most 672 pixels across in a 1080p video.
            // Resize during acquisition instead of uploading a full 4K frame.
            var format = source.CurrentFormat.VideoFormat;
            var sourceWidth = Math.Max(1u, format.Width);
            var sourceHeight = Math.Max(1u, format.Height);
            var ratio = Math.Min(1d, Math.Min(960d / sourceWidth, 720d / sourceHeight));
            var outputSize = new BitmapSize
            {
                Width = Math.Max(2u, (uint)(sourceWidth * ratio) / 2 * 2),
                Height = Math.Max(2u, (uint)(sourceHeight * ratio) / 2 * 2)
            };
            _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, outputSize);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += Reader_FrameArrived;
            var result = await _reader.StartAsync();
            if (result != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"Kamera gagal mengirim gambar ({result}).");
            await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("Akses kamera belum diizinkan Windows. Izinkan Clario memakai kamera lalu coba lagi.", exception);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("Kamera belum mengirim gambar. Periksa sambungan webcam atau buka aplikasi kamera HP, lalu coba lagi.", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void Reader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_stopping || Interlocked.Exchange(ref _processing, 1) != 0) return;
        CanvasBitmap? next = null;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            // Both the frame reference and the bitmap must be explicitly released.
            using var softwareBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (softwareBitmap is null) return;
            using var converted = softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                || softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight
                ? SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
                : null;
            next = CanvasBitmap.CreateFromSoftwareBitmap(_device, converted ?? softwareBitmap);
            CanvasBitmap? previous;
            lock (_frameGate)
            {
                if (_stopping) return;
                previous = _bitmap;
                _bitmap = next;
                next = null;
                Volatile.Write(ref _lastFrameTick, Environment.TickCount64);
            }
            previous?.Dispose();
            _firstFrame.TrySetResult();
            FrameAvailable?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            SetFailure($"Kamera terputus: {exception.Message}");
        }
        finally
        {
            next?.Dispose();
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    private void SetFailure(string message)
    {
        Volatile.Write(ref _failureMessage, message);
        _firstFrame.TrySetException(new InvalidOperationException(message));
    }

    public bool Draw(CanvasDrawingSession drawingSession, CameraRectangle destination, bool mirror)
    {
        lock (_frameGate)
        {
            if (_bitmap is null || Warning is not null) return false;
            var bounds = CameraOverlayLayout.Fit(_bitmap.SizeInPixels.Width, _bitmap.SizeInPixels.Height, destination);
            var oldTransform = drawingSession.Transform;
            try
            {
                if (mirror)
                    drawingSession.Transform = System.Numerics.Matrix3x2.CreateScale(-1, 1)
                        * System.Numerics.Matrix3x2.CreateTranslation((float)(destination.X * 2 + destination.Width), 0)
                        * oldTransform;
                drawingSession.DrawImage(_bitmap,
                    new Windows.Foundation.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    new Windows.Foundation.Rect(0, 0, _bitmap.SizeInPixels.Width, _bitmap.SizeInPixels.Height),
                    1, CanvasImageInterpolation.Linear);
                return true;
            }
            finally
            {
                drawingSession.Transform = oldTransform;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed) return;
            _stopping = true;
            if (_reader is not null)
            {
                _reader.FrameArrived -= Reader_FrameArrived;
                try { await _reader.StopAsync(); }
                catch (Exception exception) { Debug.WriteLine($"Camera stop: {exception.Message}"); }
                _reader.Dispose();
                _reader = null;
            }
            if (_capture is not null)
            {
                _capture.Dispose();
                _capture = null;
            }
            CanvasBitmap? previous;
            lock (_frameGate)
            {
                previous = _bitmap;
                _bitmap = null;
            }
            previous?.Dispose();
            _disposed = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
}
