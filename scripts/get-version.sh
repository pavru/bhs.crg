#!/usr/bin/env bash
#
# Печатает версию приложения — «MAJOR.MINOR.PATCH» из <Version> в src/server/Directory.Build.props.
#
# Это единственное место, где номер записан: клиент берёт его из /api/version, выпуск — из этого же
# файла. Скрипт существует, чтобы и разбор был один: его зовёт bump-version.sh, а по нему —
# workflow «Повышение версии» (issue #1153).
#
# На всём, что не похоже на одну строку с тремя числами, скрипт ОТКАЗЫВАЕТ, а не печатает то, что
# нашлось. Пустая строка или обрывок номера в ответе выглядели бы как версия — и следующим шагом
# стали бы сообщением коммита.
#
# VERSION_FILE подменяет файл — для проверок (bump-version.tests.sh).

set -euo pipefail

FILE="${VERSION_FILE:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/src/server/Directory.Build.props}"

[ -f "$FILE" ] || { echo "get-version: не найден $FILE" >&2; exit 1; }

# `|| true`: grep без совпадений выходит с 1, и под errexit скрипт оборвался бы здесь — молча,
# строкой раньше сообщения, ради которого проверка написана.
#
# Считаем строки, которые с тега НАЧИНАЮТСЯ, а не любые его упоминания: комментарий вида
# «<Version> поднимает CI» — не вторая версия, и отказывать из-за него незачем.
count=$(grep -cE '^[[:space:]]*<Version>' "$FILE" || true)
if [ "$count" != 1 ]; then
    echo "get-version: в $FILE строк, начинающихся с <Version>, — $count, а должна быть ровно одна" >&2
    exit 1
fi

# Число без ведущих нулей: «08» арифметика bash прочла бы как восьмеричное и отказала уже в
# bump-version.sh, сообщением не про версию. [[:space:]] в конце закрывает и \r файла с CRLF.
num='(0|[1-9][0-9]*)'
version=$(sed -nE "s#^[[:space:]]*<Version>($num\.$num\.$num)</Version>[[:space:]]*\$#\1#p" "$FILE")
if [ -z "$version" ]; then
    echo "get-version: <Version> в $FILE не разобран — ждали «MAJOR.MINOR.PATCH» одной строкой" >&2
    exit 1
fi

echo "$version"
