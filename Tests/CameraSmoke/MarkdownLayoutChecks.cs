using System.Text.Json;
using Clario_Presenter.Notes;
using Microsoft.Graphics.Canvas;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Clario_Presenter;

internal static class MarkdownLayoutChecks
{
    internal static async Task RunAsync(string report)
    {
        var notes = NoteDocument.Parse("""
            # Praktik jaringan
            ## Pemeriksaan router
            > **Narasi** — Periksa *status antarmuka* sebelum mengubah konfigurasi.

            | Router | Antarmuka | Alamat IP | Status |
            | :--- | :--- | :--- | :---: |
            | R1 | `GigabitEthernet0/0` | 192.168.10.1 | **up** |
            | R2 | `GigabitEthernet0/1` | 192.168.20.1 | down |

            ```text
            show ip interface brief
            show interfaces description
            ```

            - [x] Periksa koneksi
            - [ ] Simpan konfigurasi

            ## Langkah berikutnya
            Jalankan `write memory`.
            """, true);
        var device = CanvasDevice.GetSharedDevice();
        using var renderer = new NoteOverlayRenderer();
        float scroll = 0;
        float Measure(int width) => renderer.Measure(device, notes, (float)new NoteOverlayGeometry(width, 560).Body.Width, 17, ref scroll);
        var wide = Measure(736);
        var wideSection = renderer.SectionTop(1);
        scroll = wideSection;
        var narrow = Measure(346);
        if (narrow <= wide || Math.Abs(scroll - renderer.SectionTop(1)) > 1)
            throw new InvalidOperationException("Narrow table did not reflow or lost the section anchor.");
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(report))!);
        foreach (var width in new[] { 736, 346 })
        {
            scroll = 0;
            var height = Measure(width);
            var geometry = new NoteOverlayGeometry(width, 590);
            using var image = new CanvasRenderTarget(device, width, geometry.Height, 96);
            using (var draw = image.CreateDrawingSession())
                renderer.Draw(draw, geometry, notes, 0, false, true, scroll, height);
            var file = await folder.CreateFileAsync($"markdown-{width}.png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            await image.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, WideHeight = wide, NarrowHeight = narrow }));
    }
}
