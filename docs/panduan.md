# Panduan Clario Presenter

[Pemasangan](#pemasangan) · [Rekam layar](#rekam-layar) · [Presentasi dua layar](#presentasi-dua-layar) · [Catatan pribadi](#catatan-pribadi) · [Kamera dan suara](#kamera-dan-suara)

## Pemasangan

Unduh **Clario-Presenter-Setup-x64.exe**, buka installer, lalu klik **Pasang** dan **Selesai**. Aplikasi tersedia di menu **Mulai → Clario Presenter**. Runtime sudah termasuk di dalam installer. [Unduh dari GitHub Releases](https://github.com/msaririzki/Clario-Presenter/releases/latest).

Gunakan Windows 10 build 19041 atau lebih baru, atau Windows 11, pada PC Intel atau AMD x64. Installer v1.0.1 belum memiliki tanda tangan digital penerbit sehingga Windows dapat menampilkan peringatan SmartScreen. Untuk memeriksa integritas file, jalankan perintah berikut dari folder unduhan dan cocokkan hasilnya dengan `SHA256SUMS.txt` pada rilis yang sama.

```powershell
Get-FileHash .\Clario-Presenter-Setup-x64.exe -Algorithm SHA256
```

Untuk memperbarui, selesaikan rekaman, tutup Clario, lalu jalankan installer baru. Untuk menghapus aplikasi, buka **Pengaturan Windows → Aplikasi**, pilih **Clario Presenter**, lalu **Hapus instalan**. Video yang sudah disimpan tetap tersedia di folder rekaman.

## Rekam layar

1. Pilih mode **Rekam**. Gunakan sumber **Layar** untuk berpindah di antara beberapa aplikasi atau membuka dialog tambahan seperti konfigurasi router. Gunakan **Aplikasi** untuk merekam satu jendela tertentu.
2. Pilih monitor atau jendela yang ingin direkam. Untuk sumber **Layar**, buka **Sembunyikan aplikasi**, centang jendela pribadi, lalu klik **Terapkan**. Jika ingin menyembunyikan satu tab Chrome, pindahkan tab itu ke jendela Chrome tersendiri terlebih dahulu.
3. Klik **Lihat pratinjau** untuk memeriksa gambar sebelum mulai. Klik **Mulai rekam** saat siap. Jika Clario mengecil ke taskbar, klik ikonnya untuk membuka kontrol kembali.
4. Klik **Stop rekam**, lalu **Buka folder** untuk melihat MP4. Lokasi bawaan adalah folder **Video → Clario Presenter**. Di **Pengaturan**, Anda bisa mengganti folder atau meminta Clario menanyakan lokasi setiap kali merekam.

Untuk praktik dengan beberapa jendela, tempatkan jendela utama dan dialog yang ingin ditampilkan di monitor sumber yang sama. Lakukan rekaman singkat untuk memeriksa perpindahan jendela dan suara sebelum mulai mengerjakan materi.

## Presentasi dua layar

1. Sambungkan monitor kedua atau proyektor. Tekan **Win+P** lalu pilih **Extend (Perluas)**.
2. Di Clario, pilih **Presentasi**. Pilih sumber di laptop dan tetapkan proyektor sebagai **Layar audiens**. Untuk sumber **Layar**, terapkan pilihan jendela yang ingin disembunyikan.
3. Klik **Mulai presentasi** dan operasikan materi dari laptop. Klik **Rekam** jika Anda juga ingin menyimpan sesi. **Stop rekam** menyimpan video sambil meneruskan presentasi. **Stop presentasi** mengakhiri sesi dan menyimpan video yang sedang berjalan.

| Kontrol | Dampak pada presentasi dan rekaman |
| --- | --- |
| **Freeze** | Gambar layar bertahan pada frame terakhir. Jika sedang merekam, audio berlanjut dan kamera yang aktif tetap bergerak di video. |
| **Privasi** | Layar audiens menampilkan pesan jeda. Jika sedang merekam, audio dibisukan dan kamera disembunyikan dari video selama Privasi aktif. |

**Jika tepi tampilan terpotong**, periksa jenis sumber. Untuk sumber **Aplikasi**, pilih **Opsi tampilan → Ukuran gambar → Seluruh gambar**. Pilihan **Perbesar** dan **Isi layar** bisa memotong tepi. Sumber **Layar** selalu menampilkan seluruh gambar dengan proporsi aslinya.

## Catatan pribadi

Buka **Catatan** untuk mengetik atau menempelkan teks. **Buka file** menerima `.txt`, `.md`, dan `.markdown` hingga 200.000 karakter. Untuk catatan panjang, simpan sebagai Markdown UTF-8 dan awali setiap bagian dengan judul `##`. Tombol **Daftar** pada panel melayang membantu Anda langsung menuju bagian tertentu. [Contoh catatan siap pakai](../Examples/Praktik-Cisco.md).

Klik **Sematkan** untuk menampilkan panel di atas aplikasi. Saat **Klik tembus** aktif, kursor dapat mengoperasikan jendela di belakang panel. Tekan **Ctrl+Shift+F9** untuk mematikan Klik tembus ketika ingin memindahkan panel, menarik tepi atau sudutnya, menggunakan scrollbar, atau membuka **Daftar**. Lebar, tinggi, opasitas, dan ukuran teks juga tersedia melalui **Atur catatan**.

| Pintasan | Fungsi pada panel |
| --- | --- |
| **Ctrl+Shift+F8** | Tampilkan atau sembunyikan catatan |
| **Ctrl+Shift+F9** | Ganti mode Klik tembus |
| **Ctrl+Shift+F10 / F11** | Bagian sebelumnya / berikutnya |
| **Ctrl+Shift+↑ / ↓** | Gulir beberapa baris |
| **Ctrl+Shift+Page Up / Page Down** | Gulir hampir satu halaman |
| **Ctrl+Shift+Home / End** | Awal / akhir bagian |

Clario memeriksa apakah Windows dapat mengecualikan panel dari tangkapan. Jika pengecualian gagal, Clario mencoba memindahkan panel ke monitor lain yang aman atau menyembunyikannya. Jika memakai aplikasi perekam lain, periksa hasilnya lewat rekaman uji di aplikasi tersebut.

## Kamera dan suara

- **Webcam laptop, USB, atau virtual.** Di **Pengaturan**, aktifkan **Sertakan kamera dalam video**, pilih perangkat, lalu klik **Tes kamera**. Atur posisi, ukuran, dan opsi cermin, lalu klik **Simpan**. Kamera muncul di hasil rekaman. Untuk kamera virtual, aktifkan output kamera di aplikasi penyedia dan perbarui daftar di Clario.
- **Mikrofon.** Di bagian **Audio**, aktifkan **Mikrofon** dan pilih mikrofon laptop, USB, atau mixer yang terdaftar di Windows. Pakai **Perbarui perangkat audio** setelah menyambungkan perangkat baru.
- **Suara komputer.** Pilih perangkat keluaran yang memutar suara, misalnya speaker atau headphone. Semua aplikasi yang terdengar melalui keluaran itu dapat masuk ke video, termasuk aplikasi yang gambarnya disembunyikan. Pilih mikrofon saja jika Anda hanya ingin merekam narasi.
- **Keduanya sekaligus.** Aktifkan **Suara komputer** dan **Mikrofon** untuk mencampur keduanya dalam satu trek AAC stereo 48 kHz. Kedua pilihan mati pada pemasangan baru.

Video disimpan sebagai MP4 H.264 1920×1080. Pilih **30 fps** atau **60 fps** di **Pengaturan → Kelancaran**. Kelancaran kamera juga bergantung pada perangkat dan drivernya.

Masih ada kendala? Sertakan versi Clario dan Windows serta lokasi gejalanya, misalnya di laptop, proyektor, atau video. [Laporkan masalah](https://github.com/msaririzki/Clario-Presenter/issues/new?template=bug.yml).
