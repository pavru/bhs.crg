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
| Frontend | React 19 + TypeScript, TipTap v3, Radix UI, Tailwind v4, React Query |
| Backend | ASP.NET Core 10 (Minimal APIs), EF Core 10 (Npgsql), MediatR, SignalR |
| Auth | ASP.NET Identity + JWT (без SSO / корп. интеграций) |
| БД | PostgreSQL 16 |
| Blob-хранилище | MinIO (self-hosted) |
| PDF | Playwright (.NET headless Chromium) |
| DOCX | LibreOffice headless |
| Шаблонизатор | Scriban |
| Плагины | .NET AssemblyLoadContext + HTTP-плагины |

### Структура solution

```
src/
  server/
    BHS.CRG.slnx          — solution file (.NET 10 format)
    BHS.CRG.Api/          — ASP.NET Core Minimal API (точка входа)
    BHS.CRG.Application/  — MediatR команды/запросы, интерфейсы (IBlobStorage, IRepository)
    BHS.CRG.Domain/       — доменные сущности (чистый C#, без зависимостей)
    BHS.CRG.Infrastructure/ — EF Core, MinIO, Playwright, LibreOffice, плагины
    BHS.CRG.Plugins/      — контракты плагинов (IDataSourcePlugin)
  client/
    package.json          — React SPA (Vite + Tailwind v4)
    src/
      features/
        catalog/          — управление каталогом сущностей + LoginPage
        templates/        — редактор шаблонов (TipTap)
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

### Статус первой версии

Первая версия полностью реализована (backend + frontend + EF-миграция):

| Модуль | Статус |
|---|---|
| Auth (регистрация/вход, JWT) | ✅ |
| Каталог сущностей (CRUD) | ✅ |
| Типы документов (CRUD + схема) | ✅ |
| Шаблоны (TipTap editor + версионирование) | ✅ |
| Комплекты документов (CRUD + состав) | ✅ |
| Реквизиты и связи документа | ✅ |
| Генерация PDF / DOCX | ✅ |
| EF Core migration (InitialCreate) | ✅ |

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
POST   /api/templates               { documentTypeId, name, htmlContent }
PUT    /api/templates/{id}          { htmlContent }  — создаёт новую версию

GET    /api/document-sets
GET    /api/document-sets/{id}      → DocumentSet (с instances[].generatedFiles[])
POST   /api/document-sets           { name, projectEntityId? }
PUT    /api/document-sets/{id}/name { name }
DELETE /api/document-sets/{id}

POST   /api/document-sets/{setId}/documents          { documentTypeId }
PUT    /api/document-sets/{setId}/documents/{id}/requisites   body = JSON object
PUT    /api/document-sets/{setId}/documents/{id}/entity-refs  body = JSON object
PUT    /api/document-sets/{setId}/documents/{id}/plugin-data  body = JSON object

POST   /api/generate/{instanceId}   { format: "Pdf"|"Docx" }
GET    /api/generate/download/{instanceId}/{format}
GET    /api/generate/plugins
POST   /api/generate/plugins/{pluginId}/search  { entityType, query }
POST   /api/generate/plugins/{pluginId}/fetch   { entityType, externalId }

WS     /hubs/generation             (SignalR, auth via ?access_token=)
```

### Архитектура

#### Два режима работы

1. **Настройка** (роль Admin): типы документов (JSON Schema полей), HTML-шаблоны (TipTap), привязки плагинов.
2. **Генерация** (роль User): создаёт `DocumentSet` (комплект), заполняет реквизиты, связывает с сущностями каталога, импортирует из плагинов → получает PDF или DOCX.

#### Пайплайн генерации документа

```
DocumentInstance (реквизиты JSON + ссылки на сущности)
    │
    ▼ EntityResolver (C#-аналог ref/merge из старой XSL-системы)
    │   подмешивает данные Organization/Person/etc. из EntityCatalog
    ▼
GenerationContext (единый плоский/вложенный JSON)
    │
    ▼ ScribanRenderer → HTML (с заполненными полями и раскрытыми repeat-блоками)
    │
    ├──► Playwright → PDF
    └──► LibreOffice headless → DOCX
```

#### Ключевой паттерн шаблона

Шаблон хранится как HTML с data-атрибутами:

```html
<!-- Скалярное поле -->
<span data-field="Подрядчик.Наименование.Краткое">{{ Подрядчик.Наименование.Краткое }}</span>

<!-- Повторяющийся блок (таблица материалов, строки кабельного журнала) -->
<tr data-repeat="Материалы">
  <td data-field="ПорядковыйНомер">{{ ПорядковыйНомер }}</td>
  <td data-field="Наименование">{{ Наименование }}</td>
  <td data-field="Количество">{{ Количество }}</td>
</tr>
```

Разрыв страниц и поля печати задаются через CSS `@page`.

#### Модель данных (PostgreSQL)

```
EntityCatalog: Organization, Person, ConstructionObject, Project  — JSONB data
DocumentType: id, name, schema JSONB, pluginBindings JSONB
Template: id, documentTypeId, htmlContent TEXT, version
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
