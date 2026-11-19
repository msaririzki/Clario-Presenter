using System.Text.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace Clario_Presenter.Updates;

internal sealed record UpdateNotes(string Text, string? Name, bool Markdown, bool Reader, bool Panel, bool Pinned,
    int Caret, double ReaderOffset, float FloatingOffset, double FontSize, double Width, double Height,
    double Opacity, bool ClickThrough, string? Display)
{
    internal static string PathOnDisk => Path.Combine(UpdateService.Folder, "notes-recovery.dat");

    internal async Task SaveAsync()
    {
        Directory.CreateDirectory(UpdateService.Folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        var encrypted = await new DataProtectionProvider("LOCAL=user")
            .ProtectAsync(CryptographicBuffer.CreateFromByteArray(bytes));
        CryptographicBuffer.CopyToByteArray(encrypted, out var data);
        var tmp = PathOnDisk + "." + Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(tmp, data); File.Move(tmp, PathOnDisk, true); }
        finally { File.Delete(tmp); }
    }

    internal static async Task<UpdateNotes> ReadAsync()
    {
        var data = await File.ReadAllBytesAsync(PathOnDisk);
        var clear = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(data));
        CryptographicBuffer.CopyToByteArray(clear, out var bytes);
        return JsonSerializer.Deserialize<UpdateNotes>(bytes) ?? throw new InvalidDataException();
    }
}
