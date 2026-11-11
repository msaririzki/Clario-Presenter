# Clario Presenter

Clario Presenter adalah aplikasi Windows untuk mempresentasikan satu jendela atau mencerminkan monitor utama ke monitor klien, sementara catatan, checklist, dan kontrol presentasi tetap privat di laptop presenter.

## Status saat ini

Versi MVP sudah berjalan sebagai aplikasi WinUI 3 native:

- enumerasi jendela aplikasi yang sedang terbuka;
- deteksi monitor utama dan monitor eksternal;
- capture jendela nyata melalui `Windows.Graphics.Capture`;
- mode **Mirror Aman** untuk merekam/mencerminkan satu monitor sumber yang dipilih, termasuk monitor kedua;
- opsi **Rekam saja** menyiapkan sumber rekaman tanpa membuka output fullscreen atau memindahkan aplikasi pada monitor lain;
- mode default `Seluruh layar` mempertahankan desktop utuh tanpa crop; Mirror Aman selalu memakai mode ini agar tepi layar dan taskbar tidak berubah saat membuka aplikasi privat;
- pilihan `Adaptif nyaman` pada mode aplikasi tunggal memperhitungkan resolusi, skala Windows, dan perkiraan ukuran fisik monitor dari raw DPI, lalu memberi zoom ringan;
- mode `Seluruh layar` menampilkan semua area tanpa crop dan mode `Isi layar (crop)` memenuhi monitor semaksimal mungkin;
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
- preview sumber berukuran penuh di studio presenter sehingga web tetap nyaman dioperasikan;
- output fullscreen bersih pada monitor eksternal;
- jendela aplikasi yang terbuka atau mengingat posisi di monitor klien otomatis dikembalikan ke layar laptop selama sesi aktif;
- panel catatan privat dan checklist;
- timer sesi;
- perekam output klien ke MP4 H.264 1920×1080 dengan pilihan 30 atau 60 fps dan akselerasi hardware;
- overlay kamera khusus rekaman: pilihan webcam internal/USB atau kamera virtual yang terdaftar di Windows, pratinjau, empat posisi, tiga ukuran, serta opsi cermin;
- kamera dimulai hanya saat `Uji kamera` atau REC dengan kamera diaktifkan; kamera dilepas setelah pratinjau ditutup atau rekaman selesai;
- jika kamera tidak tersedia saat mulai REC, aplikasi menampilkan error; jika kamera terputus di tengah sesi, rekaman layar berlanjut dengan penanda kamera tidak tersedia;
- buffer BGRA independen per sampel mencegah encoder membaca frame GPU yang sedang digambar ulang dan menghilangkan kilatan hitam;
- backpressure encoder menghindari render/readback GPU yang belum diminta dan timestamp berbasis waktu nyata mencegah video dipercepat ketika frame 60 fps terlambat;
- rekaman mengambil buffer output internal Clario, sehingga panel Clario, PDF, dan aplikasi privat tidak ikut masuk;
- menu Settings untuk frame rate, bitrate, folder rekaman, dan opsi `Save As` setiap mulai merekam;
- penyimpanan otomatis yang rapi ke `Videos\Clario Presenter` dengan nama file bertimestamp;
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
2. Pilih monitor yang akan direkam pada pemilih sumber di sebelah mode. Monitor ini berbeda dari monitor output.
3. Tekan `Privat` dan centang PDF atau jendela catatan yang tidak boleh terlihat. Daftar privat dikunci selama sesi; pilih sebelum memulai.
4. Untuk presentasi, pilih monitor output yang berbeda lalu tekan `Mulai Live`. Clario dan jendela privat yang sedang terbuka otomatis diminimalkan, lalu aplikasi publik langsung diteruskan.
5. Ketika Clario/jendela privat dibuka, Clario memperbarui jendela publik di atas desktop aman terakhir. Ukuran desktop, posisi dialog, dan taskbar tetap sama; capture publik tidak dimulai ulang.
6. Aplikasi privat boleh tetap terbuka di samping Cisco/Canva/web pada laptop; output klien hanya memperbarui jendela publik. Minimalkan atau tutup aplikasi privat untuk kembali ke mirror desktop langsung.

### Merekam satu dari dua monitor sambil membuka aplikasi privat

1. Pilih `Mirror Aman`, lalu pilih **monitor sumber** yang ingin direkam.
2. Centang **Rekam saja**. Monitor lainnya tetap dapat dipakai untuk catatan/kontrol tanpa dijadikan output fullscreen.
3. Pilih aplikasi yang disembunyikan lewat **Privat**, lalu tekan **Siapkan rekaman**.
4. Aktifkan aplikasi publik yang akan direkam. Tekan **Rekam** setelah preview bersih sudah sesuai.
5. Berpindahlah antar aplikasi publik dan privat seperti biasa. Aplikasi publik yang tersedia terus diperbarui; membuka aplikasi privat tidak mengaktifkan tombol Privacy dan tidak mengganti video dengan layar jeda.
6. Tekan **REC** untuk menyimpan MP4, lalu **Stop** untuk mengakhiri sesi.

Tombol **Privacy** tetap merupakan tindakan manual yang sengaja menampilkan layar jeda. Saat hanya ingin membuka catatan tersembunyi, cukup pilih catatan itu sebagai **Privat** sebelum sesi. Biarkan aplikasi publik tetap terbuka; sebagian aplikasi/driver Windows berhenti mengirim frame saat diminimalkan atau ditutup, sehingga frame terakhir dipertahankan jika tidak ada pembaruan. Clario mencoba memulihkan aplikasi publik terakhir yang diminimalkan tanpa mengambil fokus. Komposisi menyimpan hingga delapan jendela publik aktif dan mempertahankan cache singkat untuk mengurangi beban GPU.

Mirror Aman selalu menggunakan `Seluruh layar` tanpa zoom/crop agar perubahan ke Live Terlindungi tidak mengubah tata letak. Untuk mode aplikasi tunggal, `Adaptif nyaman` dan `Isi layar (crop)` tetap tersedia jika pembesaran lebih penting daripada tepi sumber. Monitor dengan rasio berbeda memakai letterbox, bukan memotong isi. Jika monitor menyediakan data raw DPI, ukuran perkiraannya tampil di pemilih monitor. Privasi dipilih per jendela, bukan per tab Chrome; tempatkan tab catatan di jendela Chrome tersendiri jika tab lain perlu dipresentasikan.

Untuk merekam, mulai sesi Live lalu tekan `Rekam` dan lanjutkan presentasi seperti biasa. Secara default video langsung disimpan ke `Videos\Clario Presenter`; lokasi ini dapat dibuka atau diganti melalui tombol roda gigi. Aktifkan `Tanya lokasi setiap mulai merekam` jika ingin memakai dialog `Save As`. Tekan `REC` sekali lagi untuk menghentikan dan memfinalisasi video. Jangan menutup aplikasi sebelum teks tombol kembali menjadi `Rekam`. Versi saat ini merekam video output klien; audio mikrofon dan audio sistem belum disertakan.

### Rekam layar dan kamera

1. Hubungkan webcam USB, atau jalankan aplikasi kamera pihak ketiga dan aktifkan output kamera virtualnya.
2. Buka Settings (roda gigi), tekan `Muat ulang`, lalu pilih perangkat pada `Perangkat kamera`.
3. Tekan `Uji kamera` untuk memeriksa gambar. Jika Windows meminta izin kamera, berikan izin agar perangkat dapat digunakan.
4. Aktifkan `Sertakan kamera dalam rekaman`, pilih posisi/ukuran, dan tekan `Simpan`. Pratinjau berhenti dan kamera dinyalakan kembali saat REC dimulai.
5. Mulai Live kemudian tekan `Rekam`. Video MP4 berisi layar bersih ditambah kamera; monitor klien tidak mendapatkan overlay kamera.

Output video tetap 1080p 30/60 fps sesuai Settings; kelancaran kamera mengikuti frame rate yang diberikan perangkat/aplikasi kamera. Kamera 4:3 atau vertikal dipaskan tanpa meregangkan atau memotong wajah. Kamera ditempatkan di dalam area aman agar taskbar tidak tertutup. Saat Privacy atau sumber layar terputus, kamera juga disembunyikan dari video. Freeze membekukan layar; kamera tetap bergerak. Suara belum disertakan.

Clario menerima perangkat video yang bisa dibuka oleh Windows MediaCapture. Aplikasi yang hanya menampilkan preview kamera tanpa menyediakan perangkat kamera virtual belum dapat dipilih sebagai input kamera. Bila perangkat tidak muncul, aktifkan output virtualnya terlebih dahulu lalu muat ulang daftar; kompatibilitas perlu diuji pada driver yang dipakai.

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

## Pengembangan berikutnya

1. Shortcut global untuk Freeze, Privacy, dan fokus kembali ke sumber.
2. Pemilih PDF referensi dan daftar cue per bagian demo.
3. Penyimpanan sesi catatan per klien/proyek.
4. Installer MSIX bertanda tangan dan alur pembaruan aplikasi.
5. Telemetri performa lokal untuk mendeteksi frame drop tanpa merekam isi layar.
6. Pilihan audio mikrofon dan audio sistem untuk rekaman presentasi.
