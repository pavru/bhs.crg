# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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
| БД | PostgreSQL 16 |
| Blob-хранилище | MinIO (self-hosted) |
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
# Инфраструктура (PostgreSQL + MinIO)
docker compose up -d

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

### CI

`.github/workflows/ci.yml` гоняет всё это на каждый PR и на каждый push в master: backend
(сборка + тесты, PostgreSQL сервисным контейнером), frontend (`tsc -b`, `npm run build`, vitest,
храповик линта) и логика `deploy/update.sh` — тремя независимыми работами. Node в CI — той же
версии, что в `deploy/Dockerfile.web`. Обязательными в настройках репозитория сделаны первые две:
третью владелец добавляет по желанию.

**Проверки сообщают, а не запрещают.** Пока в настройках репозитория не включены required status
checks на master, красный прогон не мешает ни слить PR, ни запушить в master. Включение — действие
владельца репозитория. Прикрыт отдельно только выпуск: `release.yml` первым делом спрашивает у API
итог прогона CI на своём коммите и отказывается публиковать образы, если тот не `success`.

Линт **не требует** чинить накопленные ошибки (на момент включения — 112). Требование одно: не
добавлять новых. Сравнение идёт по правилам, а не по общему числу — иначе «починил одну, добавил
другую» прошло бы молча. Базовый уровень — `src/client/eslint-baseline.json`; стало лучше — локально
он опускается сам, в CI шаг падает с просьбой опустить и закоммитить (иначе храповик прокручивается
назад: починили пять, вернули пять, проверка молчит).

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
- `deploy/` — Docker Compose на весь стек (postgres, minio, ollama, api, web) + Dockerfile'ы
  и `.env.example`. api и web поставляются образами из GHCR (`APP_VERSION` в `.env`), выпуск —
  ручной запуск workflow `Release`, который берёт номер из `Directory.Build.props`. Запуск:
  `cp deploy/.env.example deploy/.env` → `docker compose -f deploy/docker-compose.yml up -d`;
  сборка из исходников — с оверлеем `-f deploy/docker-compose.build.yml`.
  Образ `api` включает **Typst CLI**.

### Статус первой версии

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

### REST API

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

GET    /api/document-sets
GET    /api/document-sets/{id}      → DocumentSet (с instances[].generatedFiles[])
POST   /api/document-sets           { name, projectEntityId? }
PUT    /api/document-sets/{id}/name { name }
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
