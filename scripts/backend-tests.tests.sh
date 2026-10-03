#!/usr/bin/env bash
#
# Проверка backend-tests.sh — в песочнице, без .NET и без базы: вместо `dotnet` стоит подделка,
# которая записывает, как её позвали, и отвечает по сценарию.
#
# Зачем набор для скрипта в сотню строк. Он стоит между тестами и ОБЯЗАТЕЛЬНОЙ проверкой: ошибка в
# сведении двух кодов возврата в один означает зелёный шаг при красных тестах — и узнать об этом
# неоткуда, потому что зелёный шаг никто не открывает. Набор гоняется в CI на каждый PR
# (issue #1164).

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUN="$HERE/backend-tests.sh"

SB="$(mktemp -d)"
trap 'cd /; rm -rf "$SB"' EXIT
cd "$SB" || exit 1

ok=0; bad=0
check() { # check «что проверяем» ожидание факт
    if [ "$2" = "$3" ]; then ok=$((ok + 1)); printf '  ok   %s\n' "$1"
    else bad=$((bad + 1)); printf '  ФЕЙЛ %s\n       ждали: %s\n       факт:  %s\n' "$1" "$2" "$3"; fi
}
has() { # has «что проверяем» образец текст — образец есть в тексте (строкой, не выражением)
    case "$3" in *"$2"*) ok=$((ok + 1)); printf '  ok   %s\n' "$1" ;;
    *) bad=$((bad + 1)); printf '  ФЕЙЛ %s\n       не нашлось: %s\n' "$1" "$2" ;; esac
}
hasnt() { # hasnt «что проверяем» образец текст — образца в тексте нет
    case "$3" in *"$2"*) bad=$((bad + 1)); printf '  ФЕЙЛ %s\n       нашлось лишнее: %s\n' "$1" "$2" ;;
    *) ok=$((ok + 1)); printf '  ok   %s\n' "$1" ;; esac
}

# ── Подделка dotnet ───────────────────────────────────────────────────────────────────────────
# Сценарий лежит файлами в $STUB: <поток>.out — что напечатать, <поток>.rc — чем ответить,
# <поток>.delay — сколько секунд помедлить, <поток>.hang — зависнуть, build.rc — чем ответить на
# сборку. Поток подделка узнаёт так же, как настоящие тесты узнают свою базу, — по BHS_TEST_DB.
#
# ⚠️ Каждый «прогон» ждёт, пока начнётся соседний, и без него отказывает. Это и есть проверка
# одновременности: запусти скрипт потоки по очереди, первый не дождался бы второго. Мерить вместо
# этого время значило бы завести проверку, которая краснеет на медленном раннере.
export STUB="$SB/stub"
mkdir -p "$SB/bin"
cat > "$SB/bin/dotnet" <<'STUBEOF'
#!/usr/bin/env bash
printf '%s|db=%s|lang=%s|%s\n' "$1" "${BHS_TEST_DB:-}" "${DOTNET_CLI_UI_LANGUAGE:-}" "$*" >> "$STUB/calls"
case "$1" in
    build) exit "$(cat "$STUB/build.rc" 2>/dev/null || echo 0)" ;;
    test)
        n="${BHS_TEST_DB##*_s}"
        other=$((3 - n))
        : > "$STUB/started-$n"
        for _ in $(seq 1 100); do [ -e "$STUB/started-$other" ] && break; sleep 0.1; done
        [ -e "$STUB/started-$other" ] || { echo "соседний поток не начался: потоки идут по очереди"; exit 9; }
        cat "$STUB/$n.out" 2>/dev/null
        [ -e "$STUB/$n.delay" ] && sleep "$(cat "$STUB/$n.delay")"
        # Зависший прогон. О том, что его погасили, говорит он сам — файлом: спрашивать «жив ли
        # процесс» ненадёжно, погашенный и ещё не прибранный родителем на вид тоже жив.
        if [ -e "$STUB/$n.hang" ]; then
            trap 'kill $! 2>/dev/null; : > "$STUB/killed-$n"; exit 143' TERM
            : > "$STUB/hung-$n"
            sleep 60 & wait $!
        fi
        exit "$(cat "$STUB/$n.rc" 2>/dev/null || echo 0)" ;;
esac
STUBEOF
chmod +x "$SB/bin/dotnet"
export PATH="$SB/bin:$PATH"

passed() { printf 'Passed!  - Failed:     0, Passed:  %s, Skipped:     0, Total:  %s, Duration: 1 m 2 s - BHS.CRG.Tests.dll (net10.0)\n' "$1" "$1"; }
scene() { # scene — чистый сценарий: оба потока зелёные, 2000 и 1000 тестов
    rm -rf "$STUB"; mkdir -p "$STUB"
    passed 2000 > "$STUB/1.out"; passed 1000 > "$STUB/2.out"
}
run() { out=$(bash "$RUN" "$@" 2>&1); rc=$?; calls=$(cat "$STUB/calls" 2>/dev/null); }
verdict() { # verdict НОМЕР — итоговая строка потока без времени: оно от прогона к прогону своё
    grep "^Поток $1:" <<<"$out" | sed 's/ за [0-9]*:[0-9]*//'
}
unset BHS_TEST_DB GITHUB_ACTIONS BACKEND_TESTS_MAX_SKEW

echo
echo '── Оба потока зелёные ──'
scene; run
check 'код возврата — 0'                        '0' "$rc"
has   'итог первого потока'                     'Поток 1: 2000 тестов' "$out"
has   'итог второго потока'                     'Поток 2: 1000 тестов' "$out"
has   'сумма по двум потокам'                   'Всего тестов: 3000' "$out"
check 'сборка — один раз'                       '1' "$(grep -c '^build|' <<<"$calls")"
check 'прогонов — два'                          '2' "$(grep -c '^test|' <<<"$calls")"
check 'оба прогона — без сборки'                '2' "$(grep '^test|' <<<"$calls" | grep -c -- '--no-build')"
has   'база первого потока'                     'db=bhs_crg_test_s1|' "$calls"
has   'база второго потока'                     'db=bhs_crg_test_s2|' "$calls"
check 'язык вывода назван обоим'                '2' "$(grep '^test|' <<<"$calls" | grep -c 'lang=en|')"
hasnt 'потоки шли одновременно'                 'потоки идут по очереди' "$out"

echo
echo '── Своя база и свои аргументы ──'
scene; BHS_TEST_DB=bhs_crg_wt7 run --no-build -c Debug
check 'код возврата — 0'                        '0' "$rc"
check '--no-build: сборки нет'                  '0' "$(grep -c '^build|' <<<"$calls")"
has   'имя базы — от BHS_TEST_DB, поток 1'      'db=bhs_crg_wt7_s1|' "$calls"
has   'имя базы — от BHS_TEST_DB, поток 2'      'db=bhs_crg_wt7_s2|' "$calls"
check 'аргументы дошли до обоих прогонов'       '2' "$(grep '^test|' <<<"$calls" | grep -c -- '-c Debug$')"
scene; run -c Release
has   'аргументы дошли и до сборки'             '-c Release' "$(grep '^build|' <<<"$calls")"
# `--no-build` не первым: раньше он уезжал в `dotnet build`, и тот отказывал неизвестным ключом.
scene; run -c Debug --no-build
check '--no-build не первым: код 0'             '0' "$rc"
check '--no-build не первым: сборки нет'        '0' "$(grep -c '^build|' <<<"$calls")"
check 'и прогонам он достался по одному разу'   '2' "$(grep '^test|' <<<"$calls" | grep -o -- '--no-build' | wc -l | tr -d ' ')"
check 'остальное дошло до обоих прогонов'       '2' "$(grep '^test|' <<<"$calls" | grep -c -- '-c Debug$')"

echo
echo '── Свой фильтр — отказ, а не два фильтра в одной команде ──'
for form in '--filter X' '--filter=X' '--no-build -c Debug --filter X'; do
    # shellcheck disable=SC2086 — форма нарочно разбивается на аргументы
    scene; run $form
    check "«$form»: код 2"                      '2' "$rc"
    check "«$form»: dotnet не зван"             '' "$calls"
done
has   'сказано, чем гонять один фильтр'         'обычным dotnet test' "$out"

echo
echo '── Имя базы: суффикс потока обязан уместиться ──'
# 36 байт — предел: с «_s1» выходит 39, столько тесты отводят имени базы прогона.
fits='bhs_crg_xxxxxxxxxxxxxxxxxxxxxxxxxxxx'
scene; BHS_TEST_DB="$fits" run --no-build
check 'длина образца — 36 байт'                 '36' "$(printf %s "$fits" | wc -c | tr -d ' ')"
check '36 байт: код 0'                          '0' "$rc"
scene; BHS_TEST_DB="${fits}x" run --no-build
check '37 байт: код 2'                          '2' "$rc"
check '37 байт: dotnet не зван'                 '' "$calls"
has   'названа переменная'                      'BHS_TEST_DB' "$out"
# Считаем байты, а не знаки: кириллица в имени занимает по два.
scene; BHS_TEST_DB='bhs_crg_стенд_разработчика_номер' run --no-build
check '32 знака, но 56 байт: код 2'             '2' "$rc"

echo
echo '── Фильтры: второй поток и его отрицание ──'
filters=$(bash "$RUN" --filters)
first=$(sed -n 1p <<<"$filters"); second=$(sed -n 2p <<<"$filters")
check '--filters печатает две строки'           '2' "$(wc -l <<<"$filters" | tr -d ' ')"
# Отрицание «a или b» есть «не a и не b»: те же слагаемые, `~` → `!~`, `|` → `&`.
check 'первый — точное отрицание второго'       "$first" "$(sed -e 's/~/!~/g' -e 's/|/\&/g' <<<"$second")"
hasnt 'во втором нет «и»'                       '&' "$second"
hasnt 'в первом нет «или»'                      '|' "$first"
scene; run
has   'первому потоку — первый фильтр'          "--filter $first " "$(grep 'db=bhs_crg_test_s1|' <<<"$calls")"
has   'второму потоку — второй фильтр'          "--filter $second " "$(grep 'db=bhs_crg_test_s2|' <<<"$calls")"

echo
echo '── Красный поток красит всё ──'
failing() { printf '  Failed Some.Test [12 ms]\n  Error Message:\n   ожидали одно, получили другое\nFailed!  - Failed:     1, Passed:   999, Skipped:     0, Total:  1000, Duration: 1 m - BHS.CRG.Tests.dll (net10.0)\n'; }
scene; failing > "$STUB/1.out"; echo 1 > "$STUB/1.rc"; run
check 'упал первый — код 1'                     '1' "$rc"
check 'первый назван упавшим'                   'Поток 1: 1000 тестов — УПАЛ' "$(verdict 1)"
check 'второй при этом назван прошедшим'        'Поток 2: 1000 тестов — прошёл' "$(verdict 2)"
has   'текст отказа виден'                      'ожидали одно, получили другое' "$out"
scene; failing > "$STUB/2.out"; echo 1 > "$STUB/2.rc"; run
check 'упал второй — код 1'                     '1' "$rc"
check 'второй назван упавшим'                   'Поток 2: 1000 тестов — УПАЛ' "$(verdict 2)"
scene; failing > "$STUB/1.out"; echo 1 > "$STUB/1.rc"; failing > "$STUB/2.out"; echo 1 > "$STUB/2.rc"; run
check 'упали оба — код 1'                       '1' "$rc"
# Процесс, убитый посреди прогона, итоговой строки не печатает: число тестов неизвестно, код есть.
scene; echo 'The active test run was aborted. Reason: Test host process crashed' > "$STUB/2.out"; echo 1 > "$STUB/2.rc"; run
check 'оборванный прогон — код 1'               '1' "$rc"
check 'и назван упавшим, а не пустым'           'Поток 2: 0 тестов — УПАЛ' "$(verdict 2)"

echo
echo '── Пустой поток — отказ, а не «прошёл» ──'
# Ровно так отвечает настоящий `dotnet test`, когда под фильтр не подошло ничего: код 0.
scene; echo 'No test matches the given testcase filter `…` in BHS.CRG.Tests.dll' > "$STUB/2.out"; run
check 'код возврата — 1'                        '1' "$rc"
has   'поток назван пустым'                     'Поток 2: 0 тестов — ПУСТ' "$(verdict 2)"
has   'и сказано, что править'                  'SECOND_STREAM' "$out"
scene; passed 0 > "$STUB/1.out"; run
check 'ноль тестов в итоговой строке — тоже'    '1' "$rc"

echo
echo '── Итог не разобран — отказ, но не «пуст» ──'
# Код 0, а строки «Total: N» нет: у dotnet test сменился вид итога. Сказать «деление устарело»
# значило бы отправить чинить буквы, с которыми всё в порядке.
scene; echo 'Test summary: total: 1000, failed: 0, succeeded: 1000, skipped: 0' > "$STUB/2.out"; run
check 'код возврата — 1'                        '1' "$rc"
has   'поток назван неразобранным'              'Поток 2: 0 тестов — НЕ РАЗОБРАН' "$(verdict 2)"
hasnt 'и не пустым'                             'ПУСТ' "$out"
hasnt 'буквы не обвинены'                       'SECOND_STREAM' "$out"

echo
echo '── Прервали посреди прогона — вывод напечатан, потоки погашены ──'
# Второй поток печатает отказ и зависает; скрипту приходит TERM — так шаг отменяет Actions.
scene; echo '  Failed Some.Hanging.Test [12 ms]' > "$STUB/2.out"; : > "$STUB/2.hang"
bash "$RUN" --no-build > "$SB/abort.out" 2>&1 & runner=$!
for _ in $(seq 1 100); do [ -e "$STUB/hung-2" ] && break; sleep 0.1; done
check 'второй поток завис'                      'да' "$([ -e "$STUB/hung-2" ] && echo да || echo нет)"
kill -TERM "$runner"; wait "$runner"; rc=$?
out=$(cat "$SB/abort.out")
check 'код возврата — 143'                      '143' "$rc"
has   'вывод зависшего потока напечатан'        'Some.Hanging.Test' "$out"
has   'и помечен как неоконченный'              'Поток 2 — вывод до остановки' "$out"
has   'вывод закончившего — тоже'               'Поток 1 — вывод до остановки' "$out"
for _ in $(seq 1 50); do [ -e "$STUB/killed-2" ] && break; sleep 0.1; done
check 'зависший прогон погашен'                 'да' "$([ -e "$STUB/killed-2" ] && echo да || echo нет)"

echo
echo '── Потоки разъехались — предупреждение, не отказ ──'
scene; echo 2 > "$STUB/2.delay"; BACKEND_TESTS_MAX_SKEW=0 run --no-build
check 'код возврата — 0'                        '0' "$rc"
has   'перекос назван'                          'разъехались' "$out"
has   'и сказано, что править'                  'SECOND_STREAM' "$out"
scene; echo 2 > "$STUB/2.delay"; GITHUB_ACTIONS=true BACKEND_TESTS_MAX_SKEW=0 run --no-build
has   'в Actions — предупреждением прогона'     '::warning::' "$out"
scene; echo 2 > "$STUB/2.delay"; run --no-build
hasnt 'в пределах порога — молчит'              'разъехались' "$out"
scene; echo 2 > "$STUB/2.delay"; failing > "$STUB/1.out"; echo 1 > "$STUB/1.rc"; BACKEND_TESTS_MAX_SKEW=0 run --no-build
hasnt 'красному прогону не до перекоса'         'разъехались' "$out"

echo
echo '── Сборка упала — потоки не начинаются ──'
scene; echo 1 > "$STUB/build.rc"; run
check 'код возврата — 1'                        '1' "$rc"
check 'прогонов нет'                            '0' "$(grep -c '^test|' <<<"$calls")"

echo
echo '── Вывод в Actions ──'
scene; failing > "$STUB/2.out"; echo 1 > "$STUB/2.rc"; GITHUB_ACTIONS=true run
has   'прошедший поток свёрнут'                 '::group::Поток 1' "$out"
hasnt 'упавший — развёрнут'                     '::group::Поток 2' "$out"
has   'отказ помечен для сводки прогона'        '::error::' "$out"
scene; GITHUB_ACTIONS=true run
hasnt 'зелёный прогон отказом не помечен'       '::error::' "$out"
scene; run
hasnt 'вне Actions групп нет'                   '::group::' "$out"

echo
echo "Итог: $ok прошло, $bad упало"
[ "$bad" -eq 0 ]
