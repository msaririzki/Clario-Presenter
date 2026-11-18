# Panduan Clario Presenter

[Pemasangan](#pemasangan) · [Rekam layar](#rekam-layar) · [Presentasi dua layar](#presentasi-dua-layar) · [Catatan pribadi](#catatan-pribadi) · [Kamera dan suara](#kamera-dan-suara)

## Pemasangan

1. Unduh **Clario-Presenter-Setup-x64.exe** dari [halaman unduhan](https://github.com/msaririzki/Clario-Presenter/releases/latest).
2. Buka file yang sudah diunduh. Klik **Pasang**, lalu **Selesai**.
3. Buka menu **Mulai** di Windows dan cari **Clario Presenter**.

Clario dapat dipasang di PC Intel atau AMD 64-bit dengan Windows 10 versi 2004 (build 19041) ke atas atau Windows 11. Installer sudah menyertakan komponen yang dibutuhkan.

<details>
<summary>Mengapa Windows menampilkan peringatan SmartScreen?</summary>

Installer belum ditandatangani dengan sertifikat penerbit. Untuk memeriksa apakah file Anda sama dengan yang diterbitkan di GitHub, buka PowerShell di folder unduhan dan jalankan perintah berikut. Cocokkan hasilnya dengan kode dalam file `SHA256SUMS.txt` dari rilis yang sama.

```powershell
Get-FileHash .\Clario-Presenter-Setup-x64.exe -Algorithm SHA256
```

</details>

Untuk memperbarui aplikasi, selesaikan rekaman, tutup Clario, lalu jalankan installer versi terbaru. Untuk menghapus Clario, buka **Pengaturan Windows → Aplikasi**, pilih **Clario Presenter**, lalu **Hapus instalan**. Video Anda tetap tersimpan di folder rekaman.

## Rekam layar

1. Pilih **Rekam**. Pilih **Layar** untuk merekam satu monitor beserta jendela yang Anda buka di sana. Pilih **Aplikasi** jika hanya satu jendela yang ingin Anda rekam.
2. Pilih monitor atau jendelanya. Jika memakai **Layar**, buka **Sembunyikan aplikasi**, centang jendela pribadi seperti PDF berisi naskah, lalu klik **Terapkan**. Untuk satu tab Chrome, seret tab itu ke jendela baru dahulu, lalu centang jendela baru tersebut.
3. Klik **Buka preview** untuk melihat gambar yang akan masuk ke video. Jika kamera diaktifkan di **Pengaturan**, gambarnya tampil di posisi yang sudah Anda pilih. Lihat indikator **Suara komputer** dan **Mikrofon** untuk memeriksa audio. Kedua indikator hanya terlihat di panel Clario. Setelah siap, klik **Mulai rekam**.
4. Saat rekaman dimulai, Clario tetap terbuka dan preview langsung terlihat. Jika Anda mengecilkan jendela, preview dijeda untuk menghemat daya. Buka kembali Clario dari bilah tugas dan preview akan muncul lagi. Ini juga berlaku saat mengecek preview sebelum merekam. Jika Anda memilih **Tutup preview** saat merekam, preview tetap tertutup sampai Anda membukanya sendiri.
5. Setelah **Stop rekam**, klik **Putar rekaman** untuk menonton video atau **Buka folder** untuk melihat filenya. Biasanya video tersimpan di **Video → Clario Presenter**. Anda bisa mengganti folder atau memilih lokasi setiap kali merekam lewat **Pengaturan**.

Pintasan catatan hanya mengendalikan catatan. Di mode **Rekam**, gunakan **Stop rekam** untuk mengakhiri video. Di mode **Presentasi**, tombol **Esc** saat jendela audiens sedang aktif menutup layar audiens. Rekaman tetap berjalan sampai Anda klik **Stop rekam**. Untuk menutup Clario, hentikan rekaman terlebih dahulu.

Untuk demo menu Start, perpindahan aplikasi dengan **Alt+Tab**, atau perpindahan tab browser dengan **Ctrl+Tab**, pilih **Layar**. Jika yang Anda perlukan hanya catatan pribadi, gunakan **Catatan → Sematkan** dan biarkan daftar **Sembunyikan aplikasi** kosong. Jika Anda menyembunyikan jendela lain, Clario sementara melindungi pemilih aplikasi dan thumbnail bilah tugas yang bisa memperlihatkan isinya.

Saat Anda membuka jendela yang disembunyikan, Clario tetap menampilkan materi di belakangnya. Jika aplikasi yang ditampilkan belum mengirim gambar baru, gambar terakhir bisa bertahan sejenak.

**Contoh saat mengerjakan soal Cisco.** Pilih **Layar** dan letakkan Packet Tracer di monitor itu. Buka catatan perintah di Clario lalu klik **Sematkan**. Sekarang Anda bisa membuka jendela router, mengetik perintah, lalu membuka jendela switch. Dialog yang Anda buka di monitor itu ikut masuk ke video. Jika naskahnya berupa PDF, buka di aplikasi pembaca PDF dan sembunyikan jendelanya pada langkah kedua. [Contoh catatan router dan switch](../Examples/Praktik-Cisco.md).

## Presentasi dua layar

1. Sambungkan proyektor atau monitor kedua. Tekan **Win+P** di Windows dan pilih **Extend (Perluas)**.
2. Di Clario, pilih **Presentasi**. Untuk demo Canva atau situs web, pilih **Aplikasi** lalu pilih jendela browser. Jika Anda akan berpindah aplikasi, pilih **Layar**, tentukan layar laptop, lalu pilih jendela yang ingin disembunyikan.
3. Di **Layar audiens**, pilih proyektor. Klik **Mulai presentasi**. Anda tetap mengoperasikan materi di jendela aslinya di laptop.
4. Untuk menyimpan video sambil presentasi, klik **Rekam**. Klik **Stop rekam** untuk menyimpan video dan meneruskan presentasi. **Stop presentasi** mengakhiri sesi sekaligus menyimpan rekaman yang masih berjalan.

| Tombol | Kapan digunakan |
| --- | --- |
| **Freeze** | Saat kelas perlu waktu untuk membaca. Gambar di proyektor bertahan pada tampilan terakhir. Suara dan webcam yang aktif tetap berjalan di rekaman. |
| **Privasi** | Saat ingin jeda. Proyektor menampilkan pesan jeda. Selama tombol ini aktif, suara rekaman dibisukan dan wajah dari webcam disembunyikan dari video. |

**Jika proyektor dan laptop berbeda ukuran**, sumber **Layar** akan menyesuaikan gambar agar seluruh layar terlihat. Jika Anda memakai sumber **Aplikasi** dan tepi jendela terpotong, pilih **Opsi tampilan → Ukuran gambar → Seluruh gambar**. Pilihan **Perbesar** dan **Isi layar** bisa memotong tepi.

## Catatan pribadi

Buka **Catatan** untuk mengetik atau menempelkan naskah. Untuk naskah yang sudah disiapkan, klik **Buka file**. Clario menerima file teks (`.txt`) dan Markdown (`.md` atau `.markdown`) hingga 200.000 karakter. Untuk catatan yang disiapkan sendiri atau dengan bantuan AI, simpan sebagai Markdown UTF-8 dan awali tiap langkah dengan judul seperti `## 1. Periksa router`. Tombol **Daftar** di panel melayang bisa langsung membuka langkah yang dipilih.

Klik **Sematkan** agar naskah terlihat di atas aplikasi. Semua bagian tersambung seperti satu dokumen. Saat panel diperlebar, lebih banyak kata muat di tiap baris. Saat panel ditinggikan, baris dan bagian berikutnya langsung terlihat.

- **Untuk membaca**, klik **↓ Turun** atau **↑ Naik** di bawah catatan. Arahkan penunjuk ke bilah tombol lalu gulir roda mouse untuk bergerak sedikit demi sedikit. Dengan **Klik tembus** aktif, Anda tetap bisa mengeklik dan mengetik di jendela router di balik teks.
- **Untuk memilih langkah tertentu**, klik **Daftar**, lalu klik judulnya. Tombol ini muncul jika naskah memiliki beberapa bagian.
- **Untuk memindahkan atau memperbesar panel**, klik **Atur**. Seret judul untuk memindahkan panel, atau tarik tepi dan sudutnya untuk mengubah ukuran. Klik **Selesai** untuk kembali mengoperasikan aplikasi di belakang teks. Ukuran huruf dan transparansi tersedia di **Atur catatan**.

<details>
<summary>Pintasan keyboard (opsional)</summary>

| Pintasan | Fungsi pada catatan |
| --- | --- |
| **Ctrl+Shift+F8** | Tampilkan atau sembunyikan catatan |
| **Ctrl+Shift+F9** | Aktifkan atau matikan Klik tembus |
| **Ctrl+Shift+F10 / F11** | Bagian sebelumnya / berikutnya |
| **Ctrl+Shift+↑ / ↓** | Gulir sedikit ke atas / bawah |
| **Ctrl+Shift+Page Up / Page Down** | Gulir hampir satu halaman |
| **Ctrl+Shift+Home / End** | Lompat ke awal / akhir naskah atau daftar yang sedang terbuka |

</details>

Clario memeriksa apakah Windows dapat menyembunyikan catatan dari rekamannya. Jika pemeriksaan gagal, Clario mencoba memindahkan panel ke monitor lain yang aman atau menyembunyikannya. Jika Anda menggunakan perekam lain, periksa hasilnya dengan rekaman singkat di aplikasi tersebut.

## Kamera dan suara

**Untuk menampilkan wajah di video**, sambungkan webcam USB atau gunakan kamera laptop. Buka **Pengaturan**, aktifkan **Sertakan kamera**, pilih kamera, lalu klik **Tes kamera**. Pilih posisi dan ukuran kamera. Untuk menentukan ukuran sendiri, pilih **Kustom** dan masukkan 5–90% dari lebar video. Klik **Simpan**. Jika memakai aplikasi kamera pihak ketiga, aktifkan kamera virtual di aplikasi tersebut, lalu perbarui daftar kamera di Clario.

**Untuk memilih suara**, buka **Pengaturan → Audio**. Suara komputer dan mikrofon sama-sama mati saat pertama dipasang.

Jika dengung kipas ikut terdengar, aktifkan **Redam suara kipas** di bagian **Mikrofon**. Clario meredam suara latar sebelum menghitung level mikrofon dan menyimpannya ke video. Meter membaca kekuatan rata-rata suara dalam potongan pendek dan tetap memberi peringatan saat puncaknya mendekati batas.

| Suara yang diinginkan | Yang perlu diaktifkan |
| --- | --- |
| Hanya suara Anda | Aktifkan **Mikrofon**. Pilih mikrofon laptop, USB, atau mixer yang muncul di daftar. |
| Hanya suara video atau musik | Aktifkan **Suara komputer**. Pilih speaker atau headphone tempat suara diputar. |
| Suara Anda dan suara video | Aktifkan **Mikrofon** dan **Suara komputer**. Keduanya disimpan bersama dalam satu video. |

Untuk narasi, pilih **Profil suara mikrofon → Narasi (Windows)**. Windows meminta pemrosesan suara yang tersedia pada driver mikrofon. Jika karakter suara dari mikrofon atau mixer USB lebih cocok dengan pengaturan biasanya, pilih **Standar perangkat**. Kedua pilihan memakai mikrofon yang Anda pilih.

Jika headset terdengar pelan, pilih input headset di **Pengaturan → Audio**, lalu aktifkan **Perkuat mic pelan** dan klik **Simpan**. Clario mengingat pilihan ini untuk input tersebut, termasuk saat input yang sama dipilih lewat **Otomatis**. Jika headset dan mikrofon laptop memakai satu nama input Windows, keduanya berbagi pilihan ini.

Pilih **Narasi hangat** jika ingin suara lebih berisi dan lembut. Clario memakai input standar perangkat, memperkuat nada rendah suara secara ringan, dan meratakan bagian yang keras. Suku kata pelan setelah jeda tetap diproses. Efek hanya diterapkan pada mikrofon. Mulailah bicara saat **Stop rekam** muncul. Pada titik itu Clario sudah menerima data dari mikrofon.

Klik **Perbarui perangkat audio** jika perangkat baru belum muncul. Saat **Suara komputer** aktif, semua bunyi dari speaker atau headphone yang dipilih bisa masuk ke video, termasuk notifikasi atau suara dari aplikasi yang gambarnya disembunyikan.

Video disimpan sebagai MP4 Full HD (1920×1080). Di **Pengaturan → Video**, pilih **30 fps** untuk meringankan kerja komputer atau **60 fps** untuk gerakan yang lebih halus. Kelancaran hasil juga bergantung pada komputer dan kamera yang dipakai.

Jika ada kendala, ceritakan kejadian yang Anda lihat di laptop, proyektor, atau video dan sertakan versi Clario serta Windows yang dipakai. [Laporkan masalah](https://github.com/msaririzki/Clario-Presenter/issues/new?template=bug.yml).
