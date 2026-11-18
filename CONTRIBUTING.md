# Berkontribusi ke Clario Presenter

Jika Anda menemukan masalah saat menggunakan Clario, jelaskan apa yang terjadi di laptop, di layar audiens, atau di file MP4. Untuk usulan fitur, ceritakan kegiatan yang ingin Anda selesaikan. [Buka formulir laporan dan usulan](https://github.com/msaririzki/Clario-Presenter/issues/new/choose).

## Menjalankan proyek

Gunakan Windows x64, .NET 10 SDK, dan akses ke NuGet. Jalankan perintah berikut dari folder utama repositori.

```powershell
dotnet build Clario.Presenter.csproj -p:Platform=x64
dotnet run --project Clario.Presenter.csproj --no-build -p:Platform=x64
```

## Memeriksa perubahan

```powershell
dotnet run --project Tests/Clario.GeometryTests.csproj -c Release
```

Pengujian ini memeriksa susunan jendela, geometri kamera, serta penempatan dan isi catatan. GitHub Actions menjalankan build Windows x64 dan pengujian tersebut untuk setiap pull request ke `main`. Uji yang memerlukan monitor kedua, kamera, atau mikrofon dapat dijalankan di perangkat pengembang. [Arsitektur, uji perangkat, dan pembuatan installer](docs/pengembangan.md).

## Mengirim pull request

Buat branch untuk perubahan Anda. Jelaskan perilaku yang berubah dan cara Anda mengujinya. Untuk masalah yang bergantung pada monitor, sertakan mode **Rekam** atau **Presentasi**, sumber yang dipilih, dan skala tampilan Windows. Untuk perubahan privasi, gunakan jendela contoh saat memeriksa hasil di layar audiens atau MP4.
