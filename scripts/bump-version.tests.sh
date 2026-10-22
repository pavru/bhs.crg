#!/usr/bin/env bash
#
# Проверка скриптов версии — get-version.sh, bump-version.sh, unbumped-merges.sh, planning-only.sh —
# в песочнице, без сети и без правки репозитория.
#
# Зачем набор для четырёх коротких скриптов. Запускает их не человек, а workflow после слияния, и
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
echo '── Только планирование: docs/tz и openspec версию не двигают ──'
# Тот же скрипт решает в ci.yml, пропускать ли тяжёлые работы, — поэтому граница каталогов
# проверяется здесь со всех сторон: соседний каталог, перенос, merge-коммит, кириллица в имени.
mkdir plan && cd plan || exit 1
git init -q -b master . && git config core.autocrlf false
export VERSION_FILE="$SB/plan/props.xml"
PLAN="$HERE/planning-only.sh"
put() { # put ЗАГОЛОВОК ФАЙЛ… — слияние, дописавшее строку в каждый файл
    local msg="$1"; shift
    for f in "$@"; do mkdir -p "$(dirname "$f")"; echo "$msg" >> "$f"; done
    g add -A; g commit -q -m "$msg"
}
mkdir src && echo код > src/a.cs
version 1.0.0 'Начало (#1)'

put 'ТЗ (#2)' 'docs/tz/50-планирование.md'
put 'OpenSpec (#3)' openspec/changes/plan-00-epic/tasks.md openspec/specs/core/spec.md
put 'ТЗ и OpenSpec вместе (#4)' docs/tz/README.md openspec/config.yaml
check 'только docs/tz и openspec — не повод повышать' '' "$(pending)"
check 'planning-only: да — код 0'        0 "$(bash "$PLAN" HEAD^ HEAD; echo $?)"

put 'ТЗ и CLAUDE.md (#5)' 'docs/tz/50-планирование.md' CLAUDE.md
check 'один файл вне — обычное слияние'   'after ТЗ и CLAUDE.md (#5);' "$(pending)"
check 'planning-only: нет — код 1'       1 "$(bash "$PLAN" HEAD^ HEAD; echo $?)"
version 1.0.1 'chore: bump version to 1.0.1 [skip bump]'

put 'Рядом с ТЗ (#6)' docs/README.md
put 'Похожий каталог (#7)' docs/tz-old/a.md
put 'Похожий каталог в корне (#8)' openspec2/a.md
check 'docs/, docs/tz-old/, openspec2/ — не планирование' \
    'after Похожий каталог в корне (#8);after Похожий каталог (#7);after Рядом с ТЗ (#6);' "$(pending)"
version 1.0.2 'chore: bump version to 1.0.2 [skip bump]'

# Без --no-renames перенос был бы виден одним новым именем — под openspec/.
g mv src/a.cs openspec/a.cs; g commit -q -m 'Перенос кода в openspec (#9)'
check 'перенос из src в openspec — обычное слияние' 'after Перенос кода в openspec (#9);' "$(pending)"
version 1.0.3 'chore: bump version to 1.0.3 [skip bump]'

g commit -q --allow-empty -m 'Пустое слияние (#10)'
check 'пустое изменение — не планирование' 'after Пустое слияние (#10);' "$(pending)"
version 1.0.4 'chore: bump version to 1.0.4 [skip bump]'

# Merge-коммит сравнивается с первым родителем — с master до слияния, а не с последним коммитом
# ветки: иначе ветка «код, потом спецификация» прошла бы за планирование.
g checkout -q -b code-then-spec
put 'в ветке: код' src/b.cs; put 'в ветке: спецификация' openspec/b.md
g checkout -q master
g merge -q --no-ff code-then-spec -m 'Merge pull request #11'
check 'merge-коммит с кодом внутри — обычное слияние' 'after Merge pull request #11;' "$(pending)"
version 1.0.5 'chore: bump version to 1.0.5 [skip bump]'
g checkout -q -b spec-only
put 'в ветке: ТЗ' docs/tz/c.md; put 'в ветке: спецификация' openspec/c.md
g checkout -q master
g merge -q --no-ff spec-only -m 'Merge pull request #12'
check 'merge-коммит только с планированием — пропущен' '' "$(pending)"

# Ответить не удалось — код 2 и причина, а не «нет»: иначе вызывающий не отличил бы отказ от ответа.
bash "$PLAN" нет-такой HEAD > out.txt 2> err.txt; rc=$?
check 'ревизии нет: код 2'               2 "$rc"
check 'ревизии нет: причина названа'     да "$(grep -q 'не удался' err.txt && echo да || echo нет)"
bash "$PLAN" HEAD > /dev/null 2>&1; rc=$?
check 'одна ревизия вместо двух: код 2'  2 "$rc"

# Ручная правка версии без смены MINOR: слияния до неё идут видом before — и планирование среди
# них так же не в счёт.
put 'Код (#13)' src/c.cs
put 'ТЗ (#14)' docs/tz/d.md
version 1.0.6 'PR со своим повышением (#15)'
check 'before: планирование отсеяно и там' 'before Код (#13);' "$(pending)"

# Отказ planning-only.sh на середине списка: весь ответ — отказ, и в stdout ни строки. Первый вызов
# подделки отвечает «обычное слияние» — его строка уже готова к печати, когда приходит отказ.
put 'Фикс (#16)' src/d.cs
printf '%s\n' '#!/usr/bin/env bash' \
    "n=\$(cat '$SB/calls' 2>/dev/null || echo 0); echo \$((n + 1)) > '$SB/calls'" \
    '[ "$n" -eq 0 ] && exit 1; echo "подделка: отказ" >&2; exit 2' > "$SB/fake-plan.sh"
# Без права на исполнение Linux отвечает 126, и отказ скрипта был бы зелёным по чужой причине —
# Git Bash на Windows запускает файл с shebang и так. Это ловит счётчик вызовов ниже.
chmod +x "$SB/fake-plan.sh"
rm -f "$SB/calls"
PLANNING_ONLY="$SB/fake-plan.sh" bash "$UNBUMPED" > out.txt 2> err.txt; rc=$?
check 'отказ на середине: код не ноль'   да "$([ $rc -ne 0 ] && echo да || echo нет)"
check 'отказ на середине: stdout пуст'   '' "$(cat out.txt)"
check 'отказ на середине: причина названа' да "$(grep -q 'не удалось понять' err.txt && echo да || echo нет)"
check 'подделку и правда звали дважды'   2 "$(cat "$SB/calls")"

# diff.relative в настройках git не меняет ответа: unbumped-merges.sh зовёт скрипт из подкаталога.
mkdir -p sub && cd sub || exit 1
check 'diff.relative=true: ответ тот же' 0 "$(GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=diff.relative GIT_CONFIG_VALUE_0=true bash "$PLAN" HEAD~3 HEAD~2; echo $?)"
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
