#!/usr/bin/env bash
#
# Поднимает версию приложения в src/server/Directory.Build.props и печатает новую в stdout.
#
#   bump-version.sh          — PATCH + 1
#   bump-version.sh patch    — то же
#   bump-version.sh minor    — MINOR + 1, PATCH = 0
#
# Руками его запускать не нужно: версию поднимает workflow «Повышение версии» после слияния в
# master, а PR её не трогает (issue #1153). MAJOR здесь нет нарочно — это решение владельца
# продукта о выпуске, и принимается оно правкой файла в отдельном PR, а не аргументом скрипта.
#
# В stdout уходит ТОЛЬКО номер: вызывающий берёт его подстановкой $(…), и любая пояснительная
# строка оказалась бы в сообщении коммита. Всё остальное — в stderr.
#
# VERSION_FILE подменяет файл — для проверок (bump-version.tests.sh).

set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Путь спрашиваем у get-version.sh, а не вычисляем второй раз: читать версию из одного файла, а
# писать в другой — отказ, который сверка ниже назвала бы «записывали X, а читается Y».
FILE=$("$DIR/get-version.sh" --file)

level="${1:-patch}"
case "$level" in
    patch|minor) ;;
    *) echo "bump-version: уровень «$level» не поддержан — patch или minor" >&2; exit 2 ;;
esac

current=$("$DIR/get-version.sh")
IFS=. read -r major minor patch <<<"$current"
if [ "$level" = minor ]; then
    minor=$((minor + 1)); patch=0
else
    patch=$((patch + 1))
fi
new="$major.$minor.$patch"

# Меняем только то, что между тегами, и только в строке, которая с тега начинается — той самой,
# которую читает get-version.sh: отступ, перевод строки и весь остальной файл остаются байт в байт,
# упоминание тега в комментарии тоже. Через временный файл и `cat >`, а не `sed -i`: у GNU и BSD
# этот ключ пишется по-разному, а `mv` поверх сменил бы права и владельца.
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
sed -E "s#^([[:space:]]*<Version>)[^<]*(</Version>)#\1$new\2#" "$FILE" > "$tmp"
cat "$tmp" > "$FILE"

# Читаем обратно тем же разбором, которым версию будут читать все остальные. Подстановка, не
# нашедшая чего менять, завершается успехом — и без этой сверки скрипт напечатал бы номер,
# которого в файле нет.
written=$("$DIR/get-version.sh")
if [ "$written" != "$new" ]; then
    echo "bump-version: записывали $new, а в $FILE читается $written" >&2
    exit 1
fi

echo "$new"
