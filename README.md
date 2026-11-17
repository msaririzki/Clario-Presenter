# Clario Presenter

Clario Presenter adalah aplikasi Windows untuk menampilkan dan merekam satu layar atau aplikasi, sambil menyembunyikan jendela tertentu. Catatan dan kontrol presentasi hanya terlihat oleh presenter.

## Status saat ini

Versi MVP sudah berjalan sebagai aplikasi WinUI 3 native:

- enumerasi jendela aplikasi yang sedang terbuka;
- deteksi monitor utama dan monitor eksternal;
- capture jendela nyata melalui `Windows.Graphics.Capture`;
- pilihan sumber **Layar** atau **Aplikasi** untuk merekam atau presentasi;
- mode **Rekam** menyimpan video langsung, dengan pilihan **Lihat pratinjau** untuk memeriksa sumber;
- sumber layar selalu memakai ukuran **Seluruh gambar** agar tepi dan taskbar tetap di tempatnya saat membuka aplikasi tersembunyi;
- untuk sumber aplikasi, **Perbesar** memberi zoom ringan dan **Isi layar** memenuhi area tampilan;
- pemilih jendela privat per aplikasi;
- Live Terlindungi mempertahankan desktop aman terakhir dan memperbarui jendela publik di posisi serta ukuran aslinya sehingga dialog kecil tidak diperbesar menjadi seluruh output;
- capture jendela publik disiapkan sebelum membuka aplikasi privat dan digunakan kembali selama perpindahan; adegan lama baru diganti setelah seluruh jendela baru mengirim frame nyata;
- snapshot desktop menahan transisi Alt+Tab/preview taskbar; perpindahan ke aplikasi privat tidak mengganti rekaman dengan layar jeda dan tidak mengubah dimensi output;
- jendela di monitor lain disaring dari komposisi, dan jendela publik yang tertutup penuh tidak membutuhkan capture aktif tambahan;
- jendela publik dan dialog perangkat dari proses yang sama disusun sesuai urutan desktop, termasuk dialog Cisco Packet Tracer yang tidak memiliki Win32 owner;
- taskbar aman disimpan terpisah dan selalu digambar di posisi tetap di atas jendela publik;
- jika jendela publik sempat diminimalkan, Clario memulihkannya tanpa mengambil fokus agar video tetap mengirim frame di belakang aplikasi privat;
- jendela privat dapat tetap terbuka berdampingan dengan aplikasi publik; mirror desktop hanya dilanjutkan ketika jendela privat sudah diminimalkan atau ditutup;
- Clario otomatis diminimalkan ketika presenter kembali ke Canva/browser agar tidak ikut tercermin;
- pratinjau sumber di area utama saat sesi aktif;
- output fullscreen bersih pada monitor eksternal;
- jendela aplikasi yang terbuka atau mengingat posisi di monitor klien otomatis dikembalikan ke layar laptop selama sesi aktif;
- editor catatan privat yang dibuka sesuai kebutuhan dan timer rekaman pada tombol **Stop rekam**;
- perekam output klien ke MP4 H.264 1920×1080 dengan pilihan 30 atau 60 fps dan akselerasi hardware;
- overlay kamera khusus rekaman: pilihan webcam internal/USB atau kamera virtual yang terdaftar di Windows, preview, empat posisi, tiga ukuran, serta opsi cermin;
- kamera dimulai hanya saat `Tes kamera` atau REC dengan kamera diaktifkan; kamera dilepas setelah preview ditutup atau rekaman selesai;
- jika kamera tidak tersedia saat mulai REC, aplikasi menampilkan error; jika kamera terputus di tengah sesi, rekaman layar berlanjut dengan penanda kamera tidak tersedia;
- buffer BGRA independen per sampel mencegah encoder membaca frame GPU yang sedang digambar ulang dan menghilangkan kilatan hitam;
- backpressure encoder menghindari render/readback GPU yang belum diminta dan timestamp berbasis waktu nyata mencegah video dipercepat ketika frame 60 fps terlambat;
- rekaman mengambil buffer output internal Clario, sehingga panel Clario, PDF, dan aplikasi privat tidak ikut masuk;
- menu Pengaturan untuk kelancaran video, kualitas video, folder rekaman, dan pilihan lokasi setiap kali merekam;
- penyimpanan otomatis yang rapi ke `Videos\Clario Presenter` dengan nama file bertimestamp;
- status Siap, Aktif, Gambar ditahan, dan Privasi;
- Freeze mempertahankan gambar terakhir tanpa menghentikan kontrol presenter;
- Privasi mengganti tampilan audiens dengan pesan jeda yang netral;
- fallback aman otomatis ketika jendela sumber ditutup;
- render event-driven mengikuti kedatangan frame, tanpa polling 30 FPS;
- reusable double buffer GPU dan pembuangan frame antrean yang sudah usang untuk menjaga latency rendah;
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

### Rekam

1. Pilih mode **Rekam**, lalu pilih sumber **Layar** atau **Aplikasi**.
2. Jika merekam layar, buka **Sembunyikan aplikasi**, centang jendela privat, lalu klik **Terapkan**. Untuk memeriksa gambar terlebih dahulu, klik **Lihat pratinjau**.
3. Klik **Mulai rekam**. Tekan **Stop rekam** untuk menyimpan video dan mengakhiri sesi. Untuk rekaman layar, buka kembali Clario dari taskbar agar tombolnya terlihat.

Video disimpan ke `Videos\Clario Presenter`. Setelah video tersimpan, klik **Buka folder** untuk melihat hasilnya. Gunakan **Pengaturan** untuk mengganti folder atau memilih lokasi setiap kali merekam. Rekaman belum menyertakan suara.

### Presentasi

1. Pilih mode **Presentasi**, tentukan sumber dan **Layar audiens**.
2. Jika sumbernya **Layar**, Anda bisa memilih **Sembunyikan aplikasi** sebelum mulai. Jika sumbernya **Aplikasi**, biarkan jendela tersebut terbuka.
3. Klik **Mulai presentasi**. Klik **Rekam** di bilah sesi jika Anda juga ingin menyimpan video. **Stop rekam** menyimpan video sambil meneruskan presentasi. **Stop presentasi** mengakhiri sesi dan menyimpan video yang sedang direkam.

**Freeze** menahan gambar terakhir. **Privasi** menutup gambar sementara. Keduanya tersedia ketika sesi berjalan. Aplikasi yang dipilih lewat **Sembunyikan aplikasi** bisa dibuka selama sesi. Untuk tab Chrome privat, pindahkan tab tersebut ke jendela Chrome tersendiri sebelum memilihnya.

Sumber layar ditampilkan utuh. Untuk sumber aplikasi, buka **Opsi tampilan** untuk memilih **Ukuran gambar**. Pilihan **Perbesar** dan **Isi layar** dapat memotong tepi gambar.

### Rekam layar dan kamera

1. Hubungkan webcam USB, atau jalankan aplikasi kamera pihak ketiga dan aktifkan output kamera virtualnya.
2. Buka **Pengaturan**, aktifkan **Sertakan kamera dalam video**, lalu pilih kamera. Gunakan **Perbarui daftar** jika perangkat baru disambungkan.
3. Tekan `Tes kamera` untuk memeriksa gambar. Jika Windows meminta izin kamera, berikan izin agar perangkat dapat digunakan.
4. Pilih posisi dan ukuran, lalu tekan **Simpan**. Pratinjau berhenti dan kamera dinyalakan kembali saat merekam.
5. Klik **Mulai rekam**, atau klik **Rekam** setelah presentasi dimulai. Kamera muncul di video.

Hasil video tetap 1080p 30/60 fps sesuai Pengaturan; kelancaran kamera mengikuti perangkat/aplikasi kamera. Kamera 4:3 atau vertikal dipaskan tanpa meregangkan atau memotong wajah. Kamera ditempatkan di area aman agar taskbar tidak tertutup. Saat Privasi aktif atau tampilan terputus, kamera juga disembunyikan dari video. Freeze mempertahankan gambar layar; kamera tetap bergerak. Suara belum disertakan.

Clario menerima perangkat video yang bisa dibuka oleh Windows MediaCapture. Aplikasi yang hanya menampilkan preview kamera tanpa menyediakan perangkat kamera virtual belum dapat dipilih sebagai input kamera. Bila perangkat tidak muncul, aktifkan output virtualnya terlebih dahulu lalu muat ulang daftar; kompatibilitas perlu diuji pada driver yang dipakai.

### Catatan mengambang untuk praktik Cisco

1. Di panel **Catatan**, ketik atau tempel teks, atau klik **Buka file** dan pilih `.txt`, `.md`, atau `.markdown`. Contoh siap dibuka: [Praktik-Cisco.md](Examples/Praktik-Cisco.md).
2. Klik **Sematkan**. Matikan **Klik tembus** dengan **Ctrl+Shift+F9** untuk menarik tepi atau sudut panel, menggesernya dari judul, atau menarik scrollbar. Di **Atur catatan**, lebar dan tinggi bisa diatur terpisah. Klik **Aktifkan tembus** pada panel untuk kembali mengendalikan aplikasi di belakangnya.
3. Pilih mode **Rekam** dan sumber **Layar**. Pilih layar tempat topologi dan dialog router/switch terbuka, lalu klik **Mulai rekam**. Dialog harus tetap berada di layar yang dipilih.

Untuk Markdown dengan beberapa judul `##`, klik **Daftar** untuk melompat langsung ke bagian yang dipilih. Setiap bagian bisa digulir sampai akhir. Catatan `.txt` dan Markdown dapat berisi hingga 200.000 karakter.

Tahan **Ctrl+Shift** saat menggunakan shortcut panel. **F8** menampilkan atau menyembunyikan catatan, **F9** mengubah Klik tembus, **F10/F11** berpindah bagian, **↑/↓** menggulir sedikit, **Page Up/Page Down** menggulir hampir satu layar, dan **Home/End** menuju awal atau akhir. Shortcut gulir dan pindah bagian bisa dipakai ketika Klik tembus aktif. Jika shortcut sedang digunakan aplikasi lain, gunakan scrollbar atau tombol panel dalam mode Atur.

Untuk catatan dari AI, minta satu file Markdown UTF-8 dengan judul `## Nomor — Perangkat dan tugas` per langkah. Gunakan teks tebal untuk Narasi dan Cek hasil, dan blok kode untuk perintah. Clario menampilkan isi Markdown sebagai teks lokal; gambar, halaman web, dan skrip tidak dimuat. Siapkan isi catatan sebelum memulai pratinjau atau perekaman. Editor dilipat saat sesi dimulai dan catatan yang disematkan tetap terlihat.

Clario meminta Windows mengecualikan panel dari tangkapan. Jika pengecualian tidak dapat dipasang atau diverifikasi, Clario memindahkan panel ke layar yang tidak direkam dan tidak digunakan audiens, atau menyembunyikannya. Ini diverifikasi untuk jalur tangkapan Clario, bukan jaminan untuk perekam lain. Lakukan satu rekaman uji singkat dengan file Packet Tracer yang akan dipakai.

## Prinsip desain

- permukaan graphite dengan kontras tenang;
- satu warna aksen utama, indigo;
- warna status hanya digunakan secara semantik;
- hijau untuk presentasi aktif, kuning untuk Freeze, ungu untuk Privasi, dan merah muda untuk perekaman;
- tipografi Segoe UI Variable;
- tidak menggunakan gradient dekoratif atau kumpulan kartu yang tidak diperlukan;
- tindakan darurat selalu terlihat dan memiliki label yang jelas.

## Teknologi

- C# dan .NET 9;
- WinUI 3 / Windows App SDK 1.8;
- Windows Graphics Capture untuk menangkap satu jendela atau monitor utama;
- Win2D di atas Direct3D 11 untuk preview dan output GPU;
- MediaStreamSource dan MediaTranscoder untuk encoding H.264 1080p60 berbasis hardware;
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
│   ├── DesktopCompositionLayout.cs  geometri desktop, posisi jendela, dan taskbar
│   ├── DesktopWindowPlan.cs         pemilihan jendela pada monitor sumber dan urutan layer
│   └── GraphicsCaptureItemFactory.cs interop capture berdasarkan HWND
├── Recording
│   └── OutputRecordingService.cs    buffer frame stabil dan encoder MP4
├── Camera
│   ├── CameraCaptureService.cs      input webcam/virtual camera dan buffer gambar
│   ├── CameraOverlayLayout.cs       posisi, ukuran, dan proporsi overlay
│   └── CameraOverlayRenderer.cs     komposisi kamera pada preview/MP4
├── Settings
│   ├── PresenterSettings.cs         model pengaturan persisten
│   ├── AppSettingsService.cs        penyimpanan setting dan folder rekaman
│   ├── SettingsDialog.xaml          tampilan pengaturan
│   └── SettingsDialog.xaml.cs       interaksi pengaturan
└── Services
    ├── WindowCatalogService.cs      enumerasi jendela Win32
    └── DisplayCatalogService.cs     enumerasi monitor Win32
```

Pemeriksaan regresi geometri desktop dapat dijalankan tanpa framework tes tambahan:

```powershell
dotnet run --project Tests/Clario.GeometryTests.csproj
```

Tambahkan `-- --native` untuk memeriksa bounds dan pemilihan jendela Cisco Packet Tracer yang sedang terbuka.

Uji integrasi kamera (opsional, benar-benar menyalakan kamera pertama yang tersedia) ada di `Tests/CameraSmoke`. Harness memakai layar pola uji, bukan merekam desktop pengguna, dan mencoba preview, pelepasan/reopen kamera, encoding 1080p60, serta transisi Privacy. Jalankan dengan konteks paket Clario agar Win2D tersedia:

```powershell
dotnet build Tests/CameraSmoke/Clario.CameraSmoke.csproj
# Di Windows PowerShell 5.1, setelah Clario sudah terdaftar pada Windows:
Invoke-CommandInDesktopPackage -PackageFamilyName '237E57D9-2488-4F07-B595-A20D0BDFEF66_1z32rh13vfry6' -AppId App -Command 'E:\projeck git\Bagikan layar\Tests\CameraSmoke\bin\Debug\net9.0-windows10.0.26100.0\win-x64\Clario.CameraSmoke.exe'
```

Sesuaikan path repository. Laporan tersimpan di `%TEMP%\Clario-CameraSmoke.json`; lokasi MP4 uji dicantumkan di laporan. Kamera virtual/eksternal perlu diuji ulang setelah drivernya tersedia.

Tambahkan `--desktop-only` pada argumen harness untuk uji perpindahan publik/privat tanpa menyalakan kamera. Uji ini membuat jendela pola sementara, melakukan sepuluh perpindahan, memeriksa frame privat/blank dan pembaruan aplikasi publik, serta memastikan dua capture publik tidak dimulai ulang. Contoh `-Args '--desktop-only C:\Users\msari\AppData\Local\Temp\Clario-DesktopSwitch.json'`.

Gunakan `--floating-notes` pada harness yang sama untuk menguji catatan mengambang tanpa kamera. Harness memeriksa baris terakhir teks dan blok kode panjang, resize, perpindahan dari daftar bagian, tombol gulir, fokus, dan warna dua jendela uji di balik panel melalui jalur gambar yang dipakai perekam. Gambar monitor tidak disimpan.

## Pengembangan berikutnya

1. Shortcut global untuk Freeze, Privacy, dan fokus kembali ke sumber.
2. Pemilih PDF referensi dan daftar cue per bagian demo.
3. Penyimpanan sesi catatan per klien/proyek.
4. Installer MSIX bertanda tangan dan alur pembaruan aplikasi.
5. Telemetri performa lokal untuk mendeteksi frame drop tanpa merekam isi layar.
6. Pilihan audio mikrofon dan audio sistem untuk rekaman presentasi.
