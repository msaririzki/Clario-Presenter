# Pengembangan Clario Presenter

Clario adalah aplikasi desktop Windows berbasis C#, .NET 10, dan WinUI 3. Untuk menyiapkan proyek dan menjalankan pengujian dasar, lihat [panduan kontribusi](../CONTRIBUTING.md).

## Dari sumber gambar ke layar audiens

Windows Graphics Capture menangkap monitor atau jendela yang dipilih. Win2D menggunakan perangkat Direct3D yang sama untuk pratinjau dan output layar kedua. Saat frame datang, layanan tangkapan menyalinnya ke salah satu dari dua render target yang dipakai bergantian. Ukuran target menyesuaikan perubahan ukuran sumber.

Saat presenter membuka jendela yang disembunyikan, Clario mempertahankan gambar desktop aman terakhir. Jendela publik yang sudah mengirim frame dapat terus diperbarui di posisi dan ukuran aslinya. Komposisi memperhitungkan urutan jendela, dialog dari proses yang sama, batas monitor sumber, dan posisi taskbar. Pengujian komposisi memeriksa kasus seperti dialog perangkat Cisco, jendela publik yang saling menutupi, dan perbedaan ukuran monitor.

Perekam membaca hasil komposisi melalui `IRecordingFrameSource`. `OutputRecordingService` memberi setiap sampel video buffer tersendiri agar encoder dapat selesai membaca satu frame saat GPU menyiapkan frame berikutnya. Kamera ditambahkan pada tahap perekaman. Audio komputer dan mikrofon menggunakan WASAPI, dicampur pada garis waktu rekaman, lalu disimpan sebagai AAC dalam MP4.

| Bagian | Lokasi |
| --- | --- |
| Pemilihan sumber, kontrol sesi, pratinjau | [MainPage.xaml](../MainPage.xaml) dan [MainPage.xaml.cs](../MainPage.xaml.cs) |
| Penempatan output di monitor audiens | [OutputWindow.xaml.cs](../OutputWindow.xaml.cs) |
| Tangkapan, komposisi desktop, pemilihan jendela | [Capture](../Capture) dan [Services](../Services) |
| Perekaman, kamera, audio | [Recording](../Recording), [Camera](../Camera), [Audio](../Audio) |
| Catatan melayang, Markdown, dan navigasi | [Notes](../Notes) |
| Pengaturan dan penyimpanan | [Settings](../Settings) |
| Tata letak dan perilaku kontrol | [Rencana UI/UX](rencana-ui-ux.md) |

## Pengujian di perangkat

Pengujian dasar dapat dijalankan dengan `dotnet run --project Tests/Clario.GeometryTests.csproj -c Release`. Tambahkan `-- --native` untuk memeriksa jendela Cisco Packet Tracer yang sedang terbuka.

Untuk uji media dan perpindahan jendela, bangun program uji berikut dari folder utama repositori.

```powershell
dotnet build Tests/CameraSmoke/Clario.CameraSmoke.csproj -c Release
```

Jalankan program uji dalam konteks paket pengembangan Clario yang sudah terdaftar di Windows. Contoh berikut memilih uji audio dan memakai Windows PowerShell 5.1. Identitas paket dapat dilihat di [Package.appxmanifest](../Package.appxmanifest).

```powershell
$package = Get-AppxPackage -Name '237E57D9-2488-4F07-B595-A20D0BDFEF66'
$exe = Resolve-Path 'Tests/CameraSmoke/bin/Release/net10.0-windows10.0.26100.0/win-x64/Clario.CameraSmoke.exe'
$report = Join-Path $env:TEMP 'Clario-AudioSmoke.json'
Invoke-CommandInDesktopPackage -PackageFamilyName $package.PackageFamilyName -AppId App -Command $exe.Path -Args ('--audio "{0}"' -f $report)
```

Baca laporan JSON dan periksa nilai `Passed` setelah pengujian selesai. Uji rekaman mencantumkan lokasi MP4 dalam laporan.

| Argumen | Yang diperiksa |
| --- | --- |
| Tanpa argumen mode | Menyalakan kamera pertama, menguji pratinjau, membuka kembali kamera, merekam pola uji 1080p60, dan memeriksa transisi Privasi |
| `--desktop-only` | Berpindah di antara jendela uji publik dan privat sambil memeriksa pembaruan frame |
| `--floating-notes` | Menjangkau catatan panjang, mengubah ukuran, memilih bagian, menjaga fokus, dan memeriksa pengecualian panel dari tangkapan |
| `--reference-notes` | Menguji panel aplikasi langsung, klik tembus, dan video dari jendela publik saat referensi dibuka atau ditutup |
| `--recording-stability` | Menjalankan perekam selama tiga menit pada 1080p60 dengan gambar bergerak, audio sintetis, dan pengumpulan memori berkala. MP4 disimpan di sebelah laporan. |
| `--audio` | Membuka keluaran WASAPI dan membuat MP4 uji dengan suara sintetis serta segmen Privasi |
| `--audio-input-open-only` | Membuka input mikrofon tanpa membaca sampel audio |

## Membuat installer

Gunakan Windows x64, .NET 10 SDK, Inno Setup 6.3.3 atau lebih baru dalam seri 6, dan komponen Visual C++ x64 dari Visual Studio Build Tools.

```powershell
.\installer\Build-Installer.ps1
```

Skrip menghasilkan `Clario-Presenter-Setup-x64.exe` dan `SHA256SUMS.txt` di `bin/installer/<versi>/`. Versi berasal dari properti `Version` di [Clario.Presenter.csproj](../Clario.Presenter.csproj). Untuk rilis, gunakan tag `v<versi>` dan unggah kedua file dengan nama tersebut agar tautan unduhan di README tetap berfungsi.

## Rencana implementasi referensi aplikasi

1. Tambahkan pemilih jendela pada panel Catatan. Periksa identitas sumber dan cegah pengguna memilih aplikasi yang juga akan direkam.
2. Pakai kembali panel melayang dan kontrolnya. Tampilkan sumber di area baca melalui Windows Graphics Capture. Tambahkan zoom dan geser isi. Periksa pengecualian rekaman sebelum menampilkan panel dan lepaskan tangkapan saat panel ditutup.
3. Simpan identitas jendela privat untuk seluruh sesi. Susun mode Layar dari tangkapan jendela publik sejak frame pertama. Kecualikan proses, keluarga jendela sumber, panel Clario, dan elemen pemilih aplikasi. Gunakan komposisi yang sama untuk preview, layar audiens, dan MP4. Pisahkan setelan mikrofon dari audio komputer saat referensi aktif.
4. Uji pembesaran, klik tembus, pembaruan sumber, sumber tertutup, serta rekaman publik saat sumber privat menutupi jendela publik. Jalankan ulang pengujian catatan teks dan tangkapan layar sebelum memperbarui GitHub.
