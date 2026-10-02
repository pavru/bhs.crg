#!/usr/bin/env bash
#
# Проверка скриптов версии — get-version.sh, bump-version.sh, unbumped-merges.sh — в песочнице,
# без сети и без правки репозитория.
#
# Зачем набор для трёх коротких скриптов. Запускает их не человек, а workflow после слияния, и
# результат сразу уходит коммитом в master: ошибку здесь никто не увидит глазами до тех пор, пока
# версия не окажется в выпуске. Набор гоняется в CI на каждый PR (issue #1153).

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GET="$HERE/get-version.sh"
BUMP="$HERE/bump-version.sh"
UNBUMPED="$HERE/unbumped-merges.sh"

SB="$(mktemp -d)"
trap 'cd /; rm -rf "$SB"' EXIT
cd "$SB" || exit 1

ok=0; bad=0
check() { # check «что проверяем» ожидание факт
    if [ "$2" = "$3" ]; then ok=$((ok + 1)); printf '  ok   %s\n' "$1"
    else bad=$((bad + 1)); printf '  ФЕЙЛ %s\n       ждали: %s\n       факт:  %s\n' "$1" "$2" "$3"; fi
}

# Образец собран из того, на чём легко споткнуться: тег версии, упомянутый в комментарии (его
# нельзя ни посчитать второй версией, ни переписать), атрибут Version у пакета и строка версии с
# нестандартным отступом.
sample() { # sample ВЕРСИЯ
    printf '%s\n' \
        '<Project>' \
        '  <!-- Единая версия продукта: <Version>0.0.0</Version> ниже поднимает CI. -->' \
        '  <PropertyGroup>' \
        "        <Version>$1</Version>" \
        '  </PropertyGroup>' \
        '  <ItemGroup>' \
        '    <PackageReference Include="Npgsql" Version="10.0.2" />' \
        '  </ItemGroup>' \
        '</Project>'
}
export VERSION_FILE="$SB/props.xml"

echo
echo '── Чтение ──'
sample 1.18.2 > props.xml
check 'версия читается'                 '1.18.2' "$(bash "$GET")"
check '--file называет файл версии'     "$VERSION_FILE" "$(bash "$GET" --file)"

echo
echo '── Повышение ──'
check 'по умолчанию — PATCH'            '1.18.3' "$(bash "$BUMP")"
check 'и в файле она же'                '1.18.3' "$(bash "$GET")"
check 'patch явно'                      '1.18.4' "$(bash "$BUMP" patch)"
check 'minor обнуляет PATCH'            '1.19.0' "$(bash "$BUMP" minor)"
sample 1.9.9 > props.xml
check 'девятка не переносится в MINOR'  '1.9.10' "$(bash "$BUMP")"
check 'в stdout — одна строка'          '1' "$(bash "$BUMP" | wc -l | tr -d ' ')"

echo
echo '── Остальной файл не тронут ──'
sample 1.18.2 > before.xml
cp before.xml props.xml
bash "$BUMP" > /dev/null
check 'изменилась ровно одна строка'    '1' "$(diff before.xml props.xml | grep -c '^>')"
check 'версия пакета осталась'          '1' "$(grep -c 'Version="10.0.2"' props.xml)"
check 'упоминание в комментарии цело'   '1' "$(grep -c '<Version>0.0.0</Version> ниже' props.xml)"
check 'отступ строки сохранён'          '        <Version>1.18.3</Version>' "$(grep '^ *<Version>' props.xml)"
# awk, а не `sed 's/$/\r/'`: `\r` в замене понимает только GNU sed, BSD дописал бы букву «r».
sample 1.18.2 | awk '{ printf "%s\r\n", $0 }' > props.xml
bash "$BUMP" > /dev/null
check 'CRLF: версия поднята'            '1.18.3' "$(bash "$GET")"
check 'CRLF: переводы строк сохранены'  '9' "$(grep -c $'\r$' props.xml)"

echo
echo '── Отказы ──'
# Каждый отказ обязан оставить файл нетронутым: скрипт зовут перед коммитом в master, и «отказал,
# но успел записать» означало бы повышение наполовину.
refuses() { # refuses «что проверяем» команда…
    local name="$1"; shift
    cp props.xml snapshot.xml
    "$@" > out.txt 2> err.txt
    local rc=$?
    check "$name: код не ноль"          да "$([ $rc -ne 0 ] && echo да || echo нет)"
    check "$name: stdout пуст"          '' "$(cat out.txt)"
    check "$name: причина названа"      да "$([ -s err.txt ] && echo да || echo нет)"
    check "$name: файл не тронут"       да "$(cmp -s snapshot.xml props.xml && echo да || echo нет)"
}
sample 1.18.2 > props.xml
refuses 'неизвестный уровень'  bash "$BUMP" major
sample 1.18 > props.xml
refuses 'два числа вместо трёх' bash "$BUMP"
sample 1.08.2 > props.xml
refuses 'ведущий ноль'         bash "$BUMP"
sample 1.18.2-rc.1 > props.xml
refuses 'суффикс у номера'     bash "$BUMP"
{ sample 1.18.2; echo '<Version>2.0.0</Version>'; } > props.xml
refuses 'две строки версии'    bash "$BUMP"
sample 1.18.2 | grep -v '<Version>' > props.xml
refuses 'строки версии нет'    bash "$BUMP"
rm props.xml
check 'файла нет: get отказывает'       да "$(bash "$GET" > /dev/null 2>&1 && echo нет || echo да)"

echo
echo '── Граница: за какие слияния версия не поднята ──'
# Настоящий git в песочнице: граница ищется по истории, и подменить её нечем. Слияние здесь —
# обычный коммит в master (так выглядит squash); merge-коммит проверен отдельно, ниже.
mkdir repo && cd repo || exit 1
git init -q -b master .
git config core.autocrlf false
g() { git -c user.name=t -c user.email=t@t -c commit.gpgsign=false "$@"; }
export VERSION_FILE="$SB/repo/props.xml"
merge() { echo "$1" >> log.txt; g add -A; g commit -q -m "$1"; }                # слияние без правки версии
version() { sample "$1" > props.xml; g add -A; g commit -q -m "$2"; }           # коммит, меняющий версию
# «вид заголовок;вид заголовок;» — хеши выброшены, порядок как в выводе.
pending() { bash "$UNBUMPED" 2> /dev/null | sed -E 's/^([a-z]+) [0-9a-f]+ /\1 /' | tr '\n' ';'; }

version 1.18.2 'PR сам поднял версию (#1)'
check 'слияние, само изменившее версию, — граница'  '' "$(pending)"
merge 'Фикс (#2)'
check 'слияние после границы'            'after Фикс (#2);' "$(pending)"
check 'строка — вид, полный хеш, заголовок' "after $(git rev-parse HEAD) Фикс (#2)" "$(bash "$UNBUMPED")"
version 1.18.3 'chore: bump version to 1.18.3 [skip bump]'
check 'после коммита повышения пусто — цикла нет' '' "$(pending)"

merge 'Фича (#3)'; merge 'Опечатка [skip bump] (#4)'; merge 'Фикс (#5)'
check '[skip bump] в заголовке отсеян'   'after Фикс (#5);after Фича (#3);' "$(pending)"
sed 's/Единая версия/Общая версия/' props.xml > props.tmp && cat props.tmp > props.xml && rm props.tmp
g add -A; g commit -q -m 'Комментарий в файле версии (#6)'
check 'правка комментария — не граница'  'after Комментарий в файле версии (#6);after Фикс (#5);after Фича (#3);' "$(pending)"

# Ручная правка версии поверх неучтённых слияний: PATCH они уже не требуют, но метку version:minor
# на них спросить надо — иначе она пропала бы без следа.
version 1.18.4 'PR со своим повышением (#7)'
check 'до ручной правки — видом before'  'before Комментарий в файле версии (#6);before Фикс (#5);before Фича (#3);' "$(pending)"
merge 'Фикс (#8)'
check 'before и after вместе'            'before Комментарий в файле версии (#6);before Фикс (#5);before Фича (#3);after Фикс (#8);' "$(pending)"
version 1.19.0 'chore: bump version to 1.19.0 [skip bump]'
check 'после повышения before не повторяется' '' "$(pending)"
merge 'Фикс (#9)'
version 2.0.0 'Версия 2.0.0 (#10)'
check 'ручная правка подняла MAJOR — спрашивать не о чем' '' "$(pending)"

# Слияние merge-коммитом: версию меняет коммит ИЗ ВЕТКИ, а границей обязан стать сам merge-коммит.
g checkout -q -b branch
version 2.1.0 'в ветке: 2.1.0'; merge 'в ветке: ещё коммит'
g checkout -q master
g merge -q --no-ff branch -m 'Merge pull request #11'
check 'merge-коммит с правкой версии — граница' '' "$(pending)"
merge 'Фикс (#12)'
check 'и слияние после него видно'       'after Фикс (#12);' "$(pending)"

cd "$SB" && mkdir empty && cd empty || exit 1
git init -q -b master . && git config core.autocrlf false
echo x > other.txt; g add -A; g commit -q -m 'коммит без файла версии'
sample 1.0.0 > props.xml
export VERSION_FILE="$SB/empty/props.xml"
bash "$UNBUMPED" > out.txt 2> err.txt; rc=$?
check 'границы нет: код не ноль'         да "$([ $rc -ne 0 ] && echo да || echo нет)"
check 'границы нет: stdout пуст'         '' "$(cat out.txt)"
check 'границы нет: причина названа'     да "$(grep -q 'не нашёлся коммит' err.txt && echo да || echo нет)"
cd "$SB" || exit 1

echo
echo '── Настоящий файл ──'
# Всё выше — про образец. Эта проверка — про то, что разбор подходит к файлу, который на самом
# деле лежит в репозитории: поменяй кто-нибудь его разметку, образец остался бы зелёным.
unset VERSION_FILE
real="$(bash "$GET" 2>&1)"
check 'версия репозитория читается'     да "$([[ "$real" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && echo да || echo "нет: $real")"

echo
echo "итог: ok=$ok, фейлов=$bad"
[ "$bad" -eq 0 ]
