#!/usr/bin/env bash
#
# Проверка ЛОГИКИ install.sh — без Docker, без сети, без записи куда-либо, кроме песочницы.
#
# Зачем отдельный набор, если есть живой прогон. Живой прогон дорог (образы, минуты) и потому
# редок, а ломается здесь ровно то, что дёшево проверить: разбор аргументов, подстановка значений
# в .env, требования к паролю, отказ на непустом каталоге. Набор гоняется в CI на каждый PR.
#
# ⚠️ Половина проверок ниже — про поведение ПОД `errexit`. Это не педантизм: в update.sh трижды
# находилась заботливо написанная остановка, до которой управление не доходило, потому что скрипт
# обрывался строкой выше. Такие места проверяются запуском в подоболочке `( set -e; … )`.

set -uo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/install.sh"
[ -f "$SRC" ] || { echo "не найден $SRC" >&2; exit 1; }

SB="$(mktemp -d)"
trap 'cd /; rm -rf "$SB"' EXIT
cd "$SB" || exit 1

# Отрезаем «ход установки» — всё, что ниже заголовка. Иначе подключение файла запустило бы саму
# установку: скрипт не разделён на библиотеку и точку входа, и это осознанно — он должен читаться
# сверху вниз одним куском.
sed '/^# ── Ход установки/,$d' "$SRC" > lib.sh
# shellcheck disable=SC1091
source ./lib.sh
set +e

ok=0; bad=0
check() { # check «что проверяем» ожидание факт
    if [ "$2" = "$3" ]; then ok=$((ok + 1)); printf '  ok   %s\n' "$1"
    else bad=$((bad + 1)); printf '  ФЕЙЛ %s\n       ждали: %s\n       факт:  %s\n' "$1" "$2" "$3"; fi
}
run_e() { ( set -e; "$@" ) > run.out 2>&1; echo $?; }
said() { grep -q "$1" run.out && echo да || echo нет; }

echo
echo '── Требования к паролю администратора ──'
# Проверяем ЗДЕСЬ, а не узнаём от сервера: отказ в самом конце установки, когда система уже
# поднята, оставил бы страницу первого входа открытой — ровно то, ради чего шаг и делается.
check 'короткий отвергнут'          да "$(password_bad 'Ab1' >/dev/null && echo да || echo нет)"
check 'без цифры отвергнут'         да "$(password_bad 'Abcdefgh' >/dev/null && echo да || echo нет)"
check 'без заглавной отвергнут'     да "$(password_bad 'abcdefg1' >/dev/null && echo да || echo нет)"
check 'без строчной отвергнут'      да "$(password_bad 'ABCDEFG1' >/dev/null && echo да || echo нет)"
check 'годный принят'               нет "$(password_bad 'Demo12345' >/dev/null && echo да || echo нет)"
check 'причина названа словами'     'короче 8 знаков' "$(password_bad 'Ab1')"
# ⚠️ Кириллический пароль система НЕ принимает, и это проверено живьём: Identity сравнивает с
# диапазонами 'a'..'z'/'A'..'Z', поэтому «Пароль12345» получает «нет строчной буквы; нет заглавной
# буквы». Наша проверка обязана отвергать его ЗАРАНЕЕ — иначе отказ придёт в конце установки,
# когда система уже поднята и окно первого входа открыто.
check 'кириллический отвергнут'      да "$(password_bad 'Пароль12345' >/dev/null && echo да || echo нет)"
check 'и причина названа понятно'    'нет строчной латинской буквы (система требует именно латинскую)' "$(password_bad 'Пароль12345')"

echo
echo '── Подстановка значений в .env ──'
printf '# комментарий про ключ\nAPP_VERSION=\nJWT_KEY=CHANGE_ME\nWEB_PORT=8080\n' > .env
set_env APP_VERSION 0.160.0
set_env JWT_KEY 'a/b+c=d&e'
check 'версия подставлена'           'APP_VERSION=0.160.0' "$(grep '^APP_VERSION' .env)"
# Значение со слэшем и амперсандом: через sed такая замена развалилась бы — «&» означает
# «вставить найденное», «/» закрывает выражение. Поэтому подстановка сделана awk.
check 'спецсимволы не искажены'      'JWT_KEY=a/b+c=d&e' "$(grep '^JWT_KEY' .env)"
check 'комментарии сохранены'        да "$(grep -q '^# комментарий про ключ' .env && echo да || echo нет)"
check 'посторонние ключи не тронуты' 'WEB_PORT=8080' "$(grep '^WEB_PORT' .env)"
set_env WEB_PORT 8081
check 'повторная подстановка меняет' 'WEB_PORT=8081' "$(grep '^WEB_PORT' .env)"
check 'строк не прибавилось'         4 "$(wc -l < .env)"

echo
echo '── set_env: ключ закомментирован или отсутствует ──'
# Необязательные настройки в образце объявлены ЗАКОММЕНТИРОВАННЫМИ (COMPOSE_PROFILES,
# OLLAMA_MODEL). Первая редакция их не находила, ничего не меняла и не жаловалась — а скрипт при
# этом печатал «локальное распознавание включено». Тихий отказ ровно того вида, от которого этот
# репозиторий бережёт (найдено ревью).
printf '#COMPOSE_PROFILES=ollama
#OLLAMA_MODEL=qwen2.5vl:7b
WEB_PORT=8080
' > .env
set_env COMPOSE_PROFILES ollama
check 'закомментированный раскомментирован' 'COMPOSE_PROFILES=ollama' "$(grep '^COMPOSE_PROFILES' .env)"
check 'и старой строки не осталось'         0 "$(grep -c '^#COMPOSE_PROFILES' .env)"
check 'соседний комментарий не тронут'      1 "$(grep -c '^#OLLAMA_MODEL' .env)"
set_env СОВСЕМ_НОВЫЙ значение
check 'отсутствующий дописан'               'СОВСЕМ_НОВЫЙ=значение' "$(grep '^СОВСЕМ_НОВЫЙ' .env)"
check 'существующий по-прежнему заменяется' 'WEB_PORT=8080' "$(grep '^WEB_PORT' .env)"

echo
echo '── Пароль: проверки не должны зависеть от локали ──'
# В локали C диапазоны [A-ZА-Я] и [a-zа-я] сравниваются ПОБАЙТОВО, а каждая кириллическая буква
# начинается с 0xD0/0xD1 — байт попадает в оба диапазона сразу. Проверено: «пароль123» проходил
# проверку на заглавную, которой там нет, а `${#p}` мерил байты, и пятибуквенное слово считалось
# восемью знаками (найдено ревью).
check 'кириллический отвергнут и в C'  да "$(LC_ALL=C password_bad 'Пароль12345' >/dev/null && echo да || echo нет)"
# Длина меряется в СИМВОЛАХ: в локали C `${#p}` считал бы байты, и «кот12» из пяти букв прошло бы
# как восьмизначное. Здесь оно обязано отвергаться по длине, а не по буквам.
check 'короткий кириллический — по длине' 'короче 8 знаков' "$(LC_ALL=C password_bad 'кот12')"
check 'латинский годный принят и в C'  нет "$(LC_ALL=C password_bad 'Parol123' >/dev/null && echo да || echo нет)"

echo
echo '── JSON-строка для запроса регистрации ──'
# Пароль набирает человек, и кавычка или обратный слэш в нём порвали бы тело запроса — регистрация
# отвечала бы 400, а причина выглядела бы как «сервер не принял пароль».
check 'простая строка'      '"abc"'            "$(json_string 'abc')"
check 'кавычка экранирована' '"a\"b"'          "$(json_string 'a"b')"
check 'слэш экранирован'     '"a\\b"'          "$(json_string 'a\b')"
check 'кириллица как есть'   '"Администратор"' "$(json_string 'Администратор')"

echo
echo '── --reverse-proxy: проверка имени ──'
check 'обычное имя годится'      1 "$(proxy_host_bad 'docs.example.ru' >/dev/null; echo $?)"
check 'схема отвергнута'         0 "$(proxy_host_bad 'https://docs.example.ru' >/dev/null; echo $?)"
check 'слэш отвергнут'           0 "$(proxy_host_bad 'docs.example.ru/crg' >/dev/null; echo $?)"
check 'пробел отвергнут'         0 "$(proxy_host_bad 'docs example ru' >/dev/null; echo $?)"
check 'без точки отвергнуто'     0 "$(proxy_host_bad 'localhost' >/dev/null; echo $?)"
check 'и причина названа'        'это не доменное имя: нет ни одной точки (нужно, например, docs.example.ru)' "$(proxy_host_bad 'localhost')"

echo
echo '── --reverse-proxy: подстановка в НАСТОЯЩИЙ образец ──'
# Берём deploy/reverse-proxy.conf.example, а не заглушку. Заглушка проверяла бы только сам awk, а
# сломаться может связка: переформатируют образец — и подстановка перестанет попадать в строки,
# причём молча. Заодно так проверяется ВТОРОЙ proxy_pass (в location /api/backup/), которого в
# заглушке не было вовсе (найдено ревью).
cp "$(dirname "$SRC")/reverse-proxy.conf.example" .
printf 'BACKUP_MAX_ARCHIVE_MB=900
' > .env
PROXY_HOST=crg.example.org; PORT=9443; PUBLIC_URL=''
prepare_reverse_proxy > /dev/null
check 'имя подставлено'           '    server_name crg.example.org;' "$(grep 'server_name' reverse-proxy.conf)"
check 'ОБА proxy_pass с портом'   2 "$(grep -c 'http://127.0.0.1:9443' reverse-proxy.conf)"
check 'предел тела из .env + 100' '    client_max_body_size 1000m;' "$(grep 'client_max_body_size' reverse-proxy.conf)"
check 'блок остался на 80'        1 "$(grep -c 'listen 80;' reverse-proxy.conf)"
# Ищем именно ДИРЕКТИВУ: порт стенда (9443) сам содержит «443», а в шапке файла про `listen 443
# ssl` написано словами — оба совпадения ложные.
check 'блока на 443 не появилось' 0 "$(grep -c '^[^#]*listen[^#]*443' reverse-proxy.conf)"
check 'заголовки прокси на месте' да "$(grep -q 'proxy_set_header X-Forwarded-For' reverse-proxy.conf && echo да || echo нет)"
# Шапку пишем свою: в образце она объясняет, что заменить, и называет certbot с ПРИМЕРОМ домена —
# после подстановки это уже неправда, а откроют именно установленный файл.
check 'имени-примера не осталось'  0 "$(grep -c 'docs.example.ru' reverse-proxy.conf)"
check 'в шапке своё имя и certbot' да "$(grep -q 'certbot --nginx -d crg.example.org' reverse-proxy.conf && echo да || echo нет)"
check 'веб закрыт на петлю'       'WEB_BIND=127.0.0.1' "$(grep '^WEB_BIND' .env)"
check 'публичный адрес выставлен' 'APP_PUBLIC_URL=https://crg.example.org' "$(grep '^APP_PUBLIC_URL' .env)"

# Заданный человеком адрес важнее выведенного из имени: он мог поставить прокси на другом домене.
printf 'BACKUP_MAX_ARCHIVE_MB=500
' > .env
PUBLIC_URL='https://свой.адрес'
prepare_reverse_proxy > /dev/null
check 'заданный адрес не перезаписан' 'https://свой.адрес' "$PUBLIC_URL"
check 'предел по умолчанию 500+100'   '    client_max_body_size 600m;' "$(grep 'client_max_body_size' reverse-proxy.conf)"

# Образец без нужной директивы — подстановка обязана ЗАМЕТИТЬ пропажу, а не отчитаться об успехе:
# nginx без client_max_body_size рубит тело на 1 МБ, и копия не загрузится с голой страницей 413.
grep -v 'client_max_body_size' "$(dirname "$SRC")/reverse-proxy.conf.example" > reverse-proxy.conf.example
printf 'BACKUP_MAX_ARCHIVE_MB=500
' > .env
PUBLIC_URL=''
check 'пропавшая директива замечена' 1 "$(run_e prepare_reverse_proxy)"
check 'и названа поимённо'           да "$(said 'client_max_body_size')"

echo
echo '── Разбор версии Docker ──'
check 'из 24.0.7'      24 "$(major_of 24.0.7)"
check 'из 29.7.2'      29 "$(major_of 29.7.2)"

echo
echo '── Каталог установки ──'
mkdir -p занят && printf 'APP_VERSION=0.1.0\n' > занят/.env
DIR=занят
check 'установка поверх существующей: отказ' 1 "$(run_e check_target_dir)"
check 'и сказано про update.sh'              да "$(said 'update.sh')"
cd "$SB" || exit 1
DIR=свободен
check 'в пустой каталог: можно'              0 "$(run_e check_target_dir)"
cd "$SB" || exit 1
DIR=нет/такого/пока
check 'несуществующий создаётся'             0 "$(run_e check_target_dir)"
check 'и он действительно создан'            да "$([ -d "$SB/нет/такого/пока" ] && echo да || echo нет)"
cd "$SB" || exit 1

echo
echo '── Ответы в неинтерактивном режиме ──'
INTERACTIVE=0
ADMIN_EMAIL=''; ADMIN_PASSWORD='Demo12345'; PORT=8080
check 'без почты: отказ'            1 "$(run_e collect_answers)"
check 'и названа переменная'        да "$(said 'ADMIN_EMAIL')"
ADMIN_EMAIL='a@b.c'; ADMIN_PASSWORD=''
check 'без пароля: отказ'           1 "$(run_e collect_answers)"
check 'и названа переменная'        да "$(said 'ADMIN_PASSWORD')"
ADMIN_PASSWORD='слабый'
check 'слабый пароль: отказ'        1 "$(run_e collect_answers)"
check 'и сказано, чего не хватает'  да "$(said 'не короче 8')"
ADMIN_PASSWORD='Demo12345'; PORT='восемь'
check 'порт не число: отказ'        1 "$(run_e collect_answers)"
PORT=8080
check 'всё задано: проходит'        0 "$(run_e collect_answers)"

echo
echo '── Ответ ask в неинтерактивном режиме — умолчание, а не пустота ──'
check 'умолчание возвращается'      8080 "$(INTERACTIVE=0 ask 'порт' 8080)"
check 'пустое умолчание пусто'      ''   "$(INTERACTIVE=0 ask 'адрес' '')"

echo
echo '── Секреты нужного формата ──'
key="GK$(rand_hex 12)"; secret="$(rand_hex 32)"
check 'идентификатор 26 знаков'     26 "${#key}"
check 'и начинается с GK'           да "$(case "$key" in GK*) echo да ;; *) echo нет ;; esac)"
check 'секрет 64 знака'             64 "${#secret}"
check 'только шестнадцатеричные'    да "$(case "$secret" in *[!0-9a-f]*) echo нет ;; *) echo да ;; esac)"
check 'два вызова — разные'         да "$([ "$(rand_hex 32)" != "$secret" ] && echo да || echo нет)"
b64="$(rand_b64 48)"
check 'ключ подписи ≥ 32 знаков'    да "$([ "${#b64}" -ge 32 ] && echo да || echo нет)"
# `+` и `/` заменены: значение уходит в .env, а `$`-подобные символы и слэши в паролях — источник
# сюрпризов при чтении файла чем угодно, кроме нашего собственного разбора.
check 'без символов + и /'          да "$(case "$b64" in *[+/]*) echo нет ;; *) echo да ;; esac)"

echo
printf 'ИТОГО: ок %d, фейлов %d\n' "$ok" "$bad"
[ "$bad" -eq 0 ]
