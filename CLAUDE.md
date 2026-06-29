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
| Backend | ASP.NET Core 10 (Minimal APIs), EF Core 10 (Npgsql), MediatR, SignalR |
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
```

> Тесты покрывают чистую логику: исполнители фильтра/вычисляемых колонок наборов
> данных, CSV-парсер, авто-маппер, доменные инварианты, метатеги (backend);
> наследование схем (`resolveEffectiveFields`), группировку полей, дерево фильтров
> и хелперы наборов данных (frontend).

### Документация и развёртывание

- `docs/` — инструкции (Markdown + PDF): `DEPLOYMENT.md`, `USER_GUIDE.md`, `ADMIN_GUIDE.md`
  (индекс — `docs/README.md`). Сборка PDF: `docs/tools/` (`npm run pdf`).
- `deploy/` — Docker Compose на весь стек (postgres, minio, ollama, api, web) + Dockerfile'ы
  и `.env.example`. Запуск: `cp deploy/.env.example deploy/.env` → `docker compose -f deploy/docker-compose.yml up -d --build`.
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

WS     /hubs/generation             (SignalR, auth via ?access_token=)
```

### Архитектура

#### Два режима работы

1. **Настройка** (роль Admin): типы документов (схема полей), Typst-шаблоны, привязки наборов данных/плагинов, пользователи, настройки.
2. **Генерация** (роль User): создаёт `DocumentSet` (комплект), заполняет реквизиты, связывает с сущностями каталога, подключает наборы данных и документы качества → получает PDF.

Роли разграничены и в UI (раздел «Настройка системы» — только Admin), и в API
(запись конфигурации защищена политикой `Admin`). См. память `project-roles-users`.

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
