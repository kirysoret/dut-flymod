#!/usr/bin/env bash
# Publica una version nueva: ./publicar.sh 2.0.1 "notas de la version"
# Requiere FlyMod.dll y FlyModInstaller.exe ya copiados en esta carpeta.
set -euo pipefail
cd "$(dirname "$0")"
VER="$1"; NOTAS="${2:-}"
REPO="kirysoret/dut-flymod"
GIT=(git -c user.name=kirysoret -c user.email=kirysoret@users.noreply.github.com)

# 1) subir binarios y codigo
git add FlyMod.dll FlyModInstaller.exe src README.md publicar.sh
"${GIT[@]}" commit -q -m "Binarios $VER" || true
git push -q
SHA=$(git rev-parse HEAD)

# 2) version.txt con enlaces fijados a ese commit (evita la cache del CDN)
U="https://raw.githubusercontent.com/$REPO/$SHA"
h() { sha256sum "$1" | cut -d' ' -f1 | tr a-z A-Z; }
cat > version.txt <<EOF
version=$VER
notas=$NOTAS
mod=$U/FlyMod.dll
mod_sha256=$(h FlyMod.dll)
instalador=$U/FlyModInstaller.exe
instalador_sha256=$(h FlyModInstaller.exe)
EOF
git add version.txt
"${GIT[@]}" commit -q -m "Version $VER"
git push -q
cat version.txt
