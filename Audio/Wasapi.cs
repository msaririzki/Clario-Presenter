using System.Runtime.InteropServices;

namespace Clario_Presenter.Audio;

internal sealed record AudioDevice(string Id, string Name, bool Available = true);
internal sealed record AudioDevices(IReadOnlyList<AudioDevice> Inputs, IReadOnlyList<AudioDevice> Outputs);

internal static class Wasapi
{
    private const uint Active = 1;
    private const uint Loopback = 0x00020000;
    private const uint ConvertPcm = 0x80000000;
    private const uint ResampleHighQuality = 0x08000000;
    internal const uint Silent = 2;
    internal const uint TimestampError = 4;

    internal static AudioDevices Enumerate()
    {
        var enumerator = CreateEnumerator();
        try { return new(GetDevices(enumerator, Direction.Input), GetDevices(enumerator, Direction.Output)); }
        finally { Release(enumerator); }
    }

    private static IReadOnlyList<AudioDevice> GetDevices(IDeviceEnumerator enumerator, Direction direction)
    {
        Check(enumerator.EnumAudioEndpoints(direction, Active, out var collection));
        try
        {
            Check(collection.GetCount(out var count));
            var result = new List<AudioDevice>();
            for (uint index = 0; index < count; index++)
            {
                Check(collection.Item(index, out var device));
                try
                {
                    Check(device.GetId(out var id));
                    result.Add(new(id, FriendlyName(device) ?? id));
                }
                finally { Release(device); }
            }
            return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { Release(collection); }
    }

    private static string? FriendlyName(IDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) < 0) return null;
        try
        {
            var key = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
            if (store.GetValue(ref key, out var value) < 0) return null;
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Release(store); }
    }

    internal static void Capture(string deviceId, bool loopback, Action started,
        Action<nint, int, uint, long, int> packet, CancellationToken token)
    {
        var enumerator = CreateEnumerator();
        IDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? reader = null;
        var running = false;
        try
        {
            Check(string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(loopback ? Direction.Output : Direction.Input, 1, out device)
                : enumerator.GetDevice(deviceId, out device));
            Check(device.GetState(out var state));
            if (state != Active) throw new InvalidOperationException("Perangkat audio sudah tidak aktif.");

            var clientId = typeof(IAudioClient).GUID;
            Check(device.Activate(ref clientId, 23, 0, out var activated));
            client = (IAudioClient)activated;
            var flags = ConvertPcm | ResampleHighQuality | (loopback ? Loopback : 0);
            var channels = 2;
            var format = new WaveFormat(channels);
            var initialize = client.Initialize(0, flags, 1_000_000, 0, ref format, 0);
            if (!loopback && initialize == unchecked((int)0x88890008))
            {
                // Some input drivers accept a mono shared-mode client only.
                Release(client);
                client = null;
                Check(device.Activate(ref clientId, 23, 0, out activated));
                client = (IAudioClient)activated;
                channels = 1;
                format = new WaveFormat(channels);
                initialize = client.Initialize(0, flags, 1_000_000, 0, ref format, 0);
            }
            Check(initialize);
            var readerId = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref readerId, out var service));
            reader = (IAudioCaptureClient)service;
            token.ThrowIfCancellationRequested();
            Check(client.Start());
            running = true;
            started();

            do
            {
                Check(reader.GetNextPacketSize(out var framesReady));
                while (framesReady > 0 && !token.IsCancellationRequested)
                {
                    Check(reader.GetBuffer(out var data, out var frames, out var packetFlags, out _, out var timestamp));
                    try
                    {
                        if (frames > 0) packet(data, checked((int)frames), packetFlags, checked((long)timestamp), channels);
                    }
                    finally { Check(reader.ReleaseBuffer(frames)); }
                    Check(reader.GetNextPacketSize(out framesReady));
                }
            } while (!token.WaitHandle.WaitOne(8));
        }
        finally
        {
            if (running) client!.Stop();
            Release(reader);
            Release(client);
            Release(device);
            Release(enumerator);
        }
    }

    private static IDeviceEnumerator CreateEnumerator() => (IDeviceEnumerator)(object)new DeviceEnumerator();
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(object? instance)
    {
        if (instance is not null) Marshal.ReleaseComObject(instance);
    }

    private enum Direction { Output, Input }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat(int channels)
    {
        public ushort Tag = 1; // WAVE_FORMAT_PCM
        public ushort Channels = (ushort)channels;
        public uint SamplesPerSecond = RecordingAudioFormat.SampleRate;
        public uint AverageBytesPerSecond = (uint)(RecordingAudioFormat.SampleRate * channels * sizeof(short));
        public ushort BlockAlign = (ushort)(channels * sizeof(short));
        public ushort BitsPerSample = 16;
        public ushort ExtraSize = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct PropertyKey(Guid Format, uint PropertyId);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(Direction direction, uint state, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(Direction direction, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long standard, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong position, out ulong timestamp);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
