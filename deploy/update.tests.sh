#!/usr/bin/env bash
#
# Проверка чистой логики update.sh — той, что решает, но не видна.
#
#   bash deploy/update.tests.sh
#
# Смысл файла в одном: у скрипта обновления нет способа заметить собственную поломку. Он работает
# раз в несколько недель, на чужой машине, и отвечает молчанием там, где ошибся: проверка фоновых
# задач полгода отвечала «задач нет» ВСЕГДА (падал её собственный запрос, ошибку глушило
# перенаправление), а откат столько же отказывал ВСЕГДА (сравнение списков расходилось на переводе
# строки). Оба раза скрипт выглядел работающим. Здесь проверяется то, что можно спросить без
# Docker и без сети, — на это и рассчитывать.
#
# Устройство: из update.sh вырезается развилка команд, остальное подключается как библиотека. Так
# проверяются ТЕ ЖЕ функции, а не их копии — копия разошлась бы с оригиналом на первой же правке.
# Docker и паузы подменяются функциями-заглушками там, где без них не обойтись.

set -uo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/update.sh"
[ -f "$SRC" ] || { echo "не найден $SRC" >&2; exit 1; }

SB="$(mktemp -d)"
trap 'cd /; rm -rf "$SB"' EXIT
cd "$SB" || exit 1

sed '/^if \[ "\$CHECK" -eq 1 \]; then$/,$d' "$SRC" > lib.sh
# shellcheck disable=SC1091
source ./lib.sh
set +e   # lib.sh включает errexit: без этого прогон обрывался бы на первом ОЖИДАЕМОМ отказе

ok=0; bad=0
check() { # check «что проверяем» ожидание факт
    if [ "$2" = "$3" ]; then ok=$((ok + 1)); printf '  ok   %s\n' "$1"
    else bad=$((bad + 1)); printf '  ФЕЙЛ %s\n       ждали: %s\n       факт:  %s\n' "$1" "$2" "$3"; fi
}

echo '── crosses: рубеж пересекаем ровно тогда, когда через него проходим ──'
CURRENT=0.90.0; TARGET=0.92.0
crosses 0.91.0  && r=да || r=нет; check '0.90→0.92 пересекает 0.91'     да  "$r"
crosses 0.137.0 && r=да || r=нет; check '0.90→0.92 НЕ пересекает 0.137' нет "$r"
crosses 0.139.0 && r=да || r=нет; check '0.90→0.92 НЕ пересекает 0.139' нет "$r"
CURRENT=0.90.0; TARGET=0.141.0
crosses 0.91.0  && r=да || r=нет; check '0.90→0.141 пересекает 0.91'    да "$r"
crosses 0.137.0 && r=да || r=нет; check '0.90→0.141 пересекает 0.137'   да "$r"
crosses 0.139.0 && r=да || r=нет; check '0.90→0.141 пересекает 0.139'   да "$r"
CURRENT=0.139.0; TARGET=0.150.0
crosses 0.139.0 && r=да || r=нет; check 'уже на 0.139 — рубеж позади'   нет "$r"
CURRENT=0.138.0; TARGET=0.139.0
crosses 0.139.0 && r=да || r=нет; check 'ровно на рубеж — пересекаем'   да "$r"

echo
echo '── legacy_notes: печатаем только пересечённые рубежи ──'
compose() { echo ollama; }          # как будто контейнер ollama работает
CURRENT=0.90.0; TARGET=0.92.0
out="$(legacy_notes 2>&1)"
printf '%s' "$out" | grep -q 'dp_keys'         && r=да || r=нет; check '0.90→0.92: про dp_keys сказано'   да  "$r"
printf '%s' "$out" | grep -q 'готовые образы'  && r=да || r=нет; check '0.90→0.92: про образы промолчали' нет "$r"
printf '%s' "$out" | grep -q 'профилю Compose' && r=да || r=нет; check '0.90→0.92: про ollama промолчали' нет "$r"
CURRENT=0.90.0; TARGET=0.141.0
out="$(legacy_notes 2>&1)"
printf '%s' "$out" | grep -q 'dp_keys'         && r=да || r=нет; check '0.90→0.141: про dp_keys сказано'  да "$r"
printf '%s' "$out" | grep -q 'готовые образы'  && r=да || r=нет; check '0.90→0.141: про образы сказано'   да "$r"
printf '%s' "$out" | grep -q 'профилю Compose' && r=да || r=нет; check '0.90→0.141: про ollama сказано'   да "$r"
CURRENT=0.146.0; TARGET=0.147.1
check 'свежая установка: ни одной оговорки' '' "$(legacy_notes 2>&1)"
unset -f compose

echo
echo '── compose_default: умолчание переменной из compose-файла ──'
mkdir -p new
cat > new/docker-compose.yml <<'YML'
services:
  api:
    image: ghcr.io/pavru/bhs.crg-api:${APP_VERSION:?APP_VERSION не задан}
    environment:
      A: ${WEB_PORT:-8080}
      B: ${OLLAMA_MODEL:-}
      C: ${MINIO_BUCKET}
      D: ${BACKUP_DIR:-./backups}
YML
NEW_DIR=new
check 'WEB_PORT'     8080      "$(compose_default WEB_PORT)"
check 'OLLAMA_MODEL' ''        "$(compose_default OLLAMA_MODEL)"
check 'BACKUP_DIR'   ./backups "$(compose_default BACKUP_DIR)"
compose_default MINIO_BUCKET >/dev/null && r=есть || r=нет
check 'MINIO_BUCKET: умолчания нет'         нет "$r"
compose_default APP_VERSION >/dev/null && r=есть || r=нет
check 'APP_VERSION: «:?» — не умолчание'    нет "$r"

echo
echo '── check_new_vars: что дописываем сами, а о чём спрашиваем ──'
cat > new/env.example <<'ENV'
APP_VERSION=
# Порт веб-интерфейса.
WEB_PORT=8080
# Модель распознавания.
OLLAMA_MODEL=
# Каталог резервных копий.
BACKUP_DIR=./backups
# Ведро в MinIO.
MINIO_BUCKET=bhs-crg
ENV
printf 'APP_VERSION=0.150.0\n' > .env
TARGET=0.151.0; CURRENT=0.150.0

out="$( AUTOFILL=(); check_new_vars 2>&1 )"; rc=$?
check 'ключ без умолчания — остановка' 1 "$rc"
printf '%s\n' "$out" | grep -q 'MINIO_BUCKET'  && r=да || r=нет; check 'он назван'                    да "$r"
printf '%s\n' "$out" | grep -q 'без умолчания' && r=да || r=нет; check 'сказано, что умолчания нет'   да "$r"
printf '%s\n' "$out" | grep -q 'BACKUP_DIR OLLAMA_MODEL WEB_PORT' && r=да || r=нет
check 'остальные три — впишем сами' да "$r"

sed -i '/MINIO_BUCKET/d' new/env.example
AUTOFILL=()
check_new_vars >/dev/null 2>&1; rc=$?     # БЕЗ подстановки команды: массив нужен здесь, а не в подоболочке
check 'спорных ключей нет — остановки нет' 0 "$rc"
check 'список на дозапись' 'BACKUP_DIR OLLAMA_MODEL WEB_PORT' "${AUTOFILL[*]}"

# Образец, расходящийся с умолчанием, — решение человека: версия советует не то, что подставится.
sed -i 's|^WEB_PORT=8080|WEB_PORT=9090|' new/env.example
AUTOFILL=()
out="$( check_new_vars 2>&1 )"; rc=$?
check 'расхождение образца с умолчанием — остановка' 1 "$rc"
printf '%s\n' "$out" | grep -q 'без строки подставится «8080»' && r=да || r=нет
check 'названы оба значения' да "$r"
sed -i 's|^WEB_PORT=9090|WEB_PORT=8080|' new/env.example

echo
echo '── apply_autofill: пишем во ВРЕМЕННЫЙ .env, рабочий не трогаем ──'
AUTOFILL=(); check_new_vars >/dev/null 2>&1
cp .env .env.tmp
apply_autofill .env.tmp >/dev/null
check 'рабочий .env не тронут'        1              "$(grep -c . .env)"
check 'значение — из образца'         'WEB_PORT=8080' "$(grep '^WEB_PORT=' .env.tmp)"
check 'пустое значение дописано пустым' 'OLLAMA_MODEL=' "$(grep '^OLLAMA_MODEL=' .env.tmp)"
grep -q '# Порт веб-интерфейса.' .env.tmp && r=да || r=нет
check 'комментарий из образца перенесён' да "$r"
grep -q 'Добавлено обновлением 0.150.0 → 0.151.0' .env.tmp && r=да || r=нет
check 'помечено, каким обновлением'      да "$r"

echo
echo '── check_compose_drift: полный diff уходит в файл, экран его не вмещает ──'
printf 'name: bhs-crg\nservices:\n  api:\n    ports: ["8080:80"]\n' > docker-compose.yml.release
{ printf 'name: my-crg\nservices:\n  api:\n    ports: ["9999:80"]\n'
  for i in $(seq 1 200); do echo "# правка $i"; done; } > docker-compose.yml
RELEASE_REF=docker-compose.yml.release; COMPOSE_MERGED=0; STAMP=test
out="$(check_compose_drift 2>&1)"; rc=$?
check 'правленый compose — остановка' 1 "$rc"
check 'полный diff сохранён' да "$( [ -f compose-diff-test.patch ] && echo да || echo нет )"
[ "$(wc -l < compose-diff-test.patch)" -gt 60 ] && r=да || r=нет
check 'в файле больше, чем показано на экране' да "$r"
printf '%s\n' "$out" | grep -q 'compose-diff-test.patch' && r=да || r=нет
check 'путь к файлу назван в остановке' да "$r"

echo
echo '── pull_images: три попытки, не одна ──'
sleep() { :; }                      # пауза между попытками в тесте ни к чему
attempts=0
compose() { attempts=$((attempts + 1)); [ "$attempts" -ge 3 ]; }
pull_images .env >/dev/null 2>&1
check 'успех на третьей попытке' 0 "$?"
check 'попыток ровно три'        3 "$attempts"
attempts=0
compose() { attempts=$((attempts + 1)); return 1; }
pull_images .env >/dev/null 2>&1
check 'три отказа — отказ'       1 "$?"
check 'больше трёх не пробует'   3 "$attempts"
unset -f compose sleep

echo
echo '── latest_release: разбор номера версии из адреса выпуска ──'
# Единственная проверка, которой нужна сеть. Обрыв связи — не поломка скрипта, поэтому она
# пропускается, а не роняет прогон: красный прогон обязан означать «код сломан».
latest="$(latest_release)"
if [ -z "$latest" ]; then
    echo '  пропуск: до github.com не достучаться'
else
    printf '%s\n' "$latest" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$' && r=да || r=нет
    check "номер разобран (получено: $latest)" да "$r"
fi

printf '\nИТОГО: ок %d, фейлов %d\n' "$ok" "$bad"
[ "$bad" -eq 0 ]
