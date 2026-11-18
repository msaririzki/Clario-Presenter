using System.Runtime.InteropServices;
using System.Text;

namespace Clario_Presenter.Audio;

internal enum AudioInputOrigin { Unknown, Integrated, Software }

internal sealed record AudioDevice(string Id, string Name, bool Available = true,
    AudioInputOrigin Origin = AudioInputOrigin.Unknown, string? ResolvedId = null)
{
    internal string EndpointId => ResolvedId ?? Id;
}

internal sealed record AudioDevices(IReadOnlyList<AudioDevice> Inputs, IReadOnlyList<AudioDevice> Outputs,
    AudioDevice? DefaultInput, AudioDevice? DefaultOutput, AudioDevice? AutomaticInput);

internal static class Wasapi
{
    private const uint Active = 1;
    private const uint Loopback = 0x00020000;
    private const uint ConvertPcm = 0x80000000;
    private const uint ResampleHighQuality = 0x08000000;
    private const int UnsupportedFormat = unchecked((int)0x88890008);
    private const int InvalidArgument = unchecked((int)0x80070057);
    internal const string AutomaticInputId = "clario:automatic-microphone";
    internal const uint Silent = 2;
    internal const uint Discontinuity = 1;
    internal const uint TimestampError = 4;
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly PropertyKey InstanceIdKey = new(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);
    private static readonly PropertyKey FormFactorKey = new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);
    private static readonly PropertyKey LocalContainerKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 4);

    internal static AudioDevices Enumerate()
    {
        var enumerator = CreateEnumerator();
        try
        {
            var inputs = GetDevices(enumerator, Direction.Input);
            var outputs = GetDevices(enumerator, Direction.Output);
            var defaultInput = FindDefaultDevice(enumerator, Direction.Input, inputs);
            return new AudioDevices(inputs, outputs, defaultInput, FindDefaultDevice(enumerator, Direction.Output, outputs),
                SelectAutomaticInput(inputs, defaultInput));
        }
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
                    result.Add(ReadDevice(device, direction));
                }
                finally { Release(device); }
            }
            return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { Release(collection); }
    }

    internal static AudioDevice? SelectAutomaticInput(IReadOnlyList<AudioDevice> inputs, AudioDevice? defaultInput)
    {
        if (defaultInput is { Origin: not AudioInputOrigin.Software }) return defaultInput;
        var integrated = inputs.Where(input => input.Origin == AudioInputOrigin.Integrated).Take(2).ToArray();
        return integrated.Length == 1 ? integrated[0] : defaultInput;
    }

    private static AudioDevice? FindDefaultDevice(IDeviceEnumerator enumerator, Direction direction, IReadOnlyList<AudioDevice> devices)
    {
        if (enumerator.GetDefaultAudioEndpoint(direction, DefaultRole(direction), out var device) < 0) return null;
        try
        {
            Check(device.GetId(out var id));
            return devices.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        finally { Release(device); }
    }

    private static AudioDevice ReadDevice(IDevice device, Direction direction)
    {
        Check(device.GetId(out var id));
        if (device.OpenPropertyStore(0, out var store) < 0) return new(id, id);
        try
        {
            var name = ReadString(store, FriendlyNameKey) ?? id;
            var origin = direction == Direction.Input
                ? GetInputOrigin(ReadString(store, InstanceIdKey), id)
                : AudioInputOrigin.Unknown;
            if (origin == AudioInputOrigin.Integrated && ReadUInt(store, FormFactorKey) is { } formFactor && formFactor != 4)
                origin = AudioInputOrigin.Unknown; // Integrated loopback inputs and jacks are not built-in microphones.
            return new(id, name, Origin: origin);
        }
        finally { Release(store); }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    private static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.Type == 19 ? value.Unsigned : null; }
        finally { PropVariantClear(ref value); }
    }

    private static AudioInputOrigin GetInputOrigin(string? instanceId, string endpointId)
    {
        // Some drivers omit DEVPKEY_Device_InstanceId from the Core Audio store.
        var nodeId = instanceId ?? $@"SWD\MMDEVAPI\{endpointId}";
        if (CM_Locate_DevNodeW(out var endpoint, nodeId, 0) != 0)
            return AudioInputOrigin.Unknown;

        // Windows audio endpoints are software device nodes; their parent is the adapter.
        var adapter = endpoint;
        if (nodeId.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase)
            && CM_Get_Parent(out adapter, endpoint, 0) != 0) return AudioInputOrigin.Unknown;

        var path = new StringBuilder(200);
        if (CM_Get_Device_IDW(adapter, path, (uint)path.Capacity, 0) != 0) return AudioInputOrigin.Unknown;
        var adapterId = path.ToString();
        if (adapterId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
            || adapterId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)) return AudioInputOrigin.Software;

        var key = LocalContainerKey;
        uint size = 1;
        return CM_Get_DevNode_PropertyW(adapter, ref key, out var type, out var isLocal, ref size, 0) == 0
            && type == 0x11 && isLocal != 0 ? AudioInputOrigin.Integrated : AudioInputOrigin.Unknown;
    }

    internal static void Capture(string deviceId, bool loopback, Action started,
        Action<nint, int, uint, long, int, ulong> packet, CancellationToken token, bool speechProfile = false)
    {
        var enumerator = CreateEnumerator();
        IDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? reader = null;
        var running = false;
        try
        {
            var direction = loopback ? Direction.Output : Direction.Input;
            if (!loopback && deviceId == AutomaticInputId)
            {
                var inputs = GetDevices(enumerator, Direction.Input);
                var selected = SelectAutomaticInput(inputs, FindDefaultDevice(enumerator, Direction.Input, inputs))
                    ?? throw new InvalidOperationException("Mikrofon otomatis belum tersedia. Pilih perangkat di Pengaturan.");
                deviceId = selected.Id;
            }

            Check(string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(direction, DefaultRole(direction), out device)
                : enumerator.GetDevice(deviceId, out device));
            Check(device.GetState(out var state));
            if (state != Active) throw new InvalidOperationException("Perangkat audio sudah tidak aktif.");

            var flags = ConvertPcm | ResampleHighQuality | (loopback ? Loopback : 0);
            int channels = 0;
            int Initialize(bool speech, bool alternateChannels)
            {
                Release(client);
                client = null;
                var clientId = typeof(IAudioClient).GUID;
                Check(device.Activate(ref clientId, 23, 0, out var activated));
                client = (IAudioClient)activated;
                if (speech && client is IAudioClient2 advanced)
                {
                    var properties = new AudioClientProperties { Size = Marshal.SizeOf<AudioClientProperties>(), Category = 9 }; // AudioCategory_Speech
                    var result = advanced.SetClientProperties(ref properties);
                    if (result < 0) return result;
                }
                channels = loopback ? 2 : PreferredInputChannels(client);
                if (alternateChannels) channels = channels == 1 ? 2 : 1;
                var format = new WaveFormat(channels);
                return client.Initialize(0, flags, 1_000_000, 0, ref format, 0);
            }
            var speech = speechProfile && !loopback;
            var initialize = Initialize(speech, false);
            if (speech && initialize < 0) initialize = Initialize(false, false);
            if (!loopback && initialize is UnsupportedFormat or InvalidArgument) initialize = Initialize(false, true);
            Check(initialize);
            var readerId = typeof(IAudioCaptureClient).GUID;
            Check(client!.GetService(ref readerId, out var service));
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
                    Check(reader.GetBuffer(out var data, out var frames, out var packetFlags, out var position, out var timestamp));
                    try
                    {
                        if (frames > 0) packet(data, checked((int)frames), packetFlags, checked((long)timestamp), channels, position);
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

    private static int PreferredInputChannels(IAudioClient client)
    {
        if (client.GetMixFormat(out var format) < 0 || format == nint.Zero) return 2;
        try { return Marshal.PtrToStructure<WaveFormat>(format).Channels == 1 ? 1 : 2; }
        finally { Marshal.FreeCoTaskMem(format); }
    }

    internal static string MicrophoneError(Exception exception) => exception.HResult switch
    {
        unchecked((int)0x80070005) => "Windows tidak mengizinkan akses mikrofon. Periksa izin mikrofon.",
        unchecked((int)0x8889000A) => "Mikrofon sedang dipakai secara eksklusif oleh aplikasi lain.",
        UnsupportedFormat => "Format mikrofon tidak didukung oleh driver audio saat ini.",
        _ => "Mikrofon tidak bisa dibuka. Periksa sambungan dan perangkat yang dipilih."
    };

    private static IDeviceEnumerator CreateEnumerator() => (IDeviceEnumerator)(object)new DeviceEnumerator();
    private static Role DefaultRole(Direction direction) => direction == Direction.Input ? Role.Console : Role.Multimedia;
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(object? instance)
    {
        if (instance is not null) Marshal.ReleaseComObject(instance);
    }

    private enum Direction { Output, Input }
    private enum Role { Console, Multimedia }

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
        [FieldOffset(8)] public uint Unsigned;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint device, string instanceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint device, StringBuilder buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint device, ref PropertyKey key, out uint type,
        out byte value, ref uint bufferSize, uint flags);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(Direction direction, uint state, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(Direction direction, Role role, out IDevice device);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProperties
    {
        internal int Size, Offload, Category, Options;
    }

    [ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient2
    {
        // IAudioClient's twelve methods followed by IAudioClient2.IsOffloadCapable.
        void _VtblGap1_13();
        [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
    }
}
