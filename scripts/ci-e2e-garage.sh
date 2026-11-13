#!/usr/bin/env bash
# Хранилище Garage для живых прогонов в CI — ветвь шага подготовки (`ci.yml`, работа e2e).
#
# Не сервисом работы: сервису нельзя передать аргументы команды, а Garage без них не стартует — и,
# в отличие от MinIO, его ещё надо ИНИЦИАЛИЗИРОВАТЬ.
#
# Образ ЧИТАЕМ из ПОСТАВКИ целиком — вместе с реестром, а не одну версию: прогоны обязаны идти на
# том хранилище, что поедет в продакшн. Раньше тег не указывался вовсе, то есть бралось `latest`, и
# разойтись хранилища могли только молча (issue #878). Поставку и дев-стенд сверяет сторож в работе
# backend.
#
# Ветвью, а не отдельным шагом до сборки — загрузка образов и инициализация идут, пока собирается
# сервер (замер — docs/DEV_NOTES.md, «Живые прогоны: подготовка ветвями»).

set -euo pipefail

GARAGE_IMAGE=$(sed -n 's|^ *image: *\(dxflrs/garage:[^ ]*\)$|\1|p' deploy/docker-compose.yml | head -1)
if [ -z "$GARAGE_IMAGE" ]; then
    echo "::error::В deploy/docker-compose.yml не нашёлся образ хранилища (dxflrs/garage:…) — брать неоткуда"
    exit 1
fi
echo "образ хранилища из поставки: $GARAGE_IMAGE"

# Конфигурация — та же, что у дев-стенда и (со второго этапа) у поставки. Правим одно:
# rpc_public_addr, потому что здесь узел зовут не «garage», а по петле.
sed 's|^rpc_public_addr = .*|rpc_public_addr = "127.0.0.1:3901"|' deploy/garage.toml > /tmp/garage.toml

SECRET=6465765f6f6e6c795f7270635f7365637265745f6e6f745f666f725f6465706c

docker run -d --name garage --network host \
    -e GARAGE_RPC_SECRET="$SECRET" \
    -e GARAGE_ADMIN_TOKEN=ci-only-admin-token \
    -v /tmp/garage.toml:/etc/garage.toml:ro \
    -v garage_meta:/var/lib/garage/meta \
    -v garage_data:/var/lib/garage/data \
    "$GARAGE_IMAGE" /garage server

# Инициализация — ТЕМ ЖЕ скриптом, что поднимает дев-стенд и поедет в поставку. Повторить его
# команды здесь отдельно значило бы проверять не то, что работает у людей: разойдясь, два списка
# команд не сказали бы об этом никак.
docker build -f deploy/Dockerfile.garage-init -t garage-init deploy
docker run --rm --network host \
    -e GARAGE_RPC_SECRET="$SECRET" \
    -e GARAGE_BUCKET=bhs-crg-e2e \
    -e GARAGE_KEY_ID=GK0123456789abcdef01234567 \
    -e GARAGE_SECRET=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
    -v /tmp/garage.toml:/etc/garage.toml:ro \
    -v garage_meta:/var/lib/garage/meta \
    garage-init
