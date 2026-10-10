#!/usr/bin/env bash
#
# Сторож конвертера офисных файлов (issue #1267): сервис `converter` из docker-compose.yml.
#
#   bash deploy/converter.tests.sh
#
# Нужны Docker, сеть (образ и контрольный выход наружу), python3 и pdftotext (пакет poppler-utils).
# Свой pdftotext можно назвать переменной: PDFTOTEXT='docker run --rm -i образ pdftotext'.
#
# Зачем отдельный файл, а не раздел в update.tests.sh: там проверяется логика без Docker, здесь —
# ЖИВОЙ контейнер, поднятый из того самого compose-файла, который едет заказчику. Иначе это не
# проверить: каждое из условий ниже при нарушении даёт код 200 и PDF, похожий на настоящий.
#
#   • Локаль чисел. Без неё «3 834,16» выводится как «3,834.16» — сумма счёта становится другой
#     строкой, и распознавание читает уже её.
#   • Сеть. Документ не должен мочь ничего подгрузить и ничего никуда отправить.
#   • Согласие стенда с поставкой. Сервис объявлен дважды — в поставке и в дев-стенде, — и
#     разъехаться эти два блока могут только молча.

set -uo pipefail

# В Git Bash (машина разработчика на Windows) пути в аргументах docker переписываются сами, и
# `-o /dev/null` внутри контейнера превращается в файл «nul». Поэтому переписывание выключено, а
# пути этой машины приводятся к её виду явно. В Linux обе строки ничего не делают.
export MSYS_NO_PATHCONV=1
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

HERE="$(native "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)")"
DEPLOY="$HERE/docker-compose.yml"
DEV="$HERE/../docker-compose.yml"
REF="$HERE/converter-reference.xlsx"
PROJECT="crg-converter-test-$$"
read -r -a PDFTOTEXT <<< "${PDFTOTEXT:-pdftotext}"
# На Windows имя python3 занято заглушкой магазина приложений: она находится, но не запускается.
PYTHON=python3; "$PYTHON" -c 'pass' >/dev/null 2>&1 || PYTHON=python

SB="$(native "$(mktemp -d)")"
# У compose-файла поставки обязательна только версия; остальное для одного сервиса не нужно.
printf 'APP_VERSION=0.0.0\n' > "$SB/env"
compose() { docker compose -p "$PROJECT" -f "$DEPLOY" --env-file "$SB/env" "$@" 2>>"$SB/compose.log"; }
# shellcheck disable=SC2317 # вызывается ловушкой
cleanup() { compose down -v --remove-orphans >/dev/null; rm -rf "$SB"; }
trap cleanup EXIT

ok=0; bad=0
check() { # check «что проверяем» ожидание факт
    if [ "$2" = "$3" ]; then ok=$((ok + 1)); printf '  ok   %s\n' "$1"
    else bad=$((bad + 1)); printf '  ФЕЙЛ %s\n       ждали: %s\n       факт:  %s\n' "$1" "$2" "$3"; fi
}
yes_no() { if "$@" >/dev/null 2>&1; then echo да; else echo нет; fi; }
has() { if printf '%s' "$1" | grep -q -- "$2"; then echo да; else echo нет; fi; }
# Запрос изнутри контейнера конвертера: curl в образе есть, им же пользуется его проверка здоровья.
inside() { compose exec -T converter "$@"; }

for tool in docker "$PYTHON" "${PDFTOTEXT[0]}"; do
    command -v "$tool" >/dev/null 2>&1 || { echo "не найден $tool: проверять нечем" >&2; exit 1; }
done

echo '── стенд разработчика объявляет тот же сервис, что поставка ──'
# Сравнивается РАЗОБРАННЫЙ compose, а не текст: порядок строк и комментарии различаться вправе.
# Сеть сравнивается тоже: именно она делает сервис отрезанным от мира.
cat > "$SB/pick.py" <<'PY'
import json, sys
config = json.load(sys.stdin)
service = config["services"]["converter"]
keep = ["image", "environment", "command", "read_only", "tmpfs", "cap_drop", "security_opt", "deploy", "ports",
        "healthcheck", "restart"]
picked = {key: service.get(key) for key in keep}
picked["networks"] = {name: config["networks"][name].get("internal", False) for name in service.get("networks", {})}
print(json.dumps(picked, sort_keys=True, ensure_ascii=False))
PY
service_of() { # $1 — compose-файл; печатает то, что обязано совпадать
    docker compose -f "$1" --env-file "$SB/env" config --format json 2>/dev/null | "$PYTHON" "$SB/pick.py"
}
deploy_service="$(service_of "$DEPLOY")"
check 'сервис в поставке найден' да "$(has "$deploy_service" '"image": "gotenberg/gotenberg:')"
# «Не нашлось» у стенда печатается словами: два пустых ответа иначе сошлись бы как равные.
dev_service="$(service_of "$DEV")"
check 'образ, окружение, команда, ограничения, проверка здоровья и сеть совпадают' "$deploy_service" "${dev_service:-в дев-стенде сервис не найден}"
check 'порт на хост не опубликован' да "$(has "$deploy_service" '"ports": null')"
check 'сеть сервиса одна, и она внутренняя' да "$(has "$deploy_service" '"networks": {"converter": true}')"
# Снятие зависшего процесса живьём не проверить: для этого нужен документ, на котором LibreOffice
# виснет. Проверяется настройка, без которой процесс не снимается (см. комментарий в compose).
check 'новый процесс LibreOffice на каждый документ' да "$(has "$deploy_service" '"--libreoffice-restart-after=1"')"

echo '── сервис поднимается из compose-файла поставки ──'
compose up -d converter >/dev/null
cid="$(compose ps -q converter | head -1)"
state=''
for _ in $(seq 1 60); do
    state="$(docker inspect -f '{{.State.Health.Status}}' "$cid" 2>/dev/null || true)"
    [ "$state" = healthy ] && break
    sleep 2
done
check 'готов (проверка здоровья)' healthy "$state"
if [ "$state" != healthy ]; then
    cat "$SB/compose.log"; docker logs --tail 30 "$cid" 2>&1
    printf '\nИТОГО: ок %d, фейлов %d\n' "$ok" "$bad"; exit 1
fi
image="$(docker inspect -f '{{.Config.Image}}' "$cid")"

echo '── локаль чисел: таблица → PDF с текстовым слоем ──'
inside curl -sS -o - -F 'files=@-;filename=schet.xlsx' http://localhost:3000/forms/libreoffice/convert < "$REF" > "$SB/out.pdf"
check 'ответ — PDF' '%PDF-' "$(head -c 5 "$SB/out.pdf")"
# Разделитель тысяч в русской локали — неразрывный пробел (обычный или узкий): приводим к обычному.
layer() { "${PDFTOTEXT[@]}" - - 2>/dev/null | sed -e 's/\xc2\xa0/ /g' -e 's/\xe2\x80\xaf/ /g'; }
text="$(layer < "$SB/out.pdf")"
check 'текстовый слой есть: кириллица читается' да  "$(has "$text" 'Счёт на оплату № 17')"
check 'число записано по-русски: 3 834,16'      да  "$(has "$text" '3 834,16')"
check 'и не по-американски: 3,834.16'           нет "$(has "$text" '3,834\.16')"

echo '── ответы сервиса, на которые опирается приложение (issue #1268) ──'
# Рядом с эталонной таблицей лежит PDF, который сервис из неё сделал: на этой паре тесты приложения
# проверяют свои постусловия, не поднимая конвертер. Пара обязана оставаться парой — иначе тесты
# зелены на образе, которого сервис уже не выдаёт.
check 'эталонный PDF из репозитория несёт тот же текст, что отдаёт сервис' "$text" "$(layer < "$HERE/converter-reference.pdf")"

answer() { inside curl -s -o /dev/null -w '%{http_code}' -F "files=@-;filename=$1" http://localhost:3000/forms/libreoffice/convert; }
# Новый формат под паролем сервис называет сам — по этому слову приложение и отличает «защищён»
# от «не удалось».
locked="$(inside curl -s -w ' %{http_code}' -F 'files=@-;filename=file.xlsx' http://localhost:3000/forms/libreoffice/convert < "$HERE/converter-protected.xlsx")"
check 'книга под паролем: отказ 400'           400 "${locked##* }"
check 'и в отказе сказано про пароль'          да  "$(has "$locked" password)"
# Дальше — то, чего сервис НЕ делает, и поэтому делает приложение. Если проверка покраснела оттого,
# что сервис поумнел, — хорошо: поправьте ожидание, а проверки приложения оставьте.
check 'старую книгу под паролем он паролем не объясняет (500)' 500 "$(answer file.xls < "$HERE/converter-protected.xls")"
check 'текст с расширением таблицы превращает в PDF (200)'     200 "$(printf 'просто текст, не таблица\n' | answer file.xlsx)"

echo '── сеть: изнутри наружу не выйти, а сосед по сети сервис видит ──'
# Сначала контроль: тот же образ в обычной сети наружу ВЫХОДИТ. Без него отказ ниже ничего не
# значил бы: так же отказал бы прогон на машине без интернета или образ без curl.
check 'контроль: из обычной сети внешний адрес достижим' да  "$(yes_no docker run --rm --entrypoint curl "$image" -sS -m 20 -o /dev/null https://github.com)"
check 'из сети конвертера — нет (по имени)'              нет "$(yes_no inside curl -sS -m 5 -o /dev/null https://github.com)"
check 'из сети конвертера — нет (по адресу)'             нет "$(yes_no inside curl -sS -m 5 -o /dev/null http://1.1.1.1)"
# Так к сервису приходит `api`: по имени, из той же внутренней сети.
check 'сосед по сети видит сервис по имени'              да  "$(yes_no docker run --rm --network "${PROJECT}_converter" --entrypoint curl "$image" -fsS -m 10 http://converter:3000/health)"

# `api` стоит сразу в двух сетях — обычной и этой. Контейнер в таком положении обязан сохранить
# и выход наружу, и разрешение внешних имён: потеряй он их, приложение перестало бы видеть
# облачные движки и проверку обновлений, а база и хранилище работали бы как ни в чём не бывало.
docker network create "${PROJECT}_plain" >/dev/null
reach='curl -sS -m 20 -o /dev/null https://github.com && curl -fsS -m 10 -o /dev/null http://converter:3000/health'
both="$(docker create --network "${PROJECT}_plain" --entrypoint sh "$image" -c "$reach")"
docker network connect "${PROJECT}_converter" "$both"
check 'в двух сетях сразу: и наружу, и к сервису'        да  "$(yes_no docker start -a "$both")"
docker rm -f "$both" >/dev/null; docker network rm "${PROJECT}_plain" >/dev/null

echo '── лишние двери закрыты ──'
code() { inside curl -s -o /dev/null -w '%{http_code}' "$@"; }
check 'операции над PDF выключены' 404 "$(code -X POST http://localhost:3000/forms/pdfengines/merge)"
check 'метрики выключены'          404 "$(code http://localhost:3000/prometheus/metrics)"

printf '\nИТОГО: ок %d, фейлов %d\n' "$ok" "$bad"
[ "$bad" -eq 0 ]
