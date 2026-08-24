#!/usr/bin/env bash
#
# Обновление установки BHS.CRG одной командой.
#
#   ./update.sh 0.146.0     — обновить на версию
#   ./update.sh --rollback  — вернуть прежнюю версию, пока миграции не применились
#
# Устройство скрипта подчинено одному правилу: МЕХАНИКУ он берёт на себя, а РЕШЕНИЯ оставляет
# человеку — останавливаясь с точным вопросом. «Как-нибудь слить» compose-файл или «подставить
# что-нибудь» в новую переменную опаснее, чем инструкция: инструкция хотя бы не притворяется, что
# справилась. Поэтому там, где §8 DEPLOYMENT.md описывает словами тихий отказ, здесь стоит явная
# остановка (issue #837).
#
# Порядок шагов взят из §8 и держится на том же принципе: всё, что может отказать — недоступный
# реестр, неразобранный compose, недостающая переменная, — происходит ДО первой остановки
# контейнеров. Дойдя до `up -d`, вы уже знаете, что обновление состоится.

set -euo pipefail

REPO="pavru/bhs.crg"
RELEASE_URL="https://github.com/$REPO/releases/download"
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
NEW_DIR="new"                       # сюда кладутся файлы целевой версии; сюда же человек переносит свои правки
# Эталон — copy того compose, который скрипт положил в прошлый раз: файл версии либо ваш, с
# перенесёнными правками. Сравнение с ним отвечает на единственный нужный вопрос — «правили ли
# файл ПОСЛЕ прошлого обновления».
RELEASE_REF="docker-compose.yml.release"
STAMP="$(date +%Y%m%d-%H%M%S)"

FORCE=0            # обойти проверку фоновых задач
NO_BACKUP=0        # не снимать дамп (сняли иначе)
COMPOSE_MERGED=0   # правки в new/docker-compose.yml перенесены человеком
ACCEPT_DEFAULTS=0  # новые переменные оставить в умолчаниях compose
ROLLBACK=0
TARGET=""

# ── Разговор с человеком ────────────────────────────────────────────────────────
# Три уровня, и они не взаимозаменяемы: say — ход дела, warn — «прочтите, но идём дальше»,
# stop — «дальше решает человек». Последний всегда называет, ЧТО сделать, чтобы продолжить;
# остановка без следующего шага — это тот же тихий отказ, только громкий.
say()  { printf '%s\n' "$*"; }
warn() { printf '\n! %s\n' "$*" >&2; }
stop() { printf '\n╳ %s\n' "$*" >&2; exit 1; }
head2() { printf '\n── %s ──\n' "$*"; }

# ── Разбор аргументов ───────────────────────────────────────────────────────────
usage() {
    cat <<'USAGE'
Обновление установки BHS.CRG.

  ./update.sh ВЕРСИЯ        обновить на указанную версию (например 0.146.0)
  ./update.sh --rollback    вернуть прежнюю версию (пока миграции не применились)

Ключи (каждый — осознанный обход одной из остановок):
  --force             обновляться, несмотря на незавершённые фоновые задачи
  --no-backup         не снимать дамп базы (вы сняли копию другим способом)
  --compose-merged    ваши правки уже перенесены в new/docker-compose.yml
  --accept-defaults   новые переменные оставить в умолчаниях compose-файла
  -h, --help          эта справка

Запускать из каталога, где лежат docker-compose.yml и .env (в клоне репозитория — из deploy/).
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --rollback)        ROLLBACK=1 ;;
        --force)           FORCE=1 ;;
        --no-backup)       NO_BACKUP=1 ;;
        --compose-merged)  COMPOSE_MERGED=1 ;;
        --accept-defaults) ACCEPT_DEFAULTS=1 ;;
        -h|--help)         usage; exit 0 ;;
        -*)                usage >&2; stop "Неизвестный ключ: $1" ;;
        *)
            [ -z "$TARGET" ] || stop "Версия указана дважды: «$TARGET» и «$1»."
            TARGET="${1#v}"   # «v0.146.0» и «0.146.0» — одно и то же
            ;;
    esac
    shift
done

# ── Чтение .env ─────────────────────────────────────────────────────────────────
# Читаем ГРЕПОМ, а не `source .env`: в файле лежат пароли и ключи со спецсимволами, и исполнять
# его ради двух значений — лишний способ получить сюрприз (`$` в пароле, перевод строки, `rm` в
# значении). Кавычки по краям снимаем — Compose их тоже не считает частью значения.
env_get() {
    local key="$1" file="${2:-.env}" line
    [ -f "$file" ] || return 0
    line="$(grep -E "^[[:space:]]*$key[[:space:]]*=" "$file" | tail -1 || true)"
    [ -n "$line" ] || return 0
    line="${line#*=}"
    line="${line%\"}"; line="${line#\"}"
    line="${line%\'}"; line="${line#\'}"
    printf '%s' "$line" | sed -e 's/[[:space:]]*$//'
}

env_keys() { grep -oE '^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*[[:space:]]*=' "$1" | tr -d ' \t=' | sort -u; }

compose() { docker compose "$@"; }

# ── Общий предполёт ─────────────────────────────────────────────────────────────
preflight_common() {
    command -v docker >/dev/null 2>&1 || stop "Docker не найден. Установка — Приложение А в DEPLOYMENT.md."
    docker compose version >/dev/null 2>&1 || stop \
        "Нет плагина «docker compose» (через пробел). Версия v1 (docker-compose) не годится — Приложение А."
    [ -f docker-compose.yml ] || stop \
        "В текущем каталоге нет docker-compose.yml. Запускайте из каталога установки; в клоне репозитория это deploy/."
    [ -f .env ] || stop \
        "В текущем каталоге нет .env. Запускайте из каталога установки; в клоне репозитория это deploy/."

    CURRENT="$(env_get APP_VERSION)"
    [ -n "$CURRENT" ] || stop \
        "В .env не задан APP_VERSION — непонятно, с какой версии обновляемся. Впишите текущую версию и повторите."
    # Видит ли compose эту установку. Проверка стоит первой, потому что её отсутствие даёт самый
    # сбивающий с толку симптом: правка `name:` (или запуск не из того каталога) уводит все команды
    # в ДРУГОЙ проект — работающая система остаётся работать, но перестаёт быть видна, и дальше
    # скрипт спотыкался бы об «service postgres is not running» на снятии дампа, ничего не объясняя.
    # Поймано ровно так на стенде.
    if [ -z "$(compose ps --format '{{.Name}}' 2>/dev/null | head -1)" ]; then
        stop "$(cat <<EOF
docker compose не видит ни одного контейнера этой установки.

Так бывает в двух случаях: запуск не из того каталога — или в docker-compose.yml менялось имя
проекта (\`name:\` в первой строке), а контейнеры подняты под прежним. Система при этом работает,
просто командам не видна. Проверьте: docker compose ps  и  docker ps
EOF
)"
    fi

    WEB_PORT="$(env_get WEB_PORT)"; WEB_PORT="${WEB_PORT:-8080}"
    PGUSER="$(env_get POSTGRES_USER)"; PGUSER="${PGUSER:-postgres}"
    PGDB="$(env_get POSTGRES_DB)"; PGDB="${PGDB:-bhs_crg}"
}

# Сравнение версий: возвращает 0, если $1 строго больше $2.
version_gt() {
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]
}

# ── Откат ───────────────────────────────────────────────────────────────────────
# Ровно то, что §8 разрешает делать руками, и ровно с той же оговоркой: откат возможен, ПОКА
# миграции не применились. Проверяем это по журналу api, а не по вере в лучшее — старый образ с
# новой схемой работать не обязан, и «вроде обошлось» здесь стоит целой базы.
do_rollback() {
    preflight_common
    head2 "Откат с версии $CURRENT"

    local applied
    applied="$(compose logs api --tail=4000 2>/dev/null | grep -c 'Applying migration' || true)"
    if [ "${applied:-0}" -gt 0 ]; then
        local dump
        dump="$(ls -1t backups/pre-update-*.dump 2>/dev/null | head -1 || true)"
        stop "$(cat <<EOF
Миграции уже применены — в журнале api есть строки «Applying migration».

Возврат образа тут не поможет: прежняя версия не обязана понимать новую схему. Откат делается
только восстановлением дампа, причём базу перед этим ПЕРЕСОЗДАЮТ (§9 DEPLOYMENT.md) — дамп,
залитый поверх мигрировавшей схемы, оставит смесь новой схемы со старыми данными.
${dump:+
Свежий дамп этого обновления: $dump}
EOF
)"
    fi

    local prev_compose prev_env
    prev_compose="$(ls -1t docker-compose.yml.prev-* 2>/dev/null | head -1 || true)"
    prev_env="$(ls -1t .env.prev-* 2>/dev/null | head -1 || true)"
    [ -n "$prev_compose" ] && [ -n "$prev_env" ] || stop \
        "Нет сохранённых docker-compose.yml.prev-* и .env.prev-* — откатывать не на что. Прежнюю версию впишите в .env вручную."

    local prev_version
    prev_version="$(env_get APP_VERSION "$prev_env")"
    say "Возвращаем: $prev_compose и $prev_env (версия $prev_version)."

    cp "$prev_compose" docker-compose.yml
    cp "$prev_env" .env
    # Эталон убираем: он остался от версии, с которой мы только что ушли, и следующее обновление
    # сравнивало бы вернувшийся файл с чужим — показав «у вас правки» там, где их нет. Без эталона
    # скрипт просто скачает compose текущей версии заново (проверено откатом на стенде).
    rm -f "$RELEASE_REF"
    compose up -d
    wait_for_version "$prev_version" || stop \
        "Прежняя версия не ответила за отведённое время. Журнал: docker compose logs --tail=50 api"
    say ""
    say "Откат завершён: система работает на $prev_version."
}

# ── Ожидание результата ─────────────────────────────────────────────────────────
# Ждём не «контейнер запустился», а «система отвечает ТОЙ версией»: запущенный контейнер ничего не
# обещает — api поднимается до пяти с лишним минут (20 с + 30×10 с healthcheck), и всё это время
# web отдаёт 502. Поэтому единственный честный признак успеха — номер версии в ответе.
wait_for_version() {
    local want="$1" deadline=$((SECONDS + 420)) got=""
    say "Ждём ответа системы на http://localhost:$WEB_PORT/api/version (до 7 минут)…"
    while [ $SECONDS -lt $deadline ]; do
        got="$(curl -fsS --max-time 5 "http://localhost:$WEB_PORT/api/version" 2>/dev/null \
               | grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]+"' | grep -oE '[0-9][^"]*' || true)"
        [ "$got" = "$want" ] && { say "Ответила версия $got."; return 0; }
        sleep 5
    done
    [ -n "$got" ] && warn "Система отвечает версией $got, а ожидалась $want."
    return 1
}

# ── Разовые оговорки — только тем, кого касаются ────────────────────────────────
# Инструкция рассказывает про них всем и навсегда; скрипт знает, с какой версии вы идёте, и молчит
# про то, что вас не касается.
legacy_notes() {
    if ! version_gt "$CURRENT" "0.138.999" && compose ps --services --status running 2>/dev/null | grep -qx ollama; then
        warn "$(cat <<'EOF'
У вас работает контейнер ollama, а с 0.139.0 он поднимается только по профилю Compose.

Обновление его не выключит: `up -d` контейнер отключённого профиля не трогает. Но исчезнет он
навсегда в день, когда контейнер уберут — явным `rm`, чисткой `docker system prune`, переносом на
другой сервер. Решите сейчас (§7 DEPLOYMENT.md):
  • пользуетесь  — впишите в .env COMPOSE_PROFILES=ollama и OLLAMA_MODEL=…
  • не пользуетесь — docker compose rm -sf ollama ollama-init, и выключите движок в
    «Настройка системы → Настройки → Поиск и распознавание» (сохранённая модель живёт в базе,
    очистку .env она переживёт).
EOF
)"
    fi

    if ! version_gt "$CURRENT" "0.136.999"; then
        warn "$(cat <<'EOF'
Вы переходите с поставки сборкой из исходников на готовые образы (0.137.0). Тома, имена сервисов и
данные те же. Локально собранные образы после обновления можно удалить: docker image prune.
EOF
)"
    fi

    if ! version_gt "$CURRENT" "0.90.999"; then
        warn "$(cat <<'EOF'
Разово при переходе с версии ниже 0.91.0: том dp_keys создавался от root, и владелец у него сам не
поменяется. Сервер сможет читать ключи, но не сможет выписать очередной — и через несколько недель
ссылки сброса пароля начнут отказывать. Выполните ОДИН раз (ключ --entrypoint обязателен, без него
аргументы достанутся серверу приложений и он молча запустится вместо chown):

  docker compose run --rm --user root --entrypoint chown api -R app:app /app/dp-keys
EOF
)"
    fi
}

# ── Фоновые задачи ──────────────────────────────────────────────────────────────
# Спрашиваем базу, а не интерфейс: пилюля задач показывает только СВОИ задачи, а помешает как раз
# чужая — чужая сборка комплекта или распознавание оборвутся и сами не возобновятся.
check_jobs() {
    local out
    out="$(compose exec -T postgres psql -U "$PGUSER" -d "$PGDB" -t -A \
            -c "select \"Status\" || ': ' || count(*) from jobs where \"Status\" in ('Queued','Running') group by 1;" \
            2>/dev/null || true)"
    out="$(printf '%s' "$out" | grep -v '^[[:space:]]*$' || true)"
    [ -n "$out" ] || return 0

    if [ "$FORCE" -eq 1 ]; then
        warn "Незавершённые фоновые задачи ($out) — обновляемся, потому что указан --force. Новая версия пометит их неудавшимися."
        return 0
    fi
    stop "$(cat <<EOF
В системе есть незавершённые фоновые задачи:
$out

Перезапуск их оборвёт: сборка комплекта и распознавание сами не возобновятся, новая версия при
старте пометит их неудавшимися. Дождитесь завершения — или запустите с --force, если решили, что
эти задачи не жалко.
EOF
)"
}

# ── Дамп базы ───────────────────────────────────────────────────────────────────
# Дамп, а не копия приложения: схема мигрирует при старте новой версии, отката миграций нет, и
# вернуть прошлую схему можно ТОЛЬКО из дампа. Снимаем до любой остановки контейнеров — на живой
# базе, пока всё работает.
backup_db() {
    if [ "$NO_BACKUP" -eq 1 ]; then
        warn "Дамп не снимаем — указан --no-backup. Убедитесь, что копия у вас есть: без неё откат после миграций невозможен."
        return 0
    fi
    mkdir -p backups
    DUMP="backups/pre-update-$CURRENT-to-$TARGET-$STAMP.dump"
    say "Снимаем дамп базы в $DUMP …"
    if ! compose exec -T postgres pg_dump -U "$PGUSER" -d "$PGDB" -Fc > "$DUMP" 2>/tmp/bhs-pgdump.err; then
        rm -f "$DUMP"
        stop "$(printf 'Не удалось снять дамп базы:\n%s\n\nОбновление не начато — система работает на %s.' \
                "$(tail -5 /tmp/bhs-pgdump.err 2>/dev/null)" "$CURRENT")"
    fi
    [ -s "$DUMP" ] || { rm -f "$DUMP"; stop "Дамп получился пустым. Обновление не начато."; }
    say "Дамп готов: $DUMP ($(du -h "$DUMP" | cut -f1))."
}

# ── Файлы целевой версии ────────────────────────────────────────────────────────
fetch_release_files() {
    mkdir -p "$NEW_DIR"
    if [ "$COMPOSE_MERGED" -eq 1 ]; then
        [ -f "$NEW_DIR/docker-compose.yml" ] || stop \
            "Указан --compose-merged, но $NEW_DIR/docker-compose.yml нет. Запустите без ключа — скрипт скачает файл версии."
        say "Берём $NEW_DIR/docker-compose.yml как есть: вы перенесли в него свои правки."
    else
        curl -fsSL "$RELEASE_URL/v$TARGET/docker-compose.yml" -o "$NEW_DIR/docker-compose.yml" \
            || stop "Не удалось скачать docker-compose.yml версии $TARGET."
    fi
    curl -fsSL "$RELEASE_URL/v$TARGET/env.example" -o "$NEW_DIR/env.example" \
        || stop "Не удалось скачать env.example версии $TARGET."
}

# ── Остановка 1: ваши правки в docker-compose.yml ───────────────────────────────
# Самый тихий отказ из §8, ставший громким. Заменив файл целиком, вы не получите НИКАКОЙ ошибки:
# при изменённом `name:` работающая система просто перестанет быть видна командам, а `up -d`
# поднимет рядом вторую, пустую установку (проверено на стенде). Поэтому сравниваем текущий файл с
# нетронутым файлом его версии — и при расхождении отдаём решение человеку.
check_compose_drift() {
    [ "$COMPOSE_MERGED" -eq 0 ] || return 0

    if [ ! -f "$RELEASE_REF" ]; then
        say "Эталона $RELEASE_REF нет (первый запуск скрипта) — скачиваем compose текущей версии $CURRENT для сравнения…"
        curl -fsSL "$RELEASE_URL/v$CURRENT/docker-compose.yml" -o "$RELEASE_REF" 2>/dev/null || {
            rm -f "$RELEASE_REF"
            stop "$(cat <<EOF
Не удалось скачать compose версии $CURRENT — сравнить ваш файл не с чем, а заменять его вслепую
нельзя: ваши правки (имя проекта, порты, лимиты, свои тома) исчезли бы без единой ошибки.

Сверьте сами и продолжите, когда решите:
  diff docker-compose.yml $NEW_DIR/docker-compose.yml
  # перенесите свои правки в $NEW_DIR/docker-compose.yml
  ./update.sh $TARGET --compose-merged
EOF
)"
        }
    fi

    if diff -q "$RELEASE_REF" docker-compose.yml >/dev/null 2>&1; then
        say "Ваш docker-compose.yml совпадает с файлом версии $CURRENT — замена безопасна."
        return 0
    fi

    stop "$(cat <<EOF
Ваш docker-compose.yml отличается от файла версии $CURRENT — в нём есть правки, и замена унесла бы
их молча. Слева — файл версии, справа — ваш:

$(diff -u "$RELEASE_REF" docker-compose.yml | sed 's/^/  /' | head -60)

Перенесите свои правки в $NEW_DIR/docker-compose.yml (там уже лежит файл версии $TARGET) и
запустите снова:

  ./update.sh $TARGET --compose-merged
EOF
)"
}

# ── Остановка 2: переменные, которых у вас ещё нет ──────────────────────────────
# Новая версия добавляет переменные, и обновившись «только образами», вы получаете новую настройку
# в её умолчании — не зная об этом. Поэтому список новых ключей показываем всегда; продолжить можно
# либо вписав значения, либо явным --accept-defaults, и то лишь когда у КАЖДОГО нового ключа есть
# умолчание в compose-файле.
check_new_vars() {
    local new_keys missing=() k comment
    new_keys="$(env_keys "$NEW_DIR/env.example")"
    for k in $new_keys; do
        grep -qE "^[[:space:]]*$k[[:space:]]*=" .env || missing+=("$k")
    done
    [ "${#missing[@]}" -gt 0 ] || return 0

    local without_default=()
    for k in "${missing[@]}"; do
        grep -qE "\\\$\{$k(:-|-)" "$NEW_DIR/docker-compose.yml" || without_default+=("$k")
    done

    local listing=""
    for k in "${missing[@]}"; do
        # Берём НАЧАЛО блока комментария над ключом, а не последние его строки: у ближайших к
        # ключу строк обрублено начало фразы, и подсказка читается как обрывок разговора.
        comment="$(awk -v key="$k" '
            /^[[:space:]]*#/ { block = block $0 "\n"; next }
            $0 ~ "^[[:space:]]*" key "[[:space:]]*=" { printf "%s", block; exit }
            { block = "" }' "$NEW_DIR/env.example" | head -4 || true)"
        listing+="  $k"
        if printf '%s\n' "${without_default[@]}" | grep -qx "$k"; then
            listing+="   ← без умолчания: без значения система не поднимется"
        fi
        listing+=$'\n'
        [ -n "$comment" ] && listing+="$(printf '%s\n' "$comment" | sed 's/^/      /')"$'\n'
    done

    if [ "$ACCEPT_DEFAULTS" -eq 1 ] && [ "${#without_default[@]}" -eq 0 ]; then
        warn "$(printf 'Новые переменные оставляем в умолчаниях compose (--accept-defaults):\n%s' "$listing")"
        return 0
    fi

    local hint="Впишите их в .env (образец с пояснениями — $NEW_DIR/env.example) и запустите снова."
    if [ "${#without_default[@]}" -eq 0 ]; then
        hint+=$'\n'"Или запустите с --accept-defaults: у всех новых переменных есть умолчания в compose-файле."
    else
        hint+=$'\n'"--accept-defaults здесь не поможет: у переменных выше нет умолчаний, система без них не поднимется."
    fi

    stop "$(printf 'Версия %s добавила переменные, которых нет в вашем .env:\n\n%s\n%s' "$TARGET" "$listing" "$hint")"
}

# ── Самообновление скрипта ──────────────────────────────────────────────────────
# Не молча: скрипт версии N обновляет НА N+k, и если целевой релиз принёс другой update.sh —
# сегодняшний может не знать о шаге, который в той версии обязателен.
check_self_update() {
    local tmp; tmp="$(mktemp)"
    if curl -fsSL "$RELEASE_URL/v$TARGET/update.sh" -o "$tmp" 2>/dev/null && [ -s "$tmp" ]; then
        if ! diff -q "$tmp" "$SELF" >/dev/null 2>&1; then
            rm -f "$tmp"
            stop "$(cat <<EOF
В релизе $TARGET другой update.sh — обновите сам скрипт и повторите запуск:

  curl -fsSL $RELEASE_URL/v$TARGET/update.sh -o "$SELF" && chmod +x "$SELF"
  ./update.sh $TARGET
EOF
)"
        fi
    fi
    rm -f "$tmp"
}

# ── Обновление ──────────────────────────────────────────────────────────────────
do_update() {
    preflight_common
    [ -n "$TARGET" ] || { usage >&2; stop "Не указана версия. Список выпусков: https://github.com/$REPO/releases"; }

    head2 "Обновление $CURRENT → $TARGET"

    [ "$TARGET" != "$CURRENT" ] || stop "В .env уже указана версия $TARGET — обновлять не на что."
    version_gt "$TARGET" "$CURRENT" || stop "$(cat <<EOF
$TARGET ниже текущей $CURRENT — это не обновление, а откат, и так он не делается: старая версия не
обязана понимать мигрировавшую схему. Пока миграции не применились — ./update.sh --rollback;
после — восстановление дампа с пересозданием базы (§9 DEPLOYMENT.md).
EOF
)"

    curl -fsIL --max-time 20 "$RELEASE_URL/v$TARGET/docker-compose.yml" -o /dev/null 2>/dev/null || stop \
        "Выпуска $TARGET нет или до github.com не достучаться. Список: https://github.com/$REPO/releases"

    check_self_update
    check_jobs
    fetch_release_files
    check_compose_drift
    check_new_vars
    legacy_notes
    backup_db

    # Предполёт на ВРЕМЕННОМ .env: APP_VERSION в рабочий файл пишем только после успешного pull.
    # Иначе оборвавшаяся загрузка оставила бы .env с новой версией при старых образах — состояние,
    # в котором `up -d` уже нельзя выполнить, а понять это по файлам нельзя.
    head2 "Предполётная проверка"
    local env_new=".env.$TARGET.$STAMP"
    if grep -qE '^[[:space:]]*APP_VERSION[[:space:]]*=' .env; then
        sed -E "s|^[[:space:]]*APP_VERSION[[:space:]]*=.*|APP_VERSION=$TARGET|" .env > "$env_new"
    else
        cp .env "$env_new"; printf '\nAPP_VERSION=%s\n' "$TARGET" >> "$env_new"
    fi

    compose -f "$NEW_DIR/docker-compose.yml" --env-file "$env_new" config -q || {
        rm -f "$env_new"
        stop "Compose-файл версии $TARGET не разбирается с вашим .env. Система работает на $CURRENT."
    }
    say "Compose-файл разобран."

    say "Загружаем образы $TARGET (система пока работает на $CURRENT)…"
    compose -f "$NEW_DIR/docker-compose.yml" --env-file "$env_new" pull || {
        rm -f "$env_new"
        stop "Не удалось загрузить образы. Ничего не менялось — система работает на $CURRENT."
    }

    # Точка невозврата пройдена в безопасную сторону: образы на хосте, файлы проверены. Только
    # теперь трогаем рабочие файлы — и прежние сохраняем С ДАТОЙ, чтобы второй запуск не затёр то,
    # на что откатываться.
    head2 "Переключаем версию"
    cp docker-compose.yml "docker-compose.yml.prev-$STAMP"
    cp .env ".env.prev-$STAMP"
    say "Прежние файлы сохранены: docker-compose.yml.prev-$STAMP, .env.prev-$STAMP"

    cp "$NEW_DIR/docker-compose.yml" docker-compose.yml
    cp "$NEW_DIR/docker-compose.yml" "$RELEASE_REF"   # эталон для следующего обновления
    cp "$NEW_DIR/env.example" .env.example
    mv "$env_new" .env

    compose up -d

    head2 "Проверка"
    if wait_for_version "$TARGET"; then
        compose ps
        say ""
        say "Готово: $CURRENT → $TARGET."
        [ "$NO_BACKUP" -eq 1 ] || say "Дамп перед обновлением: $DUMP"
        say "Прежние файлы: docker-compose.yml.prev-$STAMP, .env.prev-$STAMP"
    else
        warn "Система не ответила версией $TARGET. Последние строки журнала api:"
        compose logs --tail=50 api || true
        stop "$(cat <<EOF
Обновление до $TARGET не подтвердилось.

Частые причины видны в журнале выше: не хватило переменной окружения или оборвалась миграция.
Пока в журнале api нет строк «Applying migration», вернуть прежнюю версию можно одной командой:

  ./update.sh --rollback

Если миграции уже применились — только восстановление дампа с пересозданием базы (§9):
${DUMP:-снятый вами дамп}
EOF
)"
    fi
}

if [ "$ROLLBACK" -eq 1 ]; then
    [ -z "$TARGET" ] || stop "--rollback не сочетается с номером версии: откат идёт на ту версию, что сохранена в .env.prev-*."
    do_rollback
else
    do_update
fi
