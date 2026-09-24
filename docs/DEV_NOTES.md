# Заметки разработчика: почему так сделано

Обоснования решений, история переходов и справочные листинги. Вынесено сюда из `CLAUDE.md`
(issue #1016): записи ценные, но нужны редко, а `CLAUDE.md` читается агентом в начале **каждой**
сессии целиком.

Правила и ловушки остались в `CLAUDE.md` — здесь только «почему». Если вы ищете, что делать,
смотрите туда; если ищете, почему именно так, — вы на месте.

---

## Стенд по HTTPS: сертификат и доступ с телефона

Относится к разделу «HTTPS на стенде» в `CLAUDE.md`.

Сертификат **самоподписанный**: одноразовый сервис `dev-tls` выпускает мини-УЦ (`dev-tls/ca.crt`) и
сертификат сервера; каталог `dev-tls/` — вне репозитория. Let's Encrypt здесь неприменим (нужно имя
в DNS и доступность по порту 80 снаружи). В поставке всё иначе: TLS терминирует nginx на хосте с
certbot — Приложение В в `docs/DEPLOYMENT.md`.

Чтобы браузер перестал ругаться, УЦ ставится в доверенные **один раз** (перевыпуск сертификата
сервера доверие не сбрасывает — ради этого он и отделён от УЦ):

```powershell
certutil -addstore -user Root dev-tls\ca.crt    # Windows, текущий пользователь
```

Открыть стенд с телефона: дописать в корневой `.env` адрес машины в локальной сети. Без него
сертификат выписан только на `localhost`, и телефон получит не предупреждение о доверии, а отказ
«выписан не на этот адрес», который установкой УЦ не лечится:

```
DEV_TLS_NAMES=192.168.1.10,crg.local   # пример: адреса и имена через запятую
DEV_HTTPS_PORT=8443                    # если 443 на машине занят
```

После правки — `docker compose up -d dev-tls && docker compose restart nginx`: nginx читает
сертификат при старте и сам перевыпуск не подхватывает. На телефон ставится тот же `dev-tls/ca.crt`.

---

## CI: состав прогона и связь с выпуском

Относится к разделу «CI» в `CLAUDE.md`. Обязательные проверки и договор об именах работ остались
там — здесь то, что нужно при разборе упавшего прогона и при выпуске.

Обязательными в настройках репозитория сделаны три из четырёх — какие именно, сказано ниже;
остальное владелец добавляет по желанию. **Но это про слияние PR, не про выпуск**: `release.yml`
спрашивает итог прогона ЦЕЛИКОМ и работы различать не умеет, поэтому падение ЛЮБОЙ из четырёх
останавливает выпуск. Для живых прогонов это ощутимо: они тянут образ
хранилище, Typst и браузер из сети. Лекарство — «Re-run failed jobs», итог прогона пересчитывается.

Живые прогоны (issue #872) поднимают приложение целиком — postgres, Garage, Typst CLI,
опубликованный API, **собранный** клиент — сеют синтетические данные (`src/client/e2e/seed.mjs`,
дамп рабочей базы невозможен: репозиторий публичный) и гоняют все восемь прогонов, 94 проверки из
97. Ручными остались три — разбиение PDF: им нужны распознанные страницы, то есть ИИ-движок,
которого в CI нет. Пропуск заявлен вслух в итоге прогона, а не сделан молча. Подробности и
таблица — `src/client/e2e/README.md`.

Выпуск прикрыт отдельно и другим способом: `release.yml` спрашивает у API итог прогона CI на своём
коммите и отказывается публиковать образы, если тот не `success`. Это не дублирование — правило
ветки смотрит на PR, а выпуск делается с уже слитого master.

---

## Поставка: прибитые версии, переход на Garage, dependabot

Относится к разделу «Документация и развёртывание» в `CLAUDE.md`.

**Версии сторонних образов прибиты точно** (issue #878) — `latest` в compose возвращать нельзя.
Compose едет вместе с выпуском, поэтому прибитая версия делает смену postgres/MinIO/ollama/nginx
событием: с датой, коммитом и откатом. С плавающим тегом новый образ приезжал в продакшн как
побочный груз обновления приложения (`update.sh` зовёт `compose pull` без списка сервисов), а
`--rollback` его не возвращал.

**Копия прежнего хранилища в нашем GHCR** (issue #882): `ghcr.io/pavru/minio` и
`ghcr.io/pavru/mc` — побайтовые копии последнего выпуска MinIO с навсегда замороженным тегом.
После перехода на Garage они не убраны НАРОЧНО: ими `update.sh` переносит файлы на установках,
которые придут со старых версий, — возможно, спустя годы. Отбор «наших» образов в `update.sh --gc`
идёт по имени `bhs.crg-*`, а не по адресу реестра, иначе эта копия попала бы под удаление.

**Хранилище — Garage, с 0.160.0** (issue #885). MinIO архивирован upstream'ом; переход сделан
целиком: поставка, дев-стенд, живые прогоны. Кода приложения он не коснулся — регион `us-east-1`
объявляет само хранилище (`deploy/garage.toml`), потому что SDK подписывает запросы им и
переубедить его нельзя: `MakeBucket` подписывается отдельно и `WithRegion` игнорирует.

**Миграция с MinIO — внутри `update.sh`** (§8.7 DEPLOYMENT.md): предполёт по месту, остановка
приложения, `mc mirror`, сверка составом и размерами, и только потом подмена файлов. Сверка НЕ
через `mc diff`: он считает различием разницу во времени, а копия всегда новее оригинала. Том
`minio_data` и копия образа MinIO в GHCR остаются — ими пользуются установки, которые придут со
старых версий.

Следит `.github/dependabot.yml` — раз в неделю, отдельным PR, который проходит весь CI. Чего он
**не** покрывает, названо там же: теги MinIO (`RELEASE.…`) — не semver, остаются ручными; мажор
postgres — не бамп, а работа с `pg_upgrade`; .NET за `ARG` Dependabot пропускает. CI берёт версии
из `deploy/docker-compose.yml`: MinIO — чтением, postgres — литералом со сторожем (образ сервисного
контейнера из файла не вычислить: `services:` разбирается до шагов).

---

## Статус первой версии

Зафиксирован на момент её завершения. С тех пор система развивается дальше, поэтому таблица —
исторический срез, а не текущее состояние.

Первая версия полностью реализована (backend + frontend + EF-миграция):

| Модуль | Статус |
|---|---|
| Auth (регистрация/вход, JWT) | ✅ |
| Каталог сущностей (CRUD) | ✅ |
| Типы документов (CRUD + схема) | ✅ |
| Шаблоны (Monaco/Typst + версионирование) | ✅ |
| Комплекты документов (CRUD + состав) | ✅ |
| Реквизиты и связи документа | ✅ |
| Генерация PDF (Typst) | ✅ |
| Документы качества, тэги, уведомления, интеграции, роли | ✅ |
| EF Core migrations | ✅ |

---

## REST API: основные маршруты

Справочный листинг. ⚠️ **Он может отставать от кода** — источник истины в
`src/server/BHS.CRG.Api/Endpoints/`. Контрактные тонкости, на которых легко ошибиться, вынесены в
`CLAUDE.md`.

```
POST   /api/auth/register           { email, password, displayName }
POST   /api/auth/login              { email, password } → { accessToken }

GET    /api/catalog?entityType=     → CatalogEntity[]
POST   /api/catalog                 { entityType, displayName, data: string(JSON) }
PUT    /api/catalog/{id}            { displayName, data: string(JSON) }
DELETE /api/catalog/{id}

GET    /api/document-types
POST   /api/document-types          { name, code, schema: string(JSON) }
PUT    /api/document-types/{id}/schema  { schema: string(JSON) }

GET    /api/templates?documentTypeId=
POST   /api/templates               { documentTypeId, name, content }      — content = Typst
PUT    /api/templates/{id}          { content }  — создаёт новую версию
                                    (запись типов/полей/шаблонов/настроек — только роль Admin)

GET    /api/document-sets/search?q=&constructionId=   — списка «все комплекты» нет: только поиск
GET    /api/document-sets/{id}      → DocumentSet (с instances[].generatedFiles[])
POST   /api/document-sets           { sectionId, name }   — раздел ТЕЛОМ запроса (issue #960)
PUT    /api/document-sets/{id}      { name }
DELETE /api/document-sets/{id}

POST   /api/document-sets/{setId}/documents          { documentTypeId }
PUT    /api/document-sets/{setId}/documents/{id}/requisites   body = JSON object
PUT    /api/document-sets/{setId}/documents/{id}/entity-refs  body = JSON object
PUT    /api/document-sets/{setId}/documents/{id}/plugin-data  body = JSON object

POST   /api/generate/{instanceId}   { format: "Pdf" }   (DOCX не поддерживается)
GET    /api/generate/download/{instanceId}/{format}
GET    /api/generate/debug-bundle/{instanceId}  → ZIP (template.typ + data.json + typeblocks.typ + userlib.typ) для отладки шаблона во внешнем Typst
GET    /api/generate/plugins
POST   /api/generate/plugins/{pluginId}/search  { entityType, query }
POST   /api/generate/plugins/{pluginId}/fetch   { entityType, externalId }

GET    /api/jobs/active             → активные фоновые задачи (сборка комплекта, распознавание)
                                      Ход долгих операций доставляется ПОЛЛИНГОМ, не сокетом.
```
