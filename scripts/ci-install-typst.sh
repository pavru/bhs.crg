#!/usr/bin/env bash
# Typst CLI для CI — ТОЙ ЖЕ версии, что едет в образе API.
#
# Версию ЧИТАЕМ из deploy/Dockerfile.api, а не дублируем числом. Два независимых литерала
# разъезжаются молча: подняли версию в образе — CI продолжил бы проверять прежним Typst, и шаблон с
# новым синтаксисом зеленел бы здесь и падал в поставке (а убранный синтаксис — наоборот).
#
# Зовут две работы CI, и обе обязаны проверять одним компилятором, поэтому это скрипт, а не два
# одинаковых шага: «Backend» — значки из логотипа растрирует Typst (issue #1315), и его тесты идут
# на настоящем; «Живые прогоны» — проверка Typst-блоков в редакторе типов зовёт компилятор на
# сервере.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

version="$(sed -n 's/^ARG TYPST_VERSION=//p' "$root/deploy/Dockerfile.api" | head -1)"
if [ -z "$version" ]; then
  echo "::error::В deploy/Dockerfile.api не нашлась строка ARG TYPST_VERSION= — версию Typst взять неоткуда"
  exit 1
fi
echo "версия из образа API: $version"

curl -fsSL --retry 3 --retry-delay 2 \
  "https://github.com/typst/typst/releases/download/v${version}/typst-x86_64-unknown-linux-musl.tar.xz" \
  -o /tmp/typst.tar.xz
tar -xJf /tmp/typst.tar.xz -C /tmp
sudo mv /tmp/typst-x86_64-unknown-linux-musl/typst /usr/local/bin/typst
sudo chmod +x /usr/local/bin/typst
typst --version
