# Rencana implementasi UI/UX Clario Presenter

## Alur utama

Orang yang baru membuka Clario perlu bisa menjawab tiga hal dengan sekali lihat. Apa yang sedang saya siapkan, layar atau aplikasi mana yang dipilih, dan tombol mana yang memulai sesi. Gunakan **Rekam** sebagai mode awal. Dengan layar utama sudah terpilih, **Mulai rekam** langsung membuka sesi dan memulai video. **Lihat pratinjau** menjadi pilihan saat pengguna mau memeriksa komposisi lebih dahulu.

### Tampilan saat menyiapkan rekaman

```text
Clario Presenter                                                                     Siap

Mode                     Sumber                Pilih layar
[ Rekam | Presentasi ]   [ Layar | Aplikasi ]   [ Layar 1 (utama) ▾ ]   [Pengaturan] [Lihat pratinjau] [Mulai rekam]

Pratinjau · Layar 1 (utama)                                             [Sembunyikan aplikasi] [Catatan]
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                             Gambar terpilih atau petunjuk sebelum mulai.                             │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Pada jendela lebar, mode dan sumber berada dalam satu baris di kiri, sedangkan aksi utama dan **Pengaturan** berada di kanan. **Sembunyikan aplikasi** dan **Catatan** ditempatkan di baris pratinjau. Pratinjau memakai ruang yang tersisa.

### Tampilan saat sesi berjalan

- Ringkas pemilih menjadi informasi sumber, misalnya **Layar 1 (utama)** atau **Cisco Packet Tracer**. Dalam mode Presentasi, tampilkan tujuan, misalnya **Layar 1 → Layar 2**.
- Di tempat yang sama dengan tombol mulai, tampilkan **Stop rekam** atau **Stop presentasi**. Letakkan waktu di sebelah status **Sedang merekam**, sehingga label dan lebar tombol stabil.
- **Freeze** dan **Privasi** muncul di bilah sesi. Saat Freeze aktif, beri aksi **Lanjutkan tampilan**. Saat Privasi aktif, pertahankan indikator dan label yang jelas, dengan aksi **Matikan privasi**. Selama video berjalan, status seperti **Sedang merekam · Privasi aktif** menjelaskan bahwa video masih berlangsung.
- Dalam Presentasi, tombol sekunder **Rekam** memulai video. **Stop rekam** menyimpan video sambil meneruskan presentasi. **Stop presentasi** mengakhiri sesi dan menyelesaikan video yang sedang disimpan.
- Sediakan satu area umpan balik untuk kondisi **Menyiapkan…**, **Menyimpan…**, **Rekaman tersimpan**, dan kegagalan. Setelah tersimpan, tampilkan nama file dan aksi **Buka folder**. Saat simpan gagal, tampilkan penyebab dan tindakan yang sesuai dengan kondisi file.
- Biarkan jendela terbuka saat rekaman dimulai. Jeda preview saat pengguna mengecilkan Clario dan lanjutkan saat jendela dibuka kembali.

## Pemilih, panel, dan pengaturan

| Kontrol | Rancangan dan perilaku |
| --- | --- |
| **Rekam / Presentasi** | Dua pilihan sejajar seperti tombol segmen, memakai pemilihan tunggal `RadioButtons` WinUI. Pilihan aktif memiliki latar, batas, dan indikator yang terlihat saat navigasi keyboard. |
| **Layar / Aplikasi** | Dua pilihan sejajar di bawah label **Sumber**. Pemilih di sebelahnya berganti sesuai pilihan. Mengganti jenis sumber mempertahankan pilihan terakhir masing-masing selama jendela atau layar itu tersedia. |
| **Pilih layar** | `ComboBox` dengan label ringkas seperti **Layar 1 (utama)**. Resolusi dan skala tampil sebagai informasi tambahan di daftar saat dibuka. Jika layar yang dipilih terputus, minta pengguna memilih lagi sebelum memulai. |
| **Pilih aplikasi** | `ComboBox` menampilkan judul jendela. Nama aplikasi membantu membedakan judul yang sama di daftar. Muat ulang daftar saat dibuka sambil mempertahankan pilihan yang valid. Saat daftar kosong, tampilkan ajakan membuka aplikasi dan tombol **Perbarui daftar**. |
| **Layar audiens** | Hanya tampil di Presentasi dan diletakkan di kelompok sumber. Untuk sumber Layar, tampilkan layar tujuan yang berbeda. Jika belum tersedia, jelaskan pilihan untuk menyambungkan layar atau beralih ke Rekam. |
| **Ukuran gambar** | Pindahkan ke **Opsi tampilan** untuk sumber Aplikasi. **Seluruh gambar** menjadi pilihan awal. Pilihan **Perbesar** dan **Isi layar** menjelaskan kemungkinan bagian tepi terpotong. Sumber Layar memakai tampilan utuh. |
| **Sembunyikan aplikasi** | Buka daftar jendela dengan centang, jumlah yang dipilih, dan aksi **Terapkan** serta **Batal**. Simpan pilihan sebelumnya saat Batal atau Esc. Tampilkan tombol ini saat menyiapkan sumber Layar. Untuk beberapa tab Chrome, panduan ringkas menjelaskan bahwa pemilihan berlaku per jendela. |
| **Catatan** | Panel terbuka sesuai kebutuhan. Tampilkan editor, **Buka file**, dan **Sematkan**. Simpan transparansi, ukuran teks, dan pengaturan klik tembus di **Atur catatan**. Saat sesi dimulai, panel editor menutup dan catatan yang disematkan tetap terlihat oleh presenter. |
| **Pengaturan** | Gunakan dialog sama dengan tiga bagian **Kamera**, **Video**, dan **Penyimpanan**. Saat kamera mati, tampilkan sakelar dan ringkasan. Saat dinyalakan, tampilkan perangkat, tes kamera, dan posisi. Pilihan folder dan kualitas mengikuti setelan pengguna yang sudah tersimpan. |

Pilihan dua arah tetap terlihat. Daftar layar dan aplikasi memakai *dropdown* karena isinya berubah sesuai perangkat dan jendela yang terbuka. [Panduan ComboBox WinUI](https://learn.microsoft.com/windows/apps/develop/ui/controls/combo-box).

## Warna, bentuk, dan tipografi

Gunakan abu kebiruan gelap untuk area kerja, tombol utama terang, dan ungu lembut untuk fokus serta pilihan aktif. Hijau menandai presentasi, merah muda menandai rekaman, dan kuning menandai Freeze. Setiap status disertai label sehingga tetap dapat dipahami secara monokrom. Pertahankan karakter Clario dengan permukaan datar, satu tingkat garis pemisah, dan radius yang konsisten.

| Elemen | Warna usulan | Kontras yang dihitung |
| --- | --- | --- |
| Kanvas, panel, dan kontrol | `#0E1116`, `#151922`, `#1B202A` | |
| Teks utama / teks bantu pada panel | `#F3F5F8` / `#AAB4C2` | 16,10:1 / 8,39:1 |
| Garis pemisah dekoratif / batas kontrol aktif | `#2A313E` / `#657186` | Batas kontrol 3,31:1 pada `#1B202A` |
| Fokus keyboard dan pilihan aktif | Fokus `#A5A1F5`. Pilihan `#C7C5FF` pada `#292747` | 7,01:1 pada kontrol gelap dan 8,72:1 untuk label pilihan |
| Aksi utama biasa / sorot / tekan | Teks `#0E1116` pada `#F3F5F8` / `#E4E8EE` / `#CDD5E1` | 17,32:1 / 15,38:1 / 12,79:1 |
| Aksi utama nonaktif | `#AAB4C2` pada `#262D37` | 6,62:1 |
| Merekam dan Stop rekam | `#FFB2BE` pada `#3D2028`. Sorot `#502A34`, tekan `#63303D` | 8,61:1 / 7,18:1 / 6,12:1 |
| Presentasi aktif / Freeze / Privasi | `#49C49A` pada `#15372F` / `#E9B85B` pada `#3A2E19` / `#C7C5FF` pada `#292747` | 5,96:1 / 7,24:1 / 8,72:1 |

- Gunakan Segoe UI Variable dengan Segoe UI sebagai cadangan. Label dan tombol 14, informasi pendukung 12–13, dan judul panel 18–20. Pakai huruf tebal secukupnya untuk aksi utama dan judul.
- Gunakan tinggi kontrol minimal 40 unit logis, radius 8 untuk tombol dan pemilih, radius 12 untuk panel, serta jarak 8/12/16/24. Pakai ikon Fluent yang sudah tersedia untuk kontrol yang dibantu simbol, seperti tutup dan perbarui.
- Definisikan warna latar dan teks berpasangan untuk kondisi normal, sorot, tekan, nonaktif, dan terpilih. Gunakan fokus dua warna pada kontrol yang dapat berlatar terang dan gelap. Mode Kontras Tinggi Windows memakai warna sistem.
- Pakai acuan 4,5:1 untuk teks biasa dan 3:1 untuk batas kontrol atau fokus yang diperlukan. Angka pada tabel adalah perhitungan warna usulan. Uji warna yang benar-benar dirender WinUI, termasuk saat label **Mulai rekam** berubah menjadi **Menyiapkan…** atau **Stop rekam**. [WCAG 2.2 tentang kontras dan fokus](https://www.w3.org/TR/WCAG22/).

## Tata letak saat jendela diperkecil

| Lebar area kerja | Penyusunan kontrol |
| --- | --- |
| 1.240 unit logis ke atas | Mode dan sumber di kiri serta aksi rekam di kanan, dalam satu baris. |
| 720–1.239 | Mode dan jenis sumber di baris pertama, dengan pemilih layar atau aplikasi di bawah. Aksi utama tetap terlihat di bilah bawah. |
| Di bawah 720 | Alat pratinjau pindah ke baris tersendiri. Aksi utama tetap terlihat di bilah bawah. |

Catatan terbuka sebagai panel kanan selebar sekitar 320 unit pada jendela dengan lebar minimal 1.080 unit. Di bawahnya, catatan terbuka sebagai panel sementara yang bisa ditutup dengan Esc.

Gunakan `Grid` dan `VisualStateManager` agar kontrol yang sama berpindah posisi saat ukuran berubah. Uji 1.366 × 768 dan 1.920 × 1.080 pada skala Windows 100%, 125%, dan 150%, serta kondisi jendela sekitar 560 × 360 unit logis. Uji pemindahan jendela antarmonitor dengan DPI berbeda. Nilai lebar awal di tabel dapat disesuaikan berdasarkan hasil uji. [Panduan tata letak responsif Windows](https://learn.microsoft.com/windows/apps/design/layout/responsive-design).

## Urutan implementasi

| Tahap | Pekerjaan | Hasil yang diperiksa |
| --- | --- | --- |
| 1. Sistem tampilan | Rapikan resource bersama di `App.xaml` dan gaya tombol utama yang saat ini dipakai `MainPage.xaml`. Siapkan gaya tombol, pemilih, pilihan aktif, fokus, dan pesan status dari kontrol native WinUI. | Label terbaca di semua kondisi, termasuk saat fokus berpindah dan ketika teks tombol berubah. |
| 2. Alur utama | Atur ulang `MainPage.xaml` dan pusatkan label, visibilitas, serta fokus berdasarkan kondisi sesi di `MainPage.xaml.cs`. Sambungkan kontrol baru ke handler rekam, pratinjau, dan presentasi yang sudah dipakai. | Rekaman dimulai sekali klik setelah sumber siap. Tombol stop tetap terlihat ketika jendela diperkecil. Pengguna dapat membedakan layar sumber dan layar audiens. |
| 3. Panel pendukung | Terapkan daftar jendela yang disembunyikan, rapikan Catatan, dan susun ulang `Settings/SettingsDialog.xaml`. Pertahankan format `.txt`, `.md`, serta penyimpanan pengaturan yang dipakai saat ini. | Pemilihan jendela mudah diperiksa sebelum mulai. Kamera, kualitas, dan folder mudah ditemukan. Panel catatan tetap aman saat dicoba bersama jendela Cisco. |
| 4. Verifikasi dan paket | Jalankan tes komposisi dan catatan. Pakai build Windows untuk menguji kontrol dengan keyboard dan mouse, lalu buat rekaman uji dan periksa hasilnya. Cek juga tampilan audiens pada dua monitor. | Build lama dapat di-upgrade dengan pengaturan yang tersimpan. Skenario di bawah lulus pada paket yang akan diberikan ke pengguna. |

### Skenario penerimaan

- Dari layar awal, pilih monitor, sembunyikan satu jendela, mulai rekam, pindah aplikasi, lalu stop. MP4 tersimpan dan aplikasi privat tidak tampak di frame.
- Buka pratinjau, tutup pratinjau, lalu mulai rekam. Membuka pratinjau saja tidak menghasilkan file. Pembatalan pemilih lokasi mengembalikan kontrol ke keadaan siap.
- Pilih Presentasi dengan dua monitor. Mulai sesi, aktifkan dan lepaskan Freeze serta Privasi, rekam video jika diperlukan, lalu akhiri sesi. Periksa pemilihan layar tujuan, ukuran gambar, dan posisi taskbar.
- Buka catatan `.md`, sematkan, lalu buka dialog router dan switch Cisco di monitor sumber. Pastikan panel catatan dapat dilewati klik dan tidak muncul dalam MP4 atau tampilan audiens.
- Uji Tab, Shift+Tab, tombol panah, Enter, dan Esc di pemilih dan panel. Pastikan indikator fokus terlihat, fokus kembali ke tombol pemicu saat dialog ditutup, dan fokus tidak memulai atau menghentikan sesi secara tak sengaja.
- Uji nama jendela dan folder panjang, perubahan jumlah monitor, ukuran jendela sempit, serta sorot/tekan/nonaktif pada setiap varian tombol. Label, tanda pilihan, dan tombol berhenti harus tetap dapat dibaca dan digunakan.
