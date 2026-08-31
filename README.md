# Clario Presenter

Clario Presenter adalah aplikasi Windows untuk mempresentasikan satu jendela atau mencerminkan monitor utama ke monitor klien, sementara catatan, checklist, dan kontrol presentasi tetap privat di laptop presenter.

## Status saat ini

Versi MVP sudah berjalan sebagai aplikasi WinUI 3 native:

- enumerasi jendela aplikasi yang sedang terbuka;
- deteksi monitor utama dan monitor eksternal;
- capture jendela nyata melalui `Windows.Graphics.Capture`;
- mode **Mirror Aman** untuk mencerminkan seluruh monitor utama;
- auto-fit menjaga rasio sumber ketika resolusi atau rasio monitor berbeda;
- pemilih jendela privat per aplikasi;
- auto-hold menahan frame publik terakhir hanya ketika Clario atau jendela privat sedang berada di depan;
- jendela privat otomatis diminimalkan saat presenter kembali ke aplikasi publik agar mirror segera berjalan tanpa membocorkan jendela di belakang;
- Clario otomatis diminimalkan ketika presenter kembali ke Canva/browser agar tidak ikut tercermin;
- preview sumber berukuran penuh di studio presenter sehingga web tetap nyaman dioperasikan;
- output fullscreen bersih pada monitor eksternal;
- jendela aplikasi yang terbuka atau mengingat posisi di monitor klien otomatis dikembalikan ke layar laptop selama sesi aktif;
- panel catatan privat dan checklist;
- timer sesi;
- state Ready, Live, Freeze, dan Privacy;
- Freeze menahan frame terakhir tanpa menghentikan kontrol presenter;
- Privacy mengganti output klien dengan layar jeda yang netral;
- fallback aman otomatis ketika jendela sumber ditutup;
- render event-driven mengikuti kedatangan frame, tanpa polling 30 FPS;
- reusable double buffer GPU dan pembuangan frame antrean yang sudah usang untuk menjaga latency rendah;
- panel catatan yang dapat disembunyikan;
- dukungan dasar UI Automation dan keyboard accessibility.

Frame GPU disalin ke dua render target yang digunakan bergantian. Buffer dibuat ulang hanya ketika ukuran jendela berubah, bukan pada setiap frame. Ini menjaga lifetime frame tetap aman, menghindari alokasi berulang, dan memungkinkan preview laptop serta output klien memakai device Win2D yang sama.

## Menjalankan aplikasi

Persyaratan pengembangan:

- Windows 10 build 19041 atau lebih baru;
- .NET 9 SDK;
- akses NuGet untuk memulihkan Windows App SDK.

```powershell
dotnet restore -r win-x64
dotnet build -p:Platform=x64
dotnet run --no-build -p:Platform=x64
```

Untuk mode biasa, pilih jendela sumber dan monitor klien, kemudian tekan **Mulai Live**. Jendela sumber perlu tetap terbuka dan tidak diminimalkan; Windows tidak mengirim frame baru dari sebagian aplikasi yang sedang diminimalkan.

Untuk **Mirror Aman**:

1. Pilih `Mirror Aman` pada mode presentasi.
2. Tekan `Privat` dan centang PDF atau jendela catatan yang tidak boleh terlihat.
3. Tekan `Mulai Live`. Clario dan jendela privat yang sedang terbuka otomatis diminimalkan, lalu aplikasi publik langsung diteruskan.
4. Ketika Clario/jendela privat dibuka, klien tetap melihat frame aman terakhir.
5. Ketika berpindah dari jendela privat ke Canva/web, jendela privat otomatis diminimalkan dan mirror langsung berjalan kembali.

Jika rasio monitor berbeda, aplikasi menampilkan keseluruhan layar dengan letterbox tipis bila diperlukan. Gambar tidak ditarik atau dibuat gepeng.

## Prinsip desain

- permukaan graphite dengan kontras tenang;
- satu warna aksen utama, indigo;
- warna status hanya digunakan secara semantik;
- hijau untuk Live, amber untuk Freeze, merah untuk Privacy;
- tipografi Segoe UI Variable;
- tidak menggunakan gradient dekoratif atau kumpulan kartu yang tidak diperlukan;
- tindakan darurat selalu terlihat dan memiliki label yang jelas.

## Teknologi

- C# dan .NET 9;
- WinUI 3 / Windows App SDK 1.8;
- Windows Graphics Capture untuk menangkap satu jendela atau monitor utama;
- Win2D di atas Direct3D 11 untuk preview dan output GPU;
- Win32 interop untuk enumerasi jendela dan monitor;
- AppWindow fullscreen presenter untuk monitor klien.

## Struktur

```text
Clario.Presenter
├── App.xaml                         design tokens dan style global
├── MainWindow.xaml                  shell dan title bar
├── MainPage.xaml                    presenter studio
├── MainPage.xaml.cs                 state dan interaksi sesi
├── OutputWindow.xaml                output fullscreen untuk klien
├── OutputWindow.xaml.cs             placement monitor dan privacy slate
├── Capture
│   ├── CaptureSessionService.cs     frame pool, freeze, dan rendering
│   └── GraphicsCaptureItemFactory.cs interop capture berdasarkan HWND
└── Services
    ├── WindowCatalogService.cs      enumerasi jendela Win32
    └── DisplayCatalogService.cs     enumerasi monitor Win32
```

## Pengembangan berikutnya

1. Shortcut global untuk Freeze, Privacy, dan fokus kembali ke sumber.
2. Pemilih PDF referensi dan daftar cue per bagian demo.
3. Penyimpanan sesi catatan per klien/proyek.
4. Installer MSIX bertanda tangan dan alur pembaruan aplikasi.
5. Telemetri performa lokal untuk mendeteksi frame drop tanpa merekam isi layar.
