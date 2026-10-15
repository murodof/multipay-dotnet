# Windows Server 2016: .NET API Windows Service sifatida

Ilova Docker'siz, Kestrel'da `127.0.0.1:8010` da Windows Service bo‘lib ishlaydi; tashqi trafik IIS orqali HTTPS bilan keladi. `Multipay.xml` — WinSW service konfiguratsiyasi, `web.config` — IIS reverse proxy namunasi. Tuzilish Python servisning Windows yo‘riqnomasi bilan bir xil, faqat PostgreSQL o‘rnida SQL Server va Python o‘rnida tayyor `.exe` bor.

Versiyalarni tushirish kerak emas: .NET 10 Windows Server 2016 x64 ni qo‘llab-quvvatlaydi, paket esa self-contained — serverga .NET o‘rnatilmaydi.

> Bu yo‘riqnomadagi paket Linuxda yig‘ilib tekshirilgan (`.exe` fayllar hosil bo‘ladi), lekin Windows Server 2016 ning o‘zida hali sinab ko‘rilmagan. Birinchi o‘rnatishni sinov serverida qiling.

## 0. Paketni yig‘ish (ishlab chiquvchi kompyuterida)

.NET 10 SDK bor mashinada, loyiha ildizida:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\publish.ps1   # Windows
```

```bash
bash deploy/windows/publish.sh                                       # Linux / macOS
```

Natija: `dist/windows/Multipay-win-x64.zip`. Yig‘ish uchun SDK 10.0.113 yoki yangisi kerak (`global.json`): paket ichidagi runtime SDK bilan keladi, xavfsizlik yangilanishlari uchun har oy SDK yangilanib paket qayta yig‘iladi (SDK va NuGet paketlarini yangilash tartibi — loyiha ildizidagi `README.md`, "Xavfsizlik yangilanishlari" bo‘limi). Ichida:

| Fayl | Vazifasi |
| --- | --- |
| `PaymentService.exe` va DLL'lar | ilova (win-x64, .NET runtime bilan birga) |
| `migrate.exe` | baza migratsiyasi (EF bundle) |
| `.env.example` | sozlamalar namunasi |
| `deploy\windows\Multipay.xml`, `web.config`, `README.md` | service, IIS va shu yo‘riqnoma |

Zip'ni serverga ko‘chirib, `C:\Multipay` ga oching. Quyidagi buyruqlar Administrator PowerShell'da, `C:\Multipay` ichida bajariladi.

## 1. Server va ma'lumot xizmatlari

- Windows Server 2016 x64 to‘liq yangilangan bo‘lsin. Uning kengaytirilgan qo‘llab-quvvatlash muddati **2027-yil 12-yanvarda** tugaydi; undan keyin xavfsizlik yangilanishlari faqat pullik ESU orqali keladi. Imkon bo‘lsa Windows Server 2019/2022 tanlang — yo‘riqnoma ularda ham bir xil.
- Windows Server 2016 TLS 1.3 ni bilmaydi, eng yuqorisi TLS 1.2. SV-GATE, HUMO va Operator manzillari TLS 1.2 ni qabul qilishini oldindan tekshiring.
- SQL Server va Redis ishlashi kerak — shu serverda yoki tarmoqdagi boshqa serverda.
- Service uchun alohida Windows akkaunti tanlang (4-bo‘lim).

### Server soati

Operator hash'idagi `ts` server soatidan ±300 soniyadan ortiq farq qilsa, har bir sessiya `HASH_EXPIRED` bilan rad etiladi:

```powershell
w32tm /query /status
w32tm /resync
```

Domensiz serverda manbani qo‘lda belgilang:

```powershell
w32tm /config /manualpeerlist:"<ntp-server>" /syncfromflags:manual /update
Restart-Service w32time
w32tm /resync
```

### SQL Server

SQL Server **2016 yoki undan yangisi** kerak (sxema `ISJSON` ishlatadi); Windows Server 2016 ga SQL Server 2022 gacha o‘rnatiladi. Testlar SQL Server 2022 da yuradi — eskirog‘ini tanlasangiz, avval sinov bazasida 6-bo‘limdagi tekshiruvni o‘tkazing. Express nashri bazani 10 GB bilan cheklaydi.

O‘rnatishda **Mixed Mode** (SQL login) ni yoqing va TCP/IP protokolini SQL Server Configuration Manager'da yoqing. Bo‘sh baza va ilova uchun alohida login yarating (`sa` ni ilovaga bermang):

```powershell
sqlcmd -S 127.0.0.1 -E -Q "CREATE DATABASE payment; CREATE LOGIN payment WITH PASSWORD = '<kuchli-parol>'; ALTER AUTHORIZATION ON DATABASE::payment TO payment;"
Test-NetConnection 127.0.0.1 -Port 1433
```

Baza egasi bo‘lish migratsiya uchun kerak: u jadvallarni yaratadi va bazani `READ_COMMITTED_SNAPSHOT` ga o‘tkazadi. Ustunlar kollatsiyasi migratsiyada aniq yozilgan, server kollatsiyasini o‘zgartirish shart emas.

### Redis

Windows uchun rasmiy Redis yo‘q. Ikki yo‘l:

- **Alohida Linux serverdagi Redis 7** — tavsiya etiladi; ilova aynan shu bilan sinalgan.
- **Memurai** (Redis'ga mos Windows xizmati). Bepul Developer nashri production uchun emas va ma'lum vaqtdan keyin o‘zi to‘xtaydi — production'ga pullik nashr kerak. Limit va idempotentlik Lua skriptlarga tayanadi, shuning uchun Memurai bilan avval 6-bo‘limdagi sinovni to‘liq o‘tkazing.

```powershell
Get-Service -Name '*memurai*'
& 'C:\Program Files\Memurai\memurai-cli.exe' -h 127.0.0.1 -p 6379 ping
```

Nonce va idempotentlik kalitlari Redis'da turadi: AOF yoki RDB saqlashni yoqing, aks holda Redis ma'lumoti yo‘qolsa 5 daqiqalik oynadagi hash qayta ishlatilishi mumkin. `6379` va `1433` portlarini tashqi tarmoqqa ochmang.

## 2. Sozlamalar (`.env`)

```powershell
Set-Location C:\Multipay
Copy-Item .env.example .env
notepad .env
```

Kamida quyidagilarni haqiqiy qiymatlarga o‘zgartiring:

```dotenv
ENVIRONMENT=production
DATABASE_URL=Server=127.0.0.1,1433;Database=payment;User Id=payment;Password=<parol>;Encrypt=True;TrustServerCertificate=True
REDIS_URL=redis://127.0.0.1:6379/0
SECRET_KEYS=k1:<Operator bilan kelishilgan sir>
PAN_HASH_PEPPER=<alohida tasodifiy qiymat>
ALLOWED_ORIGINS=https://<operator-sayti>
FORWARDED_ALLOW_IPS=127.0.0.1
LOG_FILE=logs/payment.log
```

va SV-GATE, Operator (kerak bo‘lsa HUMO) rekvizitlarini. Eslatmalar:

- `DATABASE_URL` — SQLAlchemy URL emas, SQL Server ulanish satri. Parolda `;` bo‘lsa, parolni `{...}` ichiga oling.
- `ENVIRONMENT=production` da `ALLOWED_ORIGINS` ichida `*` yoki `http://localhost` tursa, ilova ishga tushmaydi.
- `.env` ilova ishchi papkasidan (`C:\Multipay`) o‘qiladi; Windows muhit o‘zgaruvchilari undan ustun.
- `DOCS_ENABLED=false` qolsin. Admin panel yoqilsa (`ADMIN_ENABLED=true`), `ADMIN_PASSWORD` 16+ va `ADMIN_SECRET_KEY` 32+ belgi, `ADMIN_COOKIE_SECURE=true` bo‘lsin.

Maxfiy qiymatlarni serverning o‘zida yarating, har biri alohida:

```powershell
function New-Secret { $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $b }
-join (New-Secret | ForEach-Object { $_.ToString('x2') })   # SECRET_KEYS=k1:<qiymat>, PAN_HASH_PEPPER, ADMIN_SECRET_KEY uchun
[Convert]::ToBase64String((New-Secret))                      # HUMO_PAN_ENCRYPTION_KEY (faqat HUMO_ENABLED=true bo‘lsa)
```

## 3. Migratsiya

```powershell
Set-Location C:\Multipay
$conn = (Get-Content .env | Where-Object { $_ -match '^DATABASE_URL=' } | Select-Object -Last 1) -replace '^DATABASE_URL=', ''
.\migrate.exe --connection $conn
```

`migrate.exe` qayta ishga tushirilsa, qo‘llangan migratsiyalarni o‘tkazib yuboradi. Migratsiya service har ishga tushganda bajarilmaydi: yangi release'da service to‘xtatilgach, backupdan keyin alohida bajariladi (7-bo‘lim).

## 4. Windows Service o‘rnatish

WinSW'ning barqaror **v2** x64 relizini ([Releases](https://github.com/winsw/winsw/releases), masalan v2.12.0, `WinSW-x64.exe`) yuklab, `C:\Multipay\deploy\windows\Multipay.exe` nomi bilan joylang; v3 hali alpha. U yonidagi `Multipay.xml` ni o‘qiydi.

SQL Server yoki Memurai shu serverda bo‘lsa, `install` dan **oldin** `Multipay.xml` dagi `<depend>` qatorlarini izohdan chiqarib, `Get-Service` ko‘rsatgan haqiqiy nomlarni yozing — shunda qayta yuklashda Multipay ulardan keyin ishga tushadi.

```powershell
Set-Location C:\Multipay
.\deploy\windows\Multipay.exe install
```

Service akkauntiga huquqlar (`<akkaunt>` — `DOMAIN\svc-multipay` yoki `.\svc-multipay`):

```powershell
$svc = '<akkaunt>'
New-Item -ItemType Directory -Force C:\Multipay\logs | Out-Null
icacls C:\Multipay /grant "${svc}:(OI)(CI)RX"
icacls C:\Multipay\logs /grant "${svc}:(OI)(CI)M"
icacls C:\Multipay\deploy\windows /grant "${svc}:(OI)(CI)M"
icacls C:\Multipay\.env /inheritance:r /grant:r "${svc}:R" "BUILTIN\Administrators:F" "NT AUTHORITY\SYSTEM:F"
```

Oxirgi buyruq `.env` ni faqat service akkaunti, administratorlar va SYSTEM o‘qiy oladigan qiladi. Service'ning **Log On** akkauntini `services.msc` orqali shu akkauntga o‘rnating, keyin:

```powershell
.\deploy\windows\Multipay.exe start
Get-Service Multipay
Test-NetConnection 127.0.0.1 -Port 8010
```

Alohida health endpoint yo‘q; uning o‘rnida bo‘sh so‘rov yuboriladi:

```powershell
try { Invoke-WebRequest http://127.0.0.1:8010/api/uzcard/session -Method Post -ContentType 'application/json' -Body '{}' -UseBasicParsing }
catch { $r = $_.Exception.Response; [int]$r.StatusCode; (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd() }
```

Ilova, baza sozlamalari va Redis joyida bo‘lsa, javob `401` va `"code":"HASH_INVALID"`. `500` Redis'ga ulanib bo‘lmayotganini bildiradi. Service umuman turmasa, sozlamalarda xato bor: sababi `deploy\windows\Multipay.out.log` / `Multipay.err.log` da (ilova noto‘g‘ri sozlamani nomi bilan aytib to‘xtaydi). Bu so‘rov IP limitiga (daqiqasiga 10 ta) hisoblanadi.

Ilova logi `logs\payment.log` da (10 MiB × 5 fayl, o‘zi aylanadi), WinSW loglari `deploy\windows\` da. Fon recovery loop'lari ham shu jarayonda ishlaydi, shuning uchun bitta nusxa ishlating.

```powershell
.\deploy\windows\Multipay.exe stop
.\deploy\windows\Multipay.exe start
```

## 5. IIS reverse proxy va HTTPS

`8010` port faqat `127.0.0.1` da tinglaydi; tashqi trafik IIS orqali keladi.

1. IIS'ni o‘rnating:

   ```powershell
   Install-WindowsFeature Web-Server, Web-Mgmt-Console
   ```

2. Microsoft'ning rasmiy IIS sahifasidan **URL Rewrite 2.1** va **Application Request Routing 3.0** x64 MSI paketlarini o‘rnating (avval URL Rewrite).

3. ARR proxy'ni yoqing, asl `Host` ni saqlang va `X-Forwarded-Proto` ga ruxsat bering:

   ```powershell
   $appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
   & $appcmd set config -section:system.webServer/proxy /enabled:"True" /preserveHostHeader:"True" /commit:apphost
   & $appcmd set config -section:system.webServer/rewrite/allowedServerVariables /+"[name='HTTP_X_FORWARDED_PROTO']" /commit:apphost
   ```

   Ularsiz slash-redirect va admin havolalari `http://127.0.0.1:8010/...` ko‘rinishida chiqadi.

4. IIS Manager'da `pay.<domain>.uz` uchun sayt yarating (ildizi alohida bo‘sh papka, masalan `C:\inetpub\multipay`), `443` ga HTTPS binding va sertifikat, yo‘naltirish uchun `80` binding qo‘shing.

5. `deploy\windows\web.config` ni **sayt ildiziga** nusxalang (`C:\Multipay` ga emas). U `/api/*` va `/admin/*` ni `http://127.0.0.1:8010` ga uzatadi. Undagi `<httpErrors existingResponse="PassThrough" />` majburiy: busiz IIS 401/409/422/429 javoblarining JSON tanasini o‘z xato sahifasi bilan almashtiradi. Admin tashqaridan kerak bo‘lmasa, qoidani `^api(/.*)?$` ga toraytiring.

6. Firewall'da faqat 443 (va yo‘naltirish uchun 80) ni oching:

   ```powershell
   New-NetFirewallRule -DisplayName 'Multipay HTTPS' -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
   New-NetFirewallRule -DisplayName 'Multipay HTTP redirect' -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
   ```

7. IIS shu serverda bo‘lsa `FORWARDED_ALLOW_IPS=127.0.0.1` qoladi. ARR mijoz manzilini `X-Forwarded-For` ga `ip:port` ko‘rinishida yozadi; ilova portni o‘zi ajratadi (testda mahkamlangan). Proxy boshqa serverda bo‘lsa, faqat uning IP manzilini yozing; `*` yozmang — aks holda har kim sarlavhani almashtirib IP limitini chetlab o‘tadi.

Checkout sahifasi (iframe HTML/JS) bu repozitoriyda yo‘q. Uni tarqatadigan server o‘sha HTML javobiga `Content-Security-Policy: frame-ancestors <ALLOWED_ORIGINS>` sarlavhasini qo‘yishi shart.

## 6. Qabul sinovi

- Boshqa kompyuterdan, IIS orqali:

  ```powershell
  [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
  try { Invoke-WebRequest https://pay.<domain>.uz/api/uzcard/session -Method Post -ContentType 'application/json' -Body '{}' -UseBasicParsing }
  catch { $r = $_.Exception.Response; [int]$r.StatusCode; $r.ContentType; (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd() }
  ```

  Kutiladigan javob: `401`, `application/json`, `HASH_INVALID`. HTML sahifa kelsa, `httpErrors` sozlamasi qo‘llanmagan.
- `logs\payment.log` dagi access qatorida mijozning haqiqiy IP manzili ko‘rinsin (`127.0.0.1` emas).
- Imzolangan hash bilan sessiya, idempotentlik, limitlar va Uzcard/HUMO **sandbox** oqimlarini `postman\` kolleksiyalari bilan tekshiring.
- Serverni qayta yuklab, SQL Server, Redis va Multipay service'lari o‘zi qaytishini tekshiring.

Python servisdan o‘tishda: ikkala servis alohida bazada ishlaydi (PostgreSQL → SQL Server), ma'lumot avtomatik ko‘chmaydi. Eski instansiyada yangi sessiyalarni to‘xtating, faol to‘lovlar yakunlansin, keyin trafikni almashtiring; xato bo‘lsa trafikni qaytarish tartibini oldindan tayyorlang.

## 7. Yangilash va orqaga qaytarish

```powershell
Set-Location C:\Multipay
.\deploy\windows\Multipay.exe stop
# 8-bo‘limdagi buyruq bilan backup oling
# yangi zip ichidagi fayllarni C:\Multipay ustiga nusxalang; .env, logs\ va deploy\windows\Multipay.exe ga tegmang
$conn = (Get-Content .env | Where-Object { $_ -match '^DATABASE_URL=' } | Select-Object -Last 1) -replace '^DATABASE_URL=', ''
.\migrate.exe --connection $conn
.\deploy\windows\Multipay.exe start
```

So‘ng 4-bo‘limdagi tekshiruv so‘rovini takrorlang. Service to‘xtab turganda recovery loop'lari ham ishlamaydi — to‘xtash vaqtini qisqa tuting; `PENDING_CHECK`/`PAID` sessiyalar service qaytgach davom ettiriladi.

Orqaga qaytarish: service'ni to‘xtating, avvalgi release papkasini qaytaring (yangilashdan oldin `C:\Multipay` ning nusxasini saqlab qo‘ying) va service'ni ishga tushiring. Release migratsiya qo‘shgan bo‘lsa, eski kod yangi sxema bilan ishlamasligi mumkin: bunda backupdan tiklash kerak bo‘ladi, backupdan keyingi to‘lovlarni esa Uzcard/HUMO va Operator bilan solishtirish shart.

## 8. Muntazam backup

```powershell
sqlcmd -S 127.0.0.1 -E -Q "BACKUP DATABASE payment TO DISK = N'D:\Backup\payment_$(Get-Date -Format yyyyMMdd_HHmm).bak' WITH CHECKSUM, INIT"
```

Buyruqni `C:\Multipay\backup.ps1` ga saqlab, Task Scheduler'da har kuni ishga tushiring:

```powershell
schtasks /Create /TN "Multipay DB backup" /SC DAILY /ST 03:00 /RU "<backup-akkaunt>" /RP /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Multipay\backup.ps1"
```

Backuplarni boshqa diskda yoki serverda saqlang, eskilarini tozalab boring va vaqti-vaqti bilan `RESTORE VERIFYONLY FROM DISK = N'<fayl>' WITH CHECKSUM` bilan, sinov bazasiga tiklab esa to‘liqligini tekshiring. Express nashrida backup siqilmaydi.
