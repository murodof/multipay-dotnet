#!/usr/bin/env bash
# Windows Server uchun o'rnatish paketini yig'adi (Linux yoki Windows'dagi .NET 10 SDK bilan):
#   dist/windows/Multipay/        self-contained win-x64 ilova, migrate.exe, .env.example, deploy/windows/
#   dist/windows/Multipay-win-x64.zip
# Serverda .NET o'rnatish shart emas.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
out=dist/windows/Multipay
rm -rf dist/windows
dotnet publish src/PaymentService/PaymentService.csproj -c Release -r win-x64 --self-contained true -o "$out"
dotnet tool restore
dotnet ef migrations bundle --project src/PaymentService --configuration Release \
  --self-contained -r win-x64 --output "$out/migrate.exe" --force
mkdir -p "$out/deploy/windows" "$out/logs"
cp deploy/windows/Multipay.xml deploy/windows/web.config deploy/windows/README.md "$out/deploy/windows/"
cp .env.example "$out/.env.example"
rm -f "$out/web.config"
(cd dist/windows && zip -qr Multipay-win-x64.zip Multipay)
echo "Tayyor: dist/windows/Multipay-win-x64.zip"
