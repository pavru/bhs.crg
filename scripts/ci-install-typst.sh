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
#
# ДВА ПУТИ ЗА ОДНИМ ФАЙЛОМ. 08.11.2026 адрес загрузки выпусков
# (github.com/<владелец>/<репозиторий>/releases/download/…) часами отдавал 504 — и на раннере, и
# локально, при «All Systems Operational» на странице статуса, — и обе обязательные работы падали
# на этом шаге у всех PR разом. Тот же ассет через API (api.github.com/…/releases/assets/<id>)
# отдавался исправно: это другой вход, он уводит на release-assets.githubusercontent.com мимо
# сломанного. Поэтому сначала основной адрес с повторами, при отказе — API, и о запасном пути
# говорит предупреждение в сводке прогона: он должен быть виден, а не молча становиться нормой.
#
# Токен (GITHUB_TOKEN или GH_TOKEN) нужен только запасному пути: без него API отвечает анонимно,
# 60 запросов в час на адрес, а адрес у раннеров общий. Шаги «Typst CLI» в ci.yml его передают.
#
# TYPST_RELEASES_BASE подменяет основной адрес — только чтобы проверить запасной путь руками:
#   TYPST_RELEASES_BASE=https://github.invalid bash scripts/ci-install-typst.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

version="$(sed -n 's/^ARG TYPST_VERSION=//p' "$root/deploy/Dockerfile.api" | head -1)"
if [ -z "$version" ]; then
  echo "::error::В deploy/Dockerfile.api не нашлась строка ARG TYPST_VERSION= — версию Typst взять неоткуда"
  exit 1
fi
echo "версия из образа API: $version"

repo="typst/typst"
asset="typst-x86_64-unknown-linux-musl.tar.xz"
tag="v${version}"
primary_base="${TYPST_RELEASES_BASE:-https://github.com/${repo}/releases/download}"
token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
archive="$work/typst.tar.xz"

# curl с повторами. --retry сам повторяет таймауты, 408, 429 и 5xx (504 в их числе); отказ
# соединения повторяет --retry-connrefused. Печатает код HTTP (000 — до ответа не дошло), код
# выхода curl возвращает своим: по нему и по коду HTTP строится причина отказа.
fetch() {
  local out="$1"
  shift
  curl -sS -L --fail --retry 4 --retry-delay 3 --retry-connrefused \
    --connect-timeout 20 --max-time 300 \
    -w '%{http_code}' -o "$out" "$@"
}

# Основной путь.
primary_url="${primary_base}/${tag}/${asset}"
primary_reason=""
set +e
http="$(fetch "$archive" "$primary_url" 2>"$work/primary.err")"
rc=$?
set -e
if [ "$rc" -ne 0 ]; then
  primary_reason="curl $rc, HTTP $http: $(tail -n 1 "$work/primary.err")"
  echo "основной адрес отказал ($primary_reason) — пробую через API"
fi

# Запасной путь: id ассета из описания выпуска, затем сам ассет по адресу API.
fallback_reason=""
if [ -n "$primary_reason" ]; then
  auth=()
  if [ -n "$token" ]; then
    auth=(-H "Authorization: Bearer $token")
  else
    echo "токена нет (GITHUB_TOKEN / GH_TOKEN) — API спрашиваю анонимно, лимит 60 запросов в час"
  fi

  release_json="$work/release.json"
  set +e
  http="$(fetch "$release_json" -H "Accept: application/vnd.github+json" "${auth[@]}" \
    "https://api.github.com/repos/${repo}/releases/tags/${tag}" 2>"$work/api.err")"
  rc=$?
  set -e
  if [ "$rc" -ne 0 ]; then
    fallback_reason="описание выпуска $tag: curl $rc, HTTP $http: $(tail -n 1 "$work/api.err")"
  else
    asset_id="$(jq -r --arg name "$asset" '.assets[] | select(.name == $name) | .id' "$release_json" | head -1)"
    if [ -z "$asset_id" ] || [ "$asset_id" = "null" ]; then
      fallback_reason="в выпуске $tag нет ассета $asset"
    else
      # Authorization curl на чужой хост при переходе не несёт — release-assets он и не нужен.
      set +e
      http="$(fetch "$archive" -H "Accept: application/octet-stream" "${auth[@]}" \
        "https://api.github.com/repos/${repo}/releases/assets/${asset_id}" 2>"$work/asset.err")"
      rc=$?
      set -e
      if [ "$rc" -ne 0 ]; then
        fallback_reason="ассет $asset_id: curl $rc, HTTP $http: $(tail -n 1 "$work/asset.err")"
      fi
    fi
  fi

  if [ -n "$fallback_reason" ]; then
    echo "::error title=Typst CLI не скачан::Typst $version не скачан ни одним путём. Основной адрес ($primary_url): $primary_reason. Через API: $fallback_reason. Если это сбой на стороне GitHub — перезапустите работу («Re-run failed jobs»), когда он пройдёт."
    exit 1
  fi
  echo "::warning title=Typst CLI — запасной путь::Typst $version скачан через API GitHub: основной адрес выпусков отказал ($primary_reason)."
fi

tar -xJf "$archive" -C "$work"
sudo=""
if [ "$(id -u)" -ne 0 ]; then sudo="sudo"; fi
$sudo install -m 0755 "$work/typst-x86_64-unknown-linux-musl/typst" /usr/local/bin/typst
typst --version
