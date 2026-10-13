# Windows Server uchun o'rnatish paketini yig'adi (.NET 10 SDK bor Windows kompyuterida, PowerShell 5.1+):
#   dist\windows\Multipay\        self-contained win-x64 ilova, migrate.exe, .env.example, deploy\windows\
#   dist\windows\Multipay-win-x64.zip
# Linux/macOS uchun shu ishni deploy/windows/publish.sh bajaradi.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..\..')
$out = 'dist\windows\Multipay'
if (Test-Path dist\windows) { Remove-Item dist\windows -Recurse -Force }

dotnet publish src\PaymentService\PaymentService.csproj -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish xato bilan tugadi' }
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore xato bilan tugadi' }
dotnet ef migrations bundle --project src\PaymentService --configuration Release --self-contained -r win-x64 --output "$out\migrate.exe" --force
if ($LASTEXITCODE -ne 0) { throw 'dotnet ef migrations bundle xato bilan tugadi' }

New-Item -ItemType Directory -Force "$out\deploy\windows", "$out\logs" | Out-Null
Copy-Item deploy\windows\Multipay.xml, deploy\windows\web.config, deploy\windows\README.md "$out\deploy\windows\"
Copy-Item .env.example "$out\.env.example"
if (Test-Path "$out\web.config") { Remove-Item "$out\web.config" }
Compress-Archive -Path $out -DestinationPath dist\windows\Multipay-win-x64.zip -Force
Write-Host 'Tayyor: dist\windows\Multipay-win-x64.zip'
