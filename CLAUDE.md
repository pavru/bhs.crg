# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

⚠️ **У этого файла есть копия — `AGENTS.md`** (её читают агенты, которые о `CLAUDE.md` не знают).
Источник здесь; правку переносите туда **тем же коммитом** — всё от «## Что это за проект» и до
конца. Сверяет `AgentRulesCopyTests`, но узнать об этом ДО отказа CI можно только отсюда:
предупреждение стояло лишь в самой копии, то есть попадалось на глаза кому угодно, кроме того, кто
правит источник.

## Что это за проект

Система генерации **исполнительной документации** для электромонтажных строительных проектов.

- `СтароеРешение/` — архивный прототип (VSTO Word Add-in + XSL 3.0). Используется как справочник по доменной логике и типам документов. **Не разрабатывается.**
- `src/` — новая система (в разработке, см. ниже).

---

## Новая система

### Стек

| Слой | Технология |
|---|---|
| Frontend | React 19 + TypeScript, Radix UI, Tailwind v4, React Query, Monaco (редактор Typst-шаблонов) |
| Backend | ASP.NET Core 10 (Minimal APIs), EF Core 10 (Npgsql), MediatR |
| Auth | ASP.NET Identity + JWT, роли Admin/User (без SSO / корп. интеграций) |
| БД | PostgreSQL 18 |
| Blob-хранилище | Garage (self-hosted, S3-совместимое) |
| PDF | **Typst** (CLI, env `TYPST_PATH`). DOCX **не поддерживается** |
| Распознавание/поиск | Ollama / Anthropic / Gemini (распознавание сканов), Serper / Yandex (веб-поиск) — для документов качества |
| Скриптовой движок | Jint (JavaScript — вычисляемые колонки DataSet) |
| Плагины | .NET AssemblyLoadContext + HTTP-плагины |

### Структура solution

```
src/
  server/
    BHS.CRG.slnx          — solution file (.NET 10 format)
    BHS.CRG.Api/          — ASP.NET Core Minimal API (точка входа)
    BHS.CRG.Application/  — MediatR команды/запросы, интерфейсы (IBlobStorage, IRepository)
    BHS.CRG.Domain/       — доменные сущности (чистый C#, без зависимостей)
    BHS.CRG.Infrastructure/ — EF Core, MinIO, Typst-генерация, распознавание/поиск, плагины
    BHS.CRG.Plugins/      — контракты плагинов (IDataSourcePlugin)
  client/
    package.json          — React SPA (Vite + Tailwind v4)
    src/
      features/
        catalog/          — управление каталогом сущностей + LoginPage
        templates/        — редактор Typst-шаблонов (Monaco) + библиотека Typst
        document-sets/    — комплекты документов + генерация
        settings/         — типы документов, SettingsPage
      shared/
        api/              — apiClient (axios + JWT), React Query hooks, types.ts
        hooks/            — useAuth
        ui/               — AuthProvider, ProtectedRoute, AppShell, Modal
```

### Команды разработки

```bash
# Инфраструктура (PostgreSQL + Garage) — ВСЁ в контейнерах, как в поставке (issue #894).
# ⚠️ База слушает 5433, а не 5432: 5432 может занимать нативная служба PostgreSQL, а строка
# подключения у них одинаковая — при совпадении портов приложение молча ушло бы в чужую базу.
# Порт 5433 указан везде: дев-compose, appsettings.Development.json, IntegrationTestFixture, ci.yml.
#
# ⚠️ ПЕРВЫЙ ЗАПУСК ПОСЛЕ ПЕРЕХОДА ДАЁТ ПУСТУЮ БАЗУ, и выглядит это как «данные пропали»:
# приложение мигрирует схему само и поднимается зелёным. Если ваши данные были в нативной службе
# (5432), перенесите их — она их не потеряла:
#   pg_dump -h localhost -p 5432 -U postgres -d bhs_crg -Fc -f bhs_crg.dump
#   psql   -h localhost -p 5433 -U postgres -c "DROP DATABASE IF EXISTS bhs_crg WITH (FORCE)"
#   psql   -h localhost -p 5433 -U postgres -c "CREATE DATABASE bhs_crg"
#   pg_restore -h localhost -p 5433 -U postgres -d bhs_crg --no-owner --no-privileges bhs_crg.dump
# ⚠️ Именно с `compose wait`: `up -d` возвращает 0, даже если инициализация хранилища УПАЛА,
# и тогда отказ приходит позже — ошибкой S3 из середины приложения. `up -d --wait` не годится:
# он считает одноразовый init неуспешным и краснеет даже при коде 0.
# С #880 база — PostgreSQL 18, и путь тома сменился. Если контейнер отказывается стартовать с
# упоминанием pg_upgrade, в томе лежит кластер 16: docker volume rm bhscrg_postgres_data
docker compose up -d && docker compose wait garage-init

# Backend (запуск с автомиграцией при старте)
dotnet run --project src/server/BHS.CRG.Api

# Frontend (dev-сервер на :5173, proxy /api → :5000)
cd src/client && npm run dev

# Создать EF-миграцию
dotnet ef migrations add <Name> --project src/server/BHS.CRG.Infrastructure \
                                --startup-project src/server/BHS.CRG.Api

# Ручное применение миграций (обычно не нужно — app мигрирует сам при старте)
dotnet ef database update --project src/server/BHS.CRG.Infrastructure \
                          --startup-project src/server/BHS.CRG.Api

# TypeScript проверка (ВАЖНО: -b, т.к. корневой tsconfig только ссылки;
# `tsc --noEmit` на нём ничего не проверяет и всегда «зелёный»)
cd src/client && npx tsc -b

# Backend сборка
cd src/server && dotnet build BHS.CRG.slnx

# Backend тесты (xUnit, проект BHS.CRG.Tests)
cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj

# Frontend тесты (vitest; *.test.ts рядом с кодом)
cd src/client && npm test

# Линт с храповиком (issue #854): падает, если ошибок по какому-то правилу стало БОЛЬШЕ
cd src/client && npm run lint:ratchet
cd src/client && npm run lint:ratchet:update   # переписать базовый уровень (осознанно!)

# Логика скрипта обновления (без Docker и сети; сеть нужна одной проверке — она пропускается)
bash deploy/update.tests.sh
```

> Тесты покрывают чистую логику: исполнители фильтра/вычисляемых колонок наборов
> данных, CSV-парсер, авто-маппер, доменные инварианты, метатеги (backend);
> наследование схем (`resolveEffectiveFields`), группировку полей, дерево фильтров
> и хелперы наборов данных (frontend).

### HTTPS на стенде

Стенд отвечает по HTTPS на одном адресе — **`https://localhost/`**: перед локально запущенными API
(`:5000`) и дев-сервером клиента (`:5173`) стоит nginx из того же `docker compose` (issue #940). Так
на стенде появляется контур заказчика: защищённый контекст браузера — без него камера, геолокация,
service worker и установка как приложение недоступны **вовсе**, то есть мобильный клиент на
`http://…:5173` не проверить никак, — и заголовки `X-Forwarded-*`, которых иначе на стенде не бывает.

⚠️ **API и клиент обязаны слушать не только петлю.** Контейнер приходит к ним снаружи, с адреса
шлюза, и к `localhost:5000` подключиться не может в принципе. Поэтому профиль запуска API поднимает
Kestrel на `http://+:5000`, а `npm run dev` зовёт vite с ключом `--host`. Запустили иначе — ответом
будет 502, и выглядеть это будет как поломка приложения.

📄 Подробности —
[docs/DEV_NOTES.md](docs/DEV_NOTES.md#стенд-по-https-сертификат-и-доступ-с-телефона): выпуск
самоподписанного УЦ, установка его в доверенные, открытие стенда с телефона.

### CI

`.github/workflows/ci.yml` гоняет всё это на каждый PR и на каждый push в master: backend
(сборка + тесты, PostgreSQL сервисным контейнером), frontend (`tsc -b`, `npm run build`, vitest,
храповик линта), логика `deploy/update.sh` и **живые прогоны в браузере** — четырьмя независимыми
работами. Node в CI — той же версии, что в `deploy/Dockerfile.web`.

**Проверки ЗАПРЕЩАЮТ слияние — с 25 августа 2026.** В репозитории включён набор правил «Master
ruleset»: master меняется только через PR, а слить его нельзя, пока не прошли три обязательные
проверки — **Backend (сборка + тесты)**, **Frontend (типы + тесты + линт)** и **Скрипт обновления
(логика)**. Одобрений не требуется (`required_approving_review_count: 0`), ветка обязана быть
актуальной относительно master (`strict`). Живые прогоны и claude-review обязательными не сделаны:
они сообщают.

⚠️ **Имена работ в `ci.yml` — договор с этим правилом**: обязательные проверки перечислены там ПО
ИМЕНАМ. Переименование работы означает, что обязательная проверка не появится никогда, и слияние
блокируется навсегда — с сообщением «the base branch policy prohibits the merge», которое на имя не
указывает ничем (наступали, #890). Менять имя можно только вместе с правилом, и сначала правило.

Линт **не требует** чинить накопленные ошибки (на момент включения — 112). Требование одно: не
добавлять новых. Сравнение идёт по правилам, а не по общему числу — иначе «починил одну, добавил
другую» прошло бы молча. Базовый уровень — `src/client/eslint-baseline.json`; стало лучше — локально
он опускается сам, в CI шаг падает с просьбой опустить и закоммитить (иначе храповик прокручивается
назад: починили пять, вернули пять, проверка молчит).

**Файлы не растут — храповик размера** (issue #1041). Файл, перешагнувший **500 строк кода**
(пустые и комментарии не в счёт: правило не должно спорить с привычкой объяснять решения рядом с
кодом), обязан стоять в `src/file-size-baseline.json` — и расти ему нельзя; новый файл выше порога
не заводится вовсе. Накопленное чинить не требуется, как и с линтом: требуется не добавлять.
Похудел — уровень опускается тем же PR, иначе храповик прокручивается назад. Выход осознанный:
поднять число руками и сказать в описании PR, почему файл обязан быть таким — так живут
`ComplexFields.tsx` (рекурсия схемы: дерево, разрывать нечем) и `BackupService.Restore.cs`.
Заведён после того, как за один день пришлось разрезать восемь складов: файл-склад читают ЦЕЛИКОМ
ради одной правки — и человек, и ревью, и агент.

```bash
# Локально (в CI едет внутри обязательной проверки «Backend»)
cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet
```

📄 Подробности — [docs/DEV_NOTES.md](docs/DEV_NOTES.md#ci-состав-прогона-и-связь-с-выпуском): из
чего состоят живые прогоны, почему падение любой из четырёх работ останавливает выпуск, «Re-run
failed jobs».

### Версия приложения

Единственный источник — `src/server/Directory.Build.props`, `<Version>`. Клиент своей версии не
имеет (`package.json` = `0.0.0`): UI берёт её из `/api/version`, git-хеш SDK подставляет сам.

**Версию поднимаем в том же PR, что и изменение.** MINOR — набор функциональности, PATCH — фиксы.

Правило записано здесь, потому что до этого оно нигде не было записано и держалось на памяти: с
7 июля версия менялась 221 раз (практически каждым PR), а 22 июля обрыв — и следующие **104 PR**
прошли на одной и той же `0.53.15`. Ничего при этом не сломалось и никто не предупредил, поэтому
единственная защита — чтобы правило попадалось на глаза (issue #550).

### Документация и развёртывание

- `docs/` — инструкции (Markdown + PDF): `DEPLOYMENT.md`, `USER_GUIDE.md`, `ADMIN_GUIDE.md`
  (индекс — `docs/README.md`). Сборка PDF: `docs/tools/` (`npm run pdf`).
- [`docs/DEV_NOTES.md`](docs/DEV_NOTES.md) — **«почему так сделано»**: обоснования решений,
  история переходов, статус первой версии и полный листинг REST API. Сюда вынесено то, что нужно
  редко, чтобы `CLAUDE.md` не читался целиком каждой сессией (issue #1016).
- `deploy/` — Docker Compose на весь стек (postgres, garage, ollama, api, web) + Dockerfile'ы
  и `.env.example`. api и web поставляются образами из GHCR (`APP_VERSION` в `.env`), выпуск —
  ручной запуск workflow `Release`, который берёт номер из `Directory.Build.props`. Запуск:
  `cp deploy/.env.example deploy/.env` → `docker compose -f deploy/docker-compose.yml up -d`;
  сборка из исходников — с оверлеем `-f deploy/docker-compose.build.yml`.
  Образ `api` включает **Typst CLI**.

**Установка — `deploy/install.sh`** (issue #890), обновление — `deploy/update.sh`. Оба едут
ассетами выпуска. Install ставит систему с нуля: предполёт, файлы выпуска, `.env` со случайными
паролями и ключами нужного формата, каталог копий, запуск, ожидание готовности и **создание
первого администратора** — последнее закрывает окно, в котором страница регистрации открыта любому.
Отказывается работать в каталоге, где уже есть `.env`. Логика покрыта `install.tests.sh` (без
Docker и сети), как у `update.sh`.

**Хранилище — Garage** (с 0.160.0, issue #885). Что важно помнить: хранилище слушает **3900**, ключи — вида `GK`+24 hex, а перед работой Garage
надо ИНИЦИАЛИЗИРОВАТЬ — без применённой раскладки он отвечает отказом на любой запрос. Делает это
идемпотентный `garage-init` (`deploy/garage-init.sh`); свой образ у него потому, что в образе
Garage нет shell. **Версия Garage записана в трёх местах** — поставка, дев-стенд и `FROM` в
`Dockerfile.garage-init` (клиент и сервер обязаны совпадать); сверяет их сторож в CI.

📄 Подробности —
[docs/DEV_NOTES.md](docs/DEV_NOTES.md#поставка-прибитые-версии-переход-на-garage-dependabot):
почему версии образов прибиты точно, зачем в GHCR лежит копия MinIO, как устроен переход на Garage
и что не покрывает dependabot.

### REST API

Полный листинг маршрутов — в [docs/DEV_NOTES.md](docs/DEV_NOTES.md#rest-api-основные-маршруты);
источник истины — `src/server/BHS.CRG.Api/Endpoints/`.

Здесь — только то, на чём легко ошибиться, каждый раз заново:

- `schema`, `data` и `content` в телах запросов — **строка с JSON**, а не объект. Отправленный
  объект даёт отказ, по которому это не очевидно.
- **DOCX не поддерживается**: единственный формат генерации — `Pdf`.
- Запись типов, полей, шаблонов и настроек — **только роль Admin**.
- Ход долгих операций доставляется **поллингом** (`GET /api/jobs/active`), не сокетом.

### Архитектура

#### Два режима работы

1. **Настройка** (роль Admin): типы документов (схема полей), Typst-шаблоны, привязки наборов данных/плагинов, пользователи, настройки.
2. **Генерация** (роль User): создаёт `DocumentSet` (комплект), заполняет реквизиты, связывает с сущностями каталога, подключает наборы данных и документы качества → получает PDF.

Роли разграничены и в UI (раздел «Настройка системы» — только Admin), и в API
(запись конфигурации защищена политикой `Admin`). См. память `project-roles-users`.

#### Инвариант: `CatalogScope` — не граница безопасности

`CatalogScope` (`Set` / `Section` / `Construction` / `System`) организует данные и задаёт приоритет
их разрешения. **Правами он не управляет.** Привязки данных к пользователю в системе нет вовсе:
любой вошедший видит и правит объекты всех уровней и всех строек. Это решение, а не упущение —
пользователи суть сотрудники одной компании с равным допуском (issue #675, 2026-08-05).

Записано потому, что уровни выглядят как области видимости, и однажды на них сошлются как на
разграничение доступа. Не выдавайте проверку уровня за проверку прав.

**Условие пересмотра:** учётная запись выдана кому-то вне компании (заказчик, технадзор,
субподрядчик) — тогда изоляция данных обязательна и делается прежде остального. Затронет
наследование по поддереву, `_baseRef` из родительской области, провайдеры системных наборов,
библиотеку документов качества и инструменты MCP (они действуют правами пользователя).

#### Пайплайн генерации документа

```
DocumentInstance (реквизиты JSON + ссылки на сущности)
    │
    ▼ EntityResolver (C#-аналог ref/merge из старой XSL-системы)
    │   подмешивает данные Organization/Person/etc. из EntityCatalog
    ▼
    ▼ DataSetResolver / QualityLinkResolver
    │   подмешивают наборы данных и документы качества (по функциональным тэгам)
    ▼
GenerationContext (единый JSON-контекст)
    │
    ▼ TypstGenerator: контекст → data.json; шаблон + typeblocks.typ + userlib.typ
    │   компилируются Typst CLI (env TYPST_PATH)
    ▼
PDF
```

#### Ключевой паттерн шаблона

Шаблон хранится как **Typst-документ** (поле `Template.Content`). При генерации во
временной папке создаются файлы:

- `data.json` — контекст генерации (реквизиты + подмешанные данные);
- `typeblocks.typ` — авто-сгенерированные Typst-функции отображения составных типов;
- `userlib.typ` — общая библиотека Typst (Typst User Lib, редактируется админом);
- картинки из data-URI материализуются в файлы (`TypstImageMaterializer`).

Шаблон обращается к данным через JSON и переиспользуемые функции. Отладка — через
`GET /api/generate/debug-bundle/{instanceId}` (ZIP со всеми этими файлами) во внешнем Typst.

#### Модель данных (PostgreSQL)

```
EntityCatalog: Organization, Person, ConstructionObject, Project  — JSONB data
DocumentType: id, name, schema JSONB, pluginBindings JSONB
Template: id, documentTypeId, content TEXT (Typst), version
DocumentSet: id, projectId, name
DocumentInstance: id, documentSetId, documentTypeId,
                  requisites JSONB, entityRefs JSONB, pluginData JSONB
GeneratedFile: id, documentInstanceId, format, blobPath, generatedAt
```

#### Плагины

```csharp
interface IDataSourcePlugin
{
    string Id { get; }
    EntitySchema[] ProvidedSchemas { get; }
    Task<SearchResult> SearchAsync(string entityType, string query, CancellationToken ct);
    Task<JsonDocument> FetchAsync(string entityType, string externalId, CancellationToken ct);
}
```

.NET-плагины загружаются через `AssemblyLoadContext`. HTTP-плагины работают через стандартный REST-контракт (те же методы, но по HTTP).

#### Типы документов (из старой системы, требуют шаблонов)

АОСР, ЖурналПрокладкиКабеля, КабельныйЖурнал, ВедомостьМатериалов, ПротоколИзмеренияИзоляции, ПротоколИзмеренияЗаземления, ПротоколИзмеренияМеталосвязи, ПротоколИзмеренияФазаНоль, РеестрДокументов, РеестрРабот, ВедомостьСхем, ТитульныйЛист, ПНР-документы (5 форм).

---

## Старое решение (справочник)

`СтароеРешение/Xml/CommonDataTypes.xsd` — доменная модель (типы сущностей, структура документов).
`СтароеРешение/Xml/NewElementResolverStyles.xsl` — логика ref/merge, которую нужно воспроизвести в `EntityResolver` на C#.
`СтароеРешение/Xml/*TemplateData.xml` — примеры данных для каждого типа документа.
