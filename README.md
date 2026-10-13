# Multipay (.NET)

**Uzcard va HUMO kartasi orqali mobil hisobni to‘ldirish uchun to‘lov backend servisi.** Operator buyurtmani imzolaydi, tanlangan karta tizimi uchun sessiya ochiladi, mijoz OTP bilan to‘lovni tasdiqlaydi. To‘lovdan keyin servis Operator'ning hisobni to‘ldirish API'siga murojaat qiladi va chek beradi.

Servis Python/FastAPI'dagi `multipay` servisining **.NET 10 + ASP.NET Core + EF Core + MS SQL Server** ga ko‘chirilgan varianti. HTTP kontrakt o‘zgarmagan: URL'lar, JSON kalitlari va tartibi, HTTP statuslar, `error_code` raqamlari va header'lar bir xil — frontend va Operator eski servisga qanday murojaat qilsa, bunga ham shunday murojaat qiladi. Arxitektura, qarorlar va Python'dan farqlar [CLAUDE.md](CLAUDE.md) da.

> **HUMO holati:** integratsiya kodi bor, lekin `HUMO_ENABLED=false` standart qiymat. Uni faqat HUMO test muhiti rekvizitlari bilan oqim tekshirilgandan keyin yoqing.

## Mundarija

- [Nimalar kerak](#nimalar-kerak)
- [To‘lov qanday ishlaydi](#tolov-qanday-ishlaydi)
- [API](#api)
- [Operator hash'i](#operator-hashi)
- [Sozlamalar](#sozlamalar)
- [Windows Server'ga deploy](#windows-serverga-deploy)
- [Yangilash, orqaga qaytarish, backup](#yangilash-orqaga-qaytarish-backup)
- [Muammolarni aniqlash](#muammolarni-aniqlash)
- [Admin panel](#admin-panel)
- [Lokal ishlab chiqish](#lokal-ishlab-chiqish)

## Nimalar kerak

| Qayerda | Nima |
| --- | --- |
| Paket yig‘iladigan kompyuter | .NET 10 SDK (`dotnet --version` ≥ 10.0.100). Windows'da PowerShell yetarli; Linux/macOS'da `bash` va `zip` |
| Server | Windows Server 2016 x64 yoki yangisi. **.NET o‘rnatish shart emas** — paket self-contained |
| Ma'lumotlar bazasi | MS SQL Server 2016 yoki yangisi (testlar 2022 da yuradi) |
| Kesh | Redis 7 (alohida Linux serverda) yoki Windows'da Redis'ga mos Memurai |
| Tashqi trafik | IIS + URL Rewrite 2.1 + Application Request Routing 3.0, HTTPS sertifikat |
| Service | WinSW v2 (`WinSW-x64.exe`) |

Servis Windows'da **Docker'siz**, Windows Service bo‘lib ishlaydi: Windows Server 2016 Linux konteynerlarini yurgiza olmaydi.

## To‘lov qanday ishlaydi

```text
Operator ── imzolangan hash ──> Iframe/frontend
                                   │
                                   ├─ Uzcard: POST /api/uzcard/session
                                   └─ HUMO:   POST /api/humo/session
                                                │
                                 Karta → OTP → Tasdiqlash
                                                │
                     Uzcard SV-GATE yoki HUMO MW/SMS/Payment Service
                                                │
                                      Operator top-up API
                                                │
                                         Holat va chek
```

| Bosqich | Uzcard tashqi metodi | HUMO tashqi metodi |
| --- | --- | --- |
| Karta va OTP | SV-GATE `cards.new.otp` | MW `card2`, SMS `sms2` |
| OTP tasdig‘i | SV-GATE `cards.new.verify` | SMS `code2` |
| To‘lov | SV-GATE `trans.pay.purpose` | Payment Service `Payment` hold va confirm |
| Noaniq holatni tekshirish | SV-GATE `trans.ext` | Payment Service `GetPayment` |
| Muvaffaqiyatsiz top-up | SV-GATE `trans.reverse` | Payment Service `CancelRequest` yoki `ReturnPayment` |

Natijasi noaniq qolgan to‘lovlar `NEEDS_MANUAL` holatiga tushadi — ularni Uzcard/HUMO va Operator bilan qo‘lda solishtirish kerak. Fon tiklash jarayonlari (Uzcard har 15 soniyada, HUMO har 60 soniyada) ilova ishlab turganda ishlaydi, shuning uchun servis doim yoniq va **bitta nusxada** bo‘lishi kerak.

## API

`{provider}` — `uzcard` yoki `humo`. Barcha yo‘llar `/api` ostida va katta-kichik harfga sezgir; oxirida `/` bilan kelgan so‘rov `307` bilan slashsiz manzilga yo‘naltiriladi.

| Metod | Yo‘l | Vazifasi |
| --- | --- | --- |
| `POST` | `/api/{provider}/session` | Imzolangan hash orqali sessiya ochish |
| `POST` | `/api/{provider}/session/{session_id}/card` | Kartani yuborish va OTP olish |
| `POST` | `/api/{provider}/session/{session_id}/resend` | OTP'ni qayta yuborish |
| `POST` | `/api/{provider}/session/{session_id}/verify` | OTP'ni tasdiqlash va to‘lash |
| `POST` | `/api/{provider}/session/{session_id}/expire` | Sessiyani bekor qilish |
| `GET` | `/api/{provider}/session/{session_id}` | To‘lov holati |
| `GET` | `/api/receipt/{receipt_id}` | Maskalangan chek (ochiq, IP uchun 30/min) |

Sessiya ochilgandan keyingi so‘rovlarda `Authorization: Bearer <session_token>` yuboriladi. `card`, `resend`, `verify` va `expire` uchun UUID ko‘rinishidagi `Idempotency-Key` header'i majburiy: bir xil kalit bilan takrorlangan so‘rov aynan avvalgi javobni qaytaradi va tashqi tizimga ikkinchi marta bormaydi.

So‘rov tanalari:

| Yo‘l | Tana |
| --- | --- |
| `/session` | `{"hash": "<operator hash'i>"}` |
| `/card`, `/resend` | `{"pan": "8600123412341239", "expiry": "12/29"}` |
| `/verify` | `{"code": "A12B34", "payment_confirmed": true}` |
| `/expire` | tanasiz |

Javob har doim bir xil qolipda:

```text
HTTP 200  {"success":true,"result":{...}}
HTTP 4xx  {"success":false,"error":{"code":"HASH_EXPIRED","error_code":-2002,
                                     "message":{"uz":"...","en":"...","ru":"..."},"details":{...}}}
```

Masalan, sessiya ochilganda:

```json
{"success":true,"result":{"session_id":"d3b0fe78-...","session_token":"fDq2v9ww3UqMEKMHd5c8oA",
 "phone_mask":"+998 90 *** ** 67","amount":5000000,"commission":0,"total":5000000,
 "expires_at":"2026-10-13T11:31:02.521788Z","idle_timeout":180}}
```

Limitga urilgan so‘rovlar `429` va `Retry-After` header'i bilan qaytadi. Xato kodlarining to‘liq ro‘yxati `src/PaymentService/Domain/Enums/StatusCodes.cs` da (35 ta, `-2000 … -2034`). `.env` da `DOCS_ENABLED=true` qilinsa, Swagger `/api/docs` da, sxema `/api/openapi.json` da ochiladi — production'da o‘chiq qolsin.

## Operator hash'i

```text
<kid>.<base64url(payload_json)>.<base64url(HMAC-SHA256(secret, "<kid>.<base64url(payload_json)>"))>
```

| Maydon | Talab |
| --- | --- |
| `order_id` | 1–64 belgi, `A-Z a-z 0-9 _ . : -`; har buyurtma uchun yagona (katta-kichik harf farqlanadi) |
| `phone` | `998` + 9 raqam |
| `amount` | JSON butun son, **tiyinda**; `MIN_AMOUNT`–`MAX_AMOUNT` oralig‘ida |
| `ts` | Unix vaqti, soniyada; server soatidan ±300 soniya ichida |
| `nonce` | 16–128 belgi, `A-Z a-z 0-9 _ -`; har hash uchun yangi |

`kid` va `secret` — `.env` dagi `SECRET_KEYS=kid:secret` juftligi. Python servis imzolagan hash bu servisda ham aynan shunday tekshiriladi.

Sinov uchun hash imzolab sessiya ochadigan PowerShell namunasi (Windows Server'dagi PowerShell 5.1 da ham ishlaydi):

```powershell
$kid = 'k1'; $secret = '<SECRET_KEYS dagi sir>'; $url = 'http://127.0.0.1:8010'
function B64Url([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
$nonce = New-Object byte[] 16
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($nonce)
$ts = [int][Math]::Floor(([DateTime]::UtcNow - [DateTime]'1970-01-01').TotalSeconds)
$payload = '{"order_id":"TEST-' + $ts + '","phone":"998901234567","amount":5000000,"ts":' + $ts + ',"nonce":"' + (B64Url $nonce) + '"}'
$body = B64Url ([Text.Encoding]::UTF8.GetBytes($payload))
$hmac = New-Object Security.Cryptography.HMACSHA256 (, [Text.Encoding]::UTF8.GetBytes($secret))
$sig = B64Url $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$kid.$body"))
$json = '{"hash":"' + "$kid.$body.$sig" + '"}'
try { (Invoke-WebRequest "$url/api/uzcard/session" -Method Post -ContentType 'application/json' -Body $json -UseBasicParsing).Content }
catch { $r = $_.Exception.Response; [int]$r.StatusCode; (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd() }
```

Kutiladigan javob: `"success":true` va `session_id`, `session_token`. Har yugurishda yangi `order_id` va `nonce` hosil bo‘ladi.

## Sozlamalar

Sozlamalar ilova ishchi papkasidagi `.env` faylidan o‘qiladi; Windows muhit o‘zgaruvchilari undan ustun. Hammasi ishga tushishda tekshiriladi: xato bo‘lsa ilova qaysi sozlama noto‘g‘riligini aytib, start bermaydi. To‘liq ro‘yxat va standart qiymatlar [.env.example](.env.example) da.

| O‘zgaruvchi | Ma'nosi |
| --- | --- |
| `ENVIRONMENT` | `development` yoki `production`. Serverda `production` |
| `DATABASE_URL` | SQL Server ulanish satri (URL emas): `Server=host,1433;Database=payment;User Id=...;Password=...;Encrypt=True;TrustServerCertificate=True` |
| `REDIS_URL` | `redis://host:6379/0` |
| `SECRET_KEYS` | Operator bilan kelishilgan imzo kalitlari: `kid:secret[@tugash-vaqti]`, vergul bilan. Kalit almashtirishda yangisi va eskisi birga turadi |
| `PAN_HASH_PEPPER` | Karta raqami hash'i uchun alohida sir. Production'da standart qiymat rad etiladi |
| `ALLOWED_ORIGINS` | Checkout'ni iframe'ga joylay oladigan va API'ni chaqira oladigan saytlar: `https://host[:port]`, vergul bilan. `*` va `http://localhost` faqat `development` da |
| `FORWARDED_ALLOW_IPS` | `X-Forwarded-For` ga ishoniladigan proxy IP'lari. IIS shu serverda bo‘lsa `127.0.0.1`. `*` yozmang |
| `MIN_AMOUNT`, `MAX_AMOUNT`, `COMMISSION_PERCENT` | Summa chegaralari (tiyinda) va komissiya foizi |
| `SVGATE_*` | Uzcard SV-GATE manzili, login/parol, merchant va terminal rekvizitlari |
| `OPERATOR_URL`, `OPERATOR_TOKEN` | Operator'ning hisobni to‘ldirish API'si |
| `HUMO_ENABLED`, `HUMO_*` | HUMO oqimi va rekvizitlari. `HUMO_PAN_ENCRYPTION_KEY` — 32 baytli kalitning base64 ko‘rinishi |
| `RATE_LIMIT_*` | Limitlar: IP uchun daqiqasiga 10 / soatiga 60 sessiya, bitta telefon uchun 10 daqiqada 5 |
| `ADMIN_*` | Admin panel ([pastda](#admin-panel)) |
| `DOCS_ENABLED` | Swagger sahifasi. Production'da `false` |
| `LOG_FILE`, `LOG_LEVEL`, `LOG_MAX_BYTES`, `LOG_BACKUP_COUNT` | Log fayli (standart `logs/payment.log`, 10 MiB × 5 zaxira; bo‘sh qiymat faylni o‘chiradi) |

## Windows Server'ga deploy

Sxema: ilova `127.0.0.1:8010` da Windows Service bo‘lib ishlaydi, tashqi trafik IIS orqali HTTPS bilan keladi.

```text
Internet ──HTTPS 443──> IIS (pay.<domain>.uz) ──http 127.0.0.1:8010──> PaymentService.exe ──> SQL Server
                                                                                         └──> Redis
```

> Paket Linuxda yig‘ilib, ichi tekshirilgan, lekin Windows Server'ning o‘zida hali ishga tushirib ko‘rilmagan. Birinchi o‘rnatishni sinov serverida qiling.

Quyidagi buyruqlar, alohida aytilmagan bo‘lsa, serverda **Administrator PowerShell**'da bajariladi.

### 1-qadam. Paketni yig‘ish (o‘z kompyuteringizda)

Loyiha ildizida, .NET 10 SDK o‘rnatilgan kompyuterda:

```powershell
# Windows (PowerShell)
powershell -ExecutionPolicy Bypass -File deploy\windows\publish.ps1
```

```bash
# Linux / macOS
bash deploy/windows/publish.sh
```

Ikkala skript bir xil ishni qiladi; `make` kerak emas. Natija: `dist/windows/Multipay-win-x64.zip` (taxminan 115 MB).

Skriptsiz, qo‘lda yig‘ish (har qanday tizimda bir xil buyruqlar):

```powershell
dotnet publish src/PaymentService/PaymentService.csproj -c Release -r win-x64 --self-contained true -o dist/windows/Multipay
dotnet tool restore
dotnet ef migrations bundle --project src/PaymentService --configuration Release --self-contained -r win-x64 --output dist/windows/Multipay/migrate.exe --force
```

So‘ng `dist/windows/Multipay` ichiga `.env.example` ni va `deploy/windows/` dagi `Multipay.xml`, `web.config`, `README.md` ni `deploy/windows/` papkasi bilan nusxalang, publish hosil qilgan ildizdagi `web.config` ni o‘chiring (u IIS'ning boshqa rejimi uchun) va papkani zip qiling.

| Paket ichida | Vazifasi |
| --- | --- |
| `PaymentService.exe` va DLL'lar | ilova, .NET runtime bilan birga |
| `migrate.exe` | baza sxemasini yaratadi va yangilaydi |
| `.env.example` | sozlamalar namunasi |
| `deploy\windows\Multipay.xml` | Windows Service konfiguratsiyasi (WinSW) |
| `deploy\windows\web.config` | IIS reverse proxy qoidalari |
| `deploy\windows\README.md` | shu yo‘riqnomaning serverda o‘qiladigan nusxasi |

Zip'ni serverga ko‘chirib, `C:\Multipay` ga oching. Ko‘chirilgan fayl buzilmaganini tekshirish uchun ikkala tomonda hash'ni solishtiring: Linuxda `sha256sum`, Windows'da `Get-FileHash Multipay-win-x64.zip -Algorithm SHA256`.

### 2-qadam. Serverni tayyorlash

**Windows.** Barcha yangilanishlarni o‘rnating. Ikki cheklovni bilib qo‘ying:

- Windows Server 2016 ning kengaytirilgan qo‘llab-quvvatlash muddati **2027-yil 12-yanvarda** tugaydi. Imkon bo‘lsa 2019 yoki 2022 ni tanlang — yo‘riqnoma bir xil.
- Windows Server 2016 da TLS 1.3 yo‘q, eng yuqorisi TLS 1.2. SV-GATE, HUMO va Operator manzillari TLS 1.2 ni qabul qilishini oldindan aniqlang.

**Soat.** Operator hash'idagi `ts` server soatidan ±300 soniyadan ortiq farq qilsa, har bir sessiya `HASH_EXPIRED` bilan rad etiladi:

```powershell
w32tm /query /status
w32tm /resync
```

Domensiz serverda vaqt manbasini qo‘lda belgilang:

```powershell
w32tm /config /manualpeerlist:"<ntp-server>" /syncfromflags:manual /update
Restart-Service w32time
w32tm /resync
```

**SQL Server.** 2016 yoki yangisini o‘rnating (Windows Server 2016 ga 2022 gacha o‘rnatiladi; Express nashri bazani 10 GB bilan cheklaydi). O‘rnatishda *Mixed Mode* ni yoqing, SQL Server Configuration Manager'da TCP/IP protokolini yoqing. Bo‘sh baza va ilova uchun alohida login yarating (`sa` ni ilovaga bermang):

```powershell
sqlcmd -S 127.0.0.1 -E -Q "CREATE DATABASE payment; CREATE LOGIN payment WITH PASSWORD = '<kuchli-parol>'; ALTER AUTHORIZATION ON DATABASE::payment TO payment;"
Test-NetConnection 127.0.0.1 -Port 1433
```

Login baza egasi bo‘lishi kerak: migratsiya jadvallarni yaratadi va bazani `READ_COMMITTED_SNAPSHOT` rejimiga o‘tkazadi. Server kollatsiyasini o‘zgartirish shart emas — ustunlar kollatsiyasi migratsiyada yozilgan.

**Redis.** Windows uchun rasmiy Redis yo‘q:

- **Alohida Linux serverdagi Redis 7** — tavsiya etiladi, ilova aynan shu bilan sinalgan.
- **Memurai** — Redis'ga mos Windows xizmati. Bepul Developer nashri production uchun emas va ma'lum vaqtdan keyin o‘zi to‘xtaydi. Limit va idempotentlik Lua skriptlarga tayanadi, shuning uchun Memurai bilan 6-qadamdagi sinovni to‘liq o‘tkazing.

```powershell
Get-Service -Name '*memurai*'
& 'C:\Program Files\Memurai\memurai-cli.exe' -h 127.0.0.1 -p 6379 ping
```

Redis'da AOF yoki RDB saqlashni yoqing: nonce va idempotentlik kalitlari shu yerda turadi, ma'lumot yo‘qolsa 5 daqiqalik oynadagi hash qayta ishlatilishi mumkin.

### 3-qadam. Sozlamalar

```powershell
Set-Location C:\Multipay
Copy-Item .env.example .env
notepad .env
```

Kamida quyidagilarni haqiqiy qiymatlarga o‘zgartiring, so‘ng SV-GATE va Operator (kerak bo‘lsa HUMO) rekvizitlarini kiriting:

```dotenv
ENVIRONMENT=production
DATABASE_URL=Server=127.0.0.1,1433;Database=payment;User Id=payment;Password=<parol>;Encrypt=True;TrustServerCertificate=True
REDIS_URL=redis://127.0.0.1:6379/0
SECRET_KEYS=k1:<Operator bilan kelishilgan sir>
PAN_HASH_PEPPER=<alohida tasodifiy qiymat>
ALLOWED_ORIGINS=https://<operator-sayti>
FORWARDED_ALLOW_IPS=127.0.0.1
DOCS_ENABLED=false
LOG_FILE=logs/payment.log
```

Parolda `;` bo‘lsa, uni `{...}` ichiga oling. Ishlab chiqish kompyuteridagi `.env` ni serverga ko‘chirmang — unda sinov qiymatlari bor.

Maxfiy qiymatlarni serverning o‘zida yarating, har biri alohida:

```powershell
function New-Secret { $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $b }
-join (New-Secret | ForEach-Object { $_.ToString('x2') })   # SECRET_KEYS=k1:<qiymat>, PAN_HASH_PEPPER, ADMIN_SECRET_KEY uchun
[Convert]::ToBase64String((New-Secret))                      # HUMO_PAN_ENCRYPTION_KEY uchun
```

`SECRET_KEYS` dagi sirni Operator'ga xavfsiz kanal orqali bering.

### 4-qadam. Baza sxemasi

```powershell
Set-Location C:\Multipay
$conn = (Get-Content .env | Where-Object { $_ -match '^DATABASE_URL=' } | Select-Object -Last 1) -replace '^DATABASE_URL=', ''
.\migrate.exe --connection $conn
```

`migrate.exe` ni qayta yurgizish xavfsiz: qo‘llangan migratsiyalarni o‘tkazib yuboradi. Migratsiya service ishga tushganda o‘zi bajarilmaydi — har yangi release'da qo‘lda yurgiziladi.

### 5-qadam. Windows Service

1. [WinSW Releases](https://github.com/winsw/winsw/releases) sahifasidan barqaror **v2** relizini (masalan v2.12.0, `WinSW-x64.exe`) yuklab, `C:\Multipay\deploy\windows\Multipay.exe` nomi bilan joylang. v3 hali alpha — ishlatmang.

2. SQL Server yoki Memurai shu serverda bo‘lsa, `deploy\windows\Multipay.xml` dagi `<depend>` qatorlarini izohdan chiqarib, `Get-Service` ko‘rsatgan haqiqiy nomlarni yozing. Shunda qayta yuklashda Multipay ulardan keyin ishga tushadi.

3. Service'ni o‘rnating:

   ```powershell
   Set-Location C:\Multipay
   .\deploy\windows\Multipay.exe install
   ```

4. Service uchun alohida Windows akkaunti oching va unga huquq bering (`<akkaunt>` — `DOMAIN\svc-multipay` yoki `.\svc-multipay`):

   ```powershell
   $svc = '<akkaunt>'
   New-Item -ItemType Directory -Force C:\Multipay\logs | Out-Null
   icacls C:\Multipay /grant "${svc}:(OI)(CI)RX"
   icacls C:\Multipay\logs /grant "${svc}:(OI)(CI)M"
   icacls C:\Multipay\deploy\windows /grant "${svc}:(OI)(CI)M"
   icacls C:\Multipay\.env /inheritance:r /grant:r "${svc}:R" "BUILTIN\Administrators:F" "NT AUTHORITY\SYSTEM:F"
   ```

   Oxirgi buyruqdan keyin `.env` ni faqat service akkaunti, administratorlar va SYSTEM o‘qiy oladi. `services.msc` da *Multipay API* → **Log On** bo‘limida shu akkauntni tanlang.

5. Ishga tushiring va tekshiring:

   ```powershell
   .\deploy\windows\Multipay.exe start
   Get-Service Multipay
   Test-NetConnection 127.0.0.1 -Port 8010
   try { Invoke-WebRequest http://127.0.0.1:8010/api/uzcard/session -Method Post -ContentType 'application/json' -Body '{}' -UseBasicParsing }
   catch { $r = $_.Exception.Response; [int]$r.StatusCode; (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd() }
   ```

   Alohida health endpoint yo‘q, uning o‘rnida shu bo‘sh so‘rov ishlatiladi. To‘g‘ri javob: `401` va `"code":"HASH_INVALID"` — ilova ishlayapti va Redis'ga ulangan. Bu so‘rov IP limitiga (daqiqasiga 10 ta) hisoblanadi.

### 6-qadam. IIS va HTTPS

`8010` port faqat `127.0.0.1` da tinglaydi; tashqaridan faqat IIS ko‘rinadi.

1. IIS'ni o‘rnating:

   ```powershell
   Install-WindowsFeature Web-Server, Web-Mgmt-Console
   ```

2. Microsoft'ning rasmiy IIS sahifasidan **URL Rewrite 2.1**, keyin **Application Request Routing 3.0** x64 paketlarini o‘rnating.

3. Proxy'ni yoqing:

   ```powershell
   $appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
   & $appcmd set config -section:system.webServer/proxy /enabled:"True" /preserveHostHeader:"True" /commit:apphost
   & $appcmd set config -section:system.webServer/rewrite/allowedServerVariables /+"[name='HTTP_X_FORWARDED_PROTO']" /commit:apphost
   ```

   Bularsiz yo‘naltirishlar va admin havolalari `http://127.0.0.1:8010/...` ko‘rinishida chiqadi.

4. IIS Manager'da `pay.<domain>.uz` uchun sayt yarating. Sayt ildizi — alohida bo‘sh papka, masalan `C:\inetpub\multipay`. `443` portga HTTPS binding va sertifikat, yo‘naltirish uchun `80` binding qo‘shing.

5. `C:\Multipay\deploy\windows\web.config` ni **sayt ildiziga** nusxalang (`C:\Multipay` ga emas). U `/api/*` va `/admin/*` ni ilovaga uzatadi va HTTP'ni HTTPS'ga yo‘naltiradi. Ichidagi `<httpErrors existingResponse="PassThrough" />` majburiy: busiz IIS 401/409/422/429 javoblarining JSON tanasini o‘z xato sahifasi bilan almashtiradi va frontend `error.code` ni o‘qiy olmaydi. Admin panel tashqaridan kerak bo‘lmasa, qoidani `^api(/.*)?$` ga toraytiring.

6. Firewall'da faqat 443 va 80 ni oching. `8010`, `1433` va `6379` tashqariga ochilmasin:

   ```powershell
   New-NetFirewallRule -DisplayName 'Multipay HTTPS' -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
   New-NetFirewallRule -DisplayName 'Multipay HTTP redirect' -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
   ```

IIS mijoz manzilini `X-Forwarded-For` ga `ip:port` ko‘rinishida yozadi; ilova portni o‘zi ajratadi. IIS boshqa serverda bo‘lsa, `FORWARDED_ALLOW_IPS` ga faqat o‘sha serverning IP manzilini yozing.

Checkout sahifasi (iframe HTML/JS) bu repozitoriyda yo‘q. Uni tarqatadigan server o‘sha HTML javobiga `Content-Security-Policy: frame-ancestors <ALLOWED_ORIGINS>` header'ini o‘zi qo‘yishi kerak.

### 7-qadam. Qabul sinovi

1. Boshqa kompyuterdan, IIS orqali:

   ```powershell
   [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
   try { Invoke-WebRequest https://pay.<domain>.uz/api/uzcard/session -Method Post -ContentType 'application/json' -Body '{}' -UseBasicParsing }
   catch { $r = $_.Exception.Response; [int]$r.StatusCode; $r.ContentType; (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd() }
   ```

   Kutiladi: `401`, `application/json`, `HASH_INVALID`. HTML sahifa kelsa, `web.config` dagi `httpErrors` qo‘llanmagan.

2. `logs\payment.log` dagi access qatorida mijozning haqiqiy IP manzili ko‘rinsin (`127.0.0.1` emas).

3. [Operator hash'i](#operator-hashi) bo‘limidagi PowerShell namunasi bilan (`$url = 'https://pay.<domain>.uz'`) haqiqiy sessiya oching.

4. SV-GATE (va yoqilgan bo‘lsa HUMO) **sandbox** rekvizitlari bilan to‘liq oqimni o‘tkazing: karta → OTP → to‘lov → holat → chek. Bir xil `Idempotency-Key` bilan takror va limitlarni ham tekshiring.

5. Serverni qayta yuklang: SQL Server, Redis va Multipay service'lari o‘zi ko‘tarilishi kerak.

Python servisdan o‘tilayotgan bo‘lsa: ikkala servis alohida bazada ishlaydi (PostgreSQL va SQL Server), ma'lumot o‘zi ko‘chmaydi. Eski servisda yangi sessiyalarni to‘xtating, faol to‘lovlar yakunlansin, keyin trafikni almashtiring. Xato chiqsa trafikni qaytarish tartibini oldindan tayyorlab qo‘ying.

## Yangilash, orqaga qaytarish, backup

**Yangilash.** Avval `C:\Multipay` ning nusxasini saqlab qo‘ying (orqaga qaytarish uchun), keyin:

```powershell
Set-Location C:\Multipay
.\deploy\windows\Multipay.exe stop
# backup oling (pastda)
# yangi zip ichidagi fayllarni C:\Multipay ustiga nusxalang;
# .env, logs\ va deploy\windows\Multipay.exe ga tegmang
$conn = (Get-Content .env | Where-Object { $_ -match '^DATABASE_URL=' } | Select-Object -Last 1) -replace '^DATABASE_URL=', ''
.\migrate.exe --connection $conn
.\deploy\windows\Multipay.exe start
```

So‘ng 5-qadamdagi tekshiruv so‘rovini takrorlang. Service to‘xtab turganda fon tiklash jarayonlari ham ishlamaydi, shuning uchun to‘xtash vaqtini qisqa tuting; chala qolgan to‘lovlar service qaytgach davom ettiriladi.

**Orqaga qaytarish.** Service'ni to‘xtating, saqlab qo‘yilgan avvalgi papkani qaytaring va service'ni ishga tushiring. Yangi release baza sxemasini o‘zgartirgan bo‘lsa, eski kod u bilan ishlamasligi mumkin — bunda backupdan tiklash kerak, backupdan keyin o‘tgan to‘lovlarni esa Uzcard/HUMO va Operator bilan solishtirish shart.

**Backup.**

```powershell
sqlcmd -S 127.0.0.1 -E -Q "BACKUP DATABASE payment TO DISK = N'D:\Backup\payment_$(Get-Date -Format yyyyMMdd_HHmm).bak' WITH CHECKSUM, INIT"
```

Buyruqni `C:\Multipay\backup.ps1` ga saqlab, har kuni ishga tushiring:

```powershell
schtasks /Create /TN "Multipay DB backup" /SC DAILY /ST 03:00 /RU "<backup-akkaunt>" /RP /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Multipay\backup.ps1"
```

Backuplarni boshqa diskda yoki serverda saqlang, eskilarini tozalab boring va vaqti-vaqti bilan `RESTORE VERIFYONLY FROM DISK = N'<fayl>' WITH CHECKSUM` bilan, sinov bazasiga tiklab esa to‘liqligini tekshiring.

## Muammolarni aniqlash

| Belgi | Sabab va yechim |
| --- | --- |
| Service ishga tushmaydi yoki darhol to‘xtaydi | `.env` da xato. Sababi `deploy\windows\Multipay.out.log` / `Multipay.err.log` da: ilova noto‘g‘ri sozlamani nomi bilan aytadi |
| Tekshiruv so‘rovi `500` qaytaradi | Redis yoki SQL Server'ga ulanib bo‘lmayapti. `logs\payment.log` ni ko‘ring, `REDIS_URL` va `DATABASE_URL` ni tekshiring |
| Hamma sessiya `HASH_EXPIRED` | Server soati noto‘g‘ri: `w32tm /resync` |
| Hamma sessiya `HASH_INVALID` | Operator boshqa `kid` yoki sir bilan imzolayapti; `SECRET_KEYS` ni solishtiring |
| `/card` har doim `503 PROCESSING_UNAVAILABLE` | SV-GATE'ga yetib bo‘lmayapti: manzil, firewall, TLS 1.2 |
| Xato o‘rnida IIS'ning HTML sahifasi keladi | `web.config` sayt ildizida emas yoki `httpErrors` qatori yo‘q |
| Logda mijoz IP'si `127.0.0.1` | ARR proxy sozlanmagan yoki `FORWARDED_ALLOW_IPS` da IIS manzili yo‘q |
| Bitta ofisdan kelgan mijozlar `429` oladi | IP limiti umumiy tashqi IP'ga uriladi; `RATE_LIMIT_IP_*` ni oshiring |
| Sessiya `NEEDS_MANUAL` da qolgan | Tashqi tizim natijasi noaniq. Admin paneldan topib, Uzcard/HUMO va Operator bilan qo‘lda solishtiring |

Ilova logi `C:\Multipay\logs\payment.log` da (o‘zi aylanadi), service loglari `C:\Multipay\deploy\windows\` da. Loglarda hash, token va to‘liq karta raqami yozilmaydi.

## Admin panel

Faqat o‘qish uchun: sessiyalar, to‘lovlar, cheklar, SV-GATE va HUMO audit loglari. Vaqtlar bazada UTC, panelda UTC+05:00 da ko‘rsatiladi. Telefon, karta tokeni, sessiya tokeni, OTP va xom so‘rov/javoblar ko‘rsatilmaydi; yaratish, tahrirlash, o‘chirish yo‘q.

Panel standart holatda o‘chiq. Yoqish uchun `.env` da:

```dotenv
ADMIN_ENABLED=true
ADMIN_USERNAME=admin
ADMIN_PASSWORD=<kamida 16 belgi>
ADMIN_SECRET_KEY=<kamida 32 belgi>
ADMIN_COOKIE_SECURE=true
```

va service'ni qayta ishga tushiring. Manzil: `https://pay.<domain>.uz/admin`. `ADMIN_COOKIE_SECURE=true` HTTPS ortida shart; faqat mahalliy `http://` sinovida `false` qiling, aks holda login saqlanmaydi. Login 1 soat amal qiladi. Panelni tashqi tarmoqqa ochish shart bo‘lmasa, IIS'da `/admin` ni IP bo‘yicha cheklang.

## Lokal ishlab chiqish

.NET 10 SDK, ishlab turgan SQL Server va Redis kerak.

Buyruqlar Windows (PowerShell), Linux va macOS'da bir xil; `make` kerak emas. Loyiha ildizida:

```powershell
# NuGet paketlari va dotnet-ef
dotnet restore
dotnet tool restore

# ishga tushirish: http://127.0.0.1:8000
dotnet run --project src/PaymentService --urls http://127.0.0.1:8000

# testlar
dotnet test

# ogohlantirishsiz build + format tekshiruvi
dotnet build -warnaserror
dotnet format --verify-no-changes
```

Birinchi ishga tushirishdan oldin `.env.example` dan `.env` yarating (`Copy-Item .env.example .env`, Linuxda `cp`), `DATABASE_URL` va `REDIS_URL` ni o‘z SQL Server va Redis'ingizga qarating, keyin sxemani qo‘llang.

Migratsiya buyruqlariga ulanish satri kerak — uni `.env` dagi `DATABASE_URL` dan oling:

```powershell
# PowerShell
$conn = (Get-Content .env | Where-Object { $_ -match '^DATABASE_URL=' } | Select-Object -Last 1) -replace '^DATABASE_URL=', ''
dotnet ef database update --project src/PaymentService --connection $conn                              # sxemani qo‘llash
dotnet ef migrations add "<Nomi>" --project src/PaymentService --output-dir Migrations                  # model o‘zgargach yangi migratsiya
```

```bash
# Linux / macOS
conn="$(grep '^DATABASE_URL=' .env | cut -d= -f2-)"
dotnet ef database update --project src/PaymentService --connection "$conn"
dotnet ef migrations add "<Nomi>" --project src/PaymentService --output-dir Migrations
```

Testlar haqiqiy SQL Server 2022 va Redis'ni Testcontainers orqali o‘zi ko‘taradi, shuning uchun `dotnet test` ga **Docker** kerak (faqat ishlab chiqish kompyuterida; Windows'da Docker Desktop, serverda kerak emas). Testlar `.env` ga bog‘liq emas.

Loyiha tuzilishi:

```text
src/PaymentService/
  Api/Controllers/                  HTTP yo‘llari
  Infrastructure/Services/          to‘lov oqimlari va biznes qoidalar
  Infrastructure/Repositories/      baza bilan ishlash (EF Core)
  Infrastructure/ExternalServices/  Redis, SV-GATE, HUMO, Operator mijozlari
  Domain/                           modellar, so‘rov sxemalari, status kodlar
  Core/                             sozlamalar, middleware'lar, xato ishlovchilari
  Admin/                            admin panel sahifalari
  Migrations/                       baza migratsiyasi
tests/PaymentService.Tests/         xUnit testlari
deploy/windows/                     Windows paketi va o‘rnatish fayllari
```

Kodga o‘zgartirish kiritishdan oldin [CLAUDE.md](CLAUDE.md) dagi "Differences from the Python service" bo‘limini o‘qing: g‘ayrioddiy ko‘ringan joylarning ko‘pi Python servis bilan kontraktni saqlash uchun shunday yozilgan.
