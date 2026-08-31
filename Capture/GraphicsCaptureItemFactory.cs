using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Clario_Presenter.Capture;

internal static class GraphicsCaptureItemFactory
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    public static GraphicsCaptureItem CreateForWindow(nint windowHandle)
        => Create(interop => interop.CreateForWindow(windowHandle, GraphicsCaptureItemGuid));

    public static GraphicsCaptureItem CreateForMonitor(nint monitorHandle)
        => Create(interop => interop.CreateForMonitor(monitorHandle, GraphicsCaptureItemGuid));

    private static GraphicsCaptureItem Create(Func<IGraphicsCaptureItemInterop, nint> createItem)
    {
        using var activationFactory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = activationFactory.AsInterface<IGraphicsCaptureItemInterop>();
        var itemPointer = createItem(interop);

        if (itemPointer == nint.Zero)
        {
            throw new InvalidOperationException("Windows tidak dapat membuat sumber capture untuk jendela ini.");
        }

        try
        {
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }
}
