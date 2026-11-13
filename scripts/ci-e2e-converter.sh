#!/usr/bin/env bash
# Конвертер офисных файлов для живых прогонов в CI — ветвь шага подготовки (`ci.yml`, работа e2e).
#
# Прогон счетов (issue #1270) прикладывает Excel и ждёт рядом с формой его вид для чтения. Серверные
# тесты ходят к подставному конвертеру — настоящий встречается с приложением только здесь.
#
# Образ ЧИТАЕМ из поставки, как и хранилище. Настройки названы те, от которых зависит ответ прогона:
# русская локаль и перезапуск после каждого документа. Остальное — сеть без выхода наружу,
# ограничения контейнера, согласие стенда с поставкой — сторожит `deploy/converter.tests.sh` в
# работе «Скрипт обновления»; здесь порт опубликован на петлю, потому что сервер запущен на хосте.
#
# Ветвью, а не отдельным шагом до сборки: загрузка образа и подъём LibreOffice идут, пока
# собирается сервер. Ветвь кончается, только когда конвертер ответил на /health, — сервер
# запускают после всего шага и застают конвертер готовым.

set -euo pipefail

CONVERTER_IMAGE=$(sed -n 's|^ *image: *\(gotenberg/gotenberg:[^ ]*\)$|\1|p' deploy/docker-compose.yml | head -1)
if [ -z "$CONVERTER_IMAGE" ]; then
    echo "::error::В deploy/docker-compose.yml не нашёлся образ конвертера (gotenberg/gotenberg:…) — брать неоткуда"
    exit 1
fi
echo "конвертер: $CONVERTER_IMAGE"
docker run -d --name converter -p 127.0.0.1:3910:3000 \
    -e LANG=ru_RU.UTF-8 -e LC_ALL=ru_RU.UTF-8 \
    "$CONVERTER_IMAGE" gotenberg --api-timeout=30s --libreoffice-restart-after=1 --libreoffice-auto-start
for _ in $(seq 1 60); do
    if curl -fsS http://127.0.0.1:3910/health >/dev/null 2>&1; then echo "конвертер готов"; exit 0; fi
    sleep 2
done
echo "::error::Конвертер офисных файлов не поднялся за две минуты"
docker logs --tail 50 converter 2>&1 || true
exit 1
