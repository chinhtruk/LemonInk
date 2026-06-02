# Kiem tra nghiem thu AI pipeline LemonInk

## Muc tieu

Xac nhan pipeline sach ca nhan khong luu ket qua rong hoac kem chat luong, audio
doc du noi dung, retry co the tiep tuc tu phan da tao, va admin hien dung suc
khoe van hanh AI.

## Chuan bi

- Chay ung dung tai `http://localhost:3000`.
- Dang nhap mot tai khoan ban doc va mot tai khoan quan tri.
- Dam bao khoa Gemini va cac model dang con quota su dung.
- Mo san `/Admin/ProcessingJobs` va `/Admin/AiHealth` bang tai khoan quan tri.

## Ca A: PDF co the copy chu

1. Tai len mot file PDF co noi dung van ban ro rang.
2. Theo doi card sach den khi xu ly xong.
3. Mo trang doc va nghe het audio.

Ket qua mong doi:

- Trang thai di qua trich xuat, tao tom tat, tao audio va san sang.
- Card hien phan tram va tien do audio theo tung doan.
- Gioi thieu nhanh tren trang sach chi gom 2-3 cau.
- Ban tom tat co it nhat mot chuong va mot y chinh co nghia.
- Audio mo dau bang ten sach va tac gia, sau do doc phan tong quan va cac chuong.
- Trang AI Health ghi nhan cac job va ty le thanh cong trong 24 gio.

## Ca B: PDF anh can OCR

1. Tai len mot PDF duoc tao tu anh scan co chu de doc.
2. Neu OCR thanh cong, kiem tra tom tat va audio nhu ca A.
3. Tai len mot ban scan qua mo hoac khong co noi dung doc duoc.

Ket qua mong doi:

- Ban scan ro tao duoc noi dung co nghia.
- Ban scan kem chat luong dung pipeline voi thong bao noi dung trich xuat khong
  du ro, thay vi tao tom tat hoac audio sai.

## Ca C: Retry va resume audio

1. Tai len tai lieu du dai de audio gom nhieu doan.
2. Trong khi tao audio, gay loi provider tam thoi hoac khoi dong lai ung dung.
3. Bam retry neu job hien loi co the thu lai.

Ket qua mong doi:

- Trang thai hien dang hoan tat doan `x/y`.
- Khi provider bi gioi han, tien do khong quay lai tu dau.
- Lan chay tiep theo co the khoi phuc cac doan audio da tao.
- File audio sau cung day du va co thoi luong hop ly so voi ban tom tat.

## Ca D: LemonAI

1. Tai trang doc, mo LemonAI.
2. Hoi ten tac gia, so chuong va noi dung cua mot chuong cu the.
3. Thu khi provider het quota hoac tam thoi khong san sang.

Ket qua mong doi:

- Cau tra loi dung theo du lieu sach va ten chuong nguoi dung thay tren UI.
- Khong hien citation noi bo sai hoac tra loi tu fallback context gia.
- Neu provider loi, giao dien bao chua the tra loi thay vi bia ra noi dung.

## Kiem tra persistence

1. Reload trang sach ca nhan, trang doc va AI Health.
2. Khoi dong lai ung dung sau khi mot sach da xu ly xong.

Ket qua mong doi:

- Trang thai job, audio va du lieu telemetry AI van duoc doc lai tu database.
- Sach san sang van mo va phat audio binh thuong.

## Luu bang chung cho portfolio

Ghi lai ngay thu, ten file thu, ket qua tung ca, anh chup trang job va AI
Health. Khi dua len GitHub, day la bang chung rang tinh nang AI da duoc kiem
tra nhu mot san pham, khong chi la demo giao dien.
