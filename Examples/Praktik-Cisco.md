# Contoh catatan praktik Cisco

## 1 — Router: periksa antarmuka

**Narasi:** Saya akan memeriksa alamat IP dan status antarmuka router.

**Perintah:**

```text
show ip interface brief
```

**Cek hasil:** Cocokkan alamat IP dengan topologi dan periksa kolom `Status` serta `Protocol`.

## 2 — Switch: periksa VLAN

**Narasi:** Saya akan memeriksa VLAN yang tersedia dan port yang terhubung ke setiap VLAN.

**Perintah:**

```text
show vlan brief
```

**Cek hasil:**

- Cocokkan VLAN yang tampil dengan soal.
- Periksa apakah masing-masing port masuk ke VLAN yang benar.
