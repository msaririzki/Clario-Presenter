# Rencana dan operasi pembaruan Clario

## Alur pengguna

Mulai 1.0.17, salinan Windows x64 yang dipasang dengan installer memeriksa GitHub Releases saat Clario senggang. Pemeriksaan yang berhasil berlaku 24 jam. Pengguna juga dapat membuka **Pengaturan → Pembaruan → Cek sekarang**. **Nanti** menyembunyikan pemberitahuan selama sesi tersebut. Ringkasan dan catatan rilis tersedia di Pengaturan.

Clario menunda aktivitas pembaruan selama preview, presentasi, perekaman dan penyimpanan video. Setelah unduhan lengkap dan terverifikasi, **Pasang dan buka kembali** menyimpan pengaturan dan catatan, menunggu Clario ditutup, lalu menjalankan installer per pengguna. Catatan dipulihkan saat aplikasi dibuka kembali. Salinan pemulihan disimpan sementara di direktori Updates pengguna dan dienkripsi dengan perlindungan data Windows untuk akun tersebut. Posisi baca, pilihan Markdown, ukuran panel dan sematan dipulihkan melalui pemeriksaan privasi monitor yang biasa.

Build tanpa identitas penerbit terkonfigurasi menyediakan **Unduh pembaruan**, yang membuka halaman rilis resmi. Jika memakai tombol ini, tutup Clario secara normal setelah selesai merekam agar catatan ikut dipulihkan. Pengguna 1.0.16 atau lebih lama memasang versi pertama yang memiliki menu Pembaruan secara manual dan menyimpan catatan sebelum menutup versi lama. Untuk beralih dari build tanpa identitas penerbit ke pembaruan otomatis, pasang satu build bertanda tangan secara manual.

## Tahapan rilis

1. **Uji lokal.** Samakan versi proyek dan manifest. Jalankan pengujian komposisi dan pembaruan, bangun installer, lalu uji pengaturan, catatan dan sesi rekaman.
2. **Siapkan draf.** Setelah pengujian lokal diterima, buat tag yang sama persis dengan versi, misalnya `v1.0.17`. Workflow `release.yml` memvalidasi tag, menjalankan pengujian, membuat installer dan checksum, lalu mengunggah keduanya ke draf GitHub Release. Pengulangan job memperbarui aset draf. Workflow menolak mengganti aset rilis yang sudah terbit.
3. **Periksa kandidat.** Unduh installer dari draf, cocokkan SHA-256, dan uji upgrade dari versi yang sebelumnya dibagikan. Tulis ringkasan perubahan dalam bahasa Indonesia pada paragraf pertama catatan rilis.
4. **Terbitkan.** Pemilik repositori memilih **Publish release** dan menjadikannya latest stable. Aplikasi memeriksa endpoint rilis stabil dan menerima versi bernomor lebih tinggi dengan installer resmi x64. Untuk memperbaiki rilis bermasalah, terbitkan versi perbaikan dengan nomor lebih tinggi.

## Identitas penerbit

Untuk build bertanda tangan, siapkan environment GitHub bernama `release` dengan variabel `CLARIO_PUBLISHER_SHA256` berisi SHA-256 sertifikat publik (64 digit heksadesimal), serta secret `CLARIO_SIGNING_PFX_BASE64` dan `CLARIO_SIGNING_PASSWORD` untuk sertifikat code signing Windows yang dipercaya dan sesuai dengan kebijakan penyedia kunci. Jalur PFX memerlukan kunci yang memang boleh diekspor. Penyedia kunci perangkat keras atau berbasis layanan memerlukan integrasi penandatanganan khusus di runner. Kunci privat dan kata sandi disimpan di luar repositori. Konfigurasi sebagian menghentikan build.

Skrip build menerima `-SigningThumbprint` untuk memilih sertifikat dari store CurrentUser/My. Build menyematkan sidik jari SHA-256 sertifikat tersebut pada aplikasi, menandatangani aplikasi, skrip pembantu serta installer, dan menghitung checksum setelah penandatanganan. Updater memeriksa asal aset, ukuran, SHA-256, status Authenticode, penerbit dan versi installer. File dikunci selama pemeriksaan terakhir dan pemasangan. Saat sertifikat berganti, versi lama yang mempercayai sertifikat sebelumnya beralih ke pengunduhan manual sampai pengguna memasang build dengan identitas baru.

## Pemeriksaan sebelum publikasi

- Uji versi baru, versi sama, versi lama dan prarilis dengan feed simulasi. Periksa dua kali dalam satu hari dan ulangi dalam kondisi offline.
- Putuskan unduhan dan coba installer dengan isi, ukuran atau tanda tangan yang salah. Pastikan file lama tetap dapat dipakai dan Clario tetap terbuka.
- Mulai preview atau rekaman ketika pemeriksaan sedang berjalan. Periksa bahwa pembaruan ditunda dan **Stop rekam** masih berfungsi.
- Uji upgrade dengan dua versi bertanda tangan penerbit yang sama. Gunakan catatan Markdown, posisi gulir dan pengaturan mic yang mudah dikenali. Verifikasi bahwa aplikasi kembali dengan versi dan isi yang benar. Ulangi dengan pemasangan gagal.
