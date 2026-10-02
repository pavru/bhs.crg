#!/usr/bin/env bash
#
# Проверка get-version.sh и bump-version.sh — в песочнице, без сети и без правки репозитория.
#
# Зачем набор для двух коротких скриптов. Запускает их не человек, а workflow после слияния, и
# результат сразу уходит коммитом в master: ошибку здесь никто не увидит глазами до тех пор, пока
# версия не окажется в выпуске. Набор гоняется в CI на каждый PR (issue #1153).

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GET="$HERE/get-version.sh"
BUMP="$HERE/bump-version.sh"

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
sample 1.18.2 | sed 's/$/\r/' > props.xml
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
echo '── Настоящий файл ──'
# Всё выше — про образец. Эта проверка — про то, что разбор подходит к файлу, который на самом
# деле лежит в репозитории: поменяй кто-нибудь его разметку, образец остался бы зелёным.
unset VERSION_FILE
real="$(bash "$GET" 2>&1)"
check 'версия репозитория читается'     да "$([[ "$real" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && echo да || echo "нет: $real")"

echo
echo "итог: ok=$ok, фейлов=$bad"
[ "$bad" -eq 0 ]
