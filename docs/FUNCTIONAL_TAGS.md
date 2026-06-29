# Функциональные тэги BHS.CRG

> **Версия:** 2026-06-29  
> Обновлять при добавлении/изменении тэгов.

---

## Назначение

Функциональные тэги — **единственный санкционированный способ связать hard-coded функционал с пользовательской схемой** (типами документов, полями).

Без тэгов: hard-code находит поле по имени `"Артикул"` → имя меняется → всё ломается.  
С тэгами: hard-code спрашивает `«поле с тэгом material.identity»` → связь не зависит от названия.

---

## Архитектура

```
FunctionalTag.cs          ← коды-константы (string)
    │
TagRegistry.cs            ← метаданные каждого тэга (scope, label, appliesTo, multiple)
    │
    ├── SchemaTags.cs     ← аксессоры для backend: поиск тэгированных полей/типов
    │
    └── GET /api/tags     ← отдаёт реестр во frontend
            │
        tags.ts           ← FUNCTIONAL_TAG (зеркало констант) + fieldTags() / typeTags()
```

### Файлы

| Файл | Назначение |
|---|---|
| `src/server/BHS.CRG.Domain/Schema/FunctionalTag.cs` | Строковые коды-константы |
| `src/server/BHS.CRG.Application/Schema/TagRegistry.cs` | Реестр: `TagDefinition`, `TagRegistry.All` |
| `src/server/BHS.CRG.Application/Schema/SchemaTags.cs` | Backend-аксессоры |
| `src/server/BHS.CRG.Api/Endpoints/Schema/TagsEndpoints.cs` | `GET /api/tags` |
| `src/client/src/shared/api/tags.ts` | Frontend: `FUNCTIONAL_TAG`, `useTagRegistry`, хелперы |

---

## Хранение в схеме

```jsonc
// Поле — fields[].tags
{
  "fields": [
    { "key": "Артикул", "type": "string", "tags": ["material.identity"] },
    { "key": "НомерДокумента", "type": "string", "tags": ["doc.number"] }
  ]
}

// Тип документа — корень схемы
{
  "tags": ["type.qualityDocument"],
  "fields": [...]
}
```

Поле `tags: string[]` разрешено как у поля, так и у типа. Тэги наследуются: если тип-родитель несёт `type.qualityDocument`, потомок тоже считается документом качества.

---

## Каталог тэгов

### Тэги поля (`scope = Field`)

| Код | Константа (C#) | Константа (TS) | Применимые типы поля | Multiple | Описание |
|---|---|---|---|---|---|
| `doc.pageCount` | `DocPageCount` | `docPageCount` | `number`, `string`, `text` | Нет | Кол-во страниц PDF — автозаполняется после генерации или загрузки печатной формы |
| `doc.generatedAt` | `DocGeneratedAt` | — | `date`, `string`, `text` | Нет | Дата генерации документа — автозаполняется |
| `doc.generatedBy` | `DocGeneratedBy` | — | `string`, `text` | Нет | Имя пользователя, запустившего генерацию — автозаполняется |
| `doc.printForm` | `DocPrintForm` | `docPrintForm` | `file` | Нет | Поле-файл с загруженной печатной формой; при загрузке запускается извлечение метаданных (кол-во страниц и т.д.) |
| `doc.number` | `DocNumber` | `docNumber` | `string`, `text` | Нет | Номер документа — используется в списках (в т.ч. в библиотеке документов качества) |
| `material.identity` | `MaterialIdentity` | `materialIdentity` | `string`, `text` | **Да** | Поле идентичности материала (артикул, наименование и т.п.) для сопоставления с документом качества. Можно навесить на несколько полей — порядок задаёт приоритет |
| `material.qualityDocLink` | `MaterialQualityDocLink` | `materialQualityDocLink` | `complex` | Нет | Целевое поле, в которое `QualityLinkResolver` подмешивает данные привязанного документа качества |
| `quality.validUntil` | `QualityValidUntil` | `qualityValidUntil` | `date` | Нет | Срок действия документа качества. Просроченные документы исключаются при автоподборе к материалу |
| `quality.manufacturer` | `QualityManufacturer` | `qualityManufacturer` | `string`, `text` | Нет | Производитель — для группировки библиотеки и оценки релевантности при подборе |

### Тэги типа (`scope = Type`)

| Код | Константа (C#) | Константа (TS) | Применимые виды типа | Multiple | Описание |
|---|---|---|---|---|---|
| `type.qualityDocument` | `TypeQualityDocument` | `typeQualityDocument` | `Document` | Нет | Тип документа является «документом качества» — участвует в библиотеке и автораспознавании. Наследуется потомками |

---

## Потребители (backend)

| Тэг(и) | Где используется |
|---|---|
| `doc.pageCount`, `doc.generatedAt`, `doc.generatedBy` | `GenerateDocumentHandler` — патчит реквизиты после генерации через `SchemaTags.PatchMetadata` |
| `doc.printForm` | `PrintFormEndpoints` — при загрузке файла извлекает метаданные и патчит реквизиты; детект поля через `SchemaTags.FieldKeysWithTag` |
| `doc.pageCount` | `MetadataExtractor` — пишет кол-во страниц после рендера PDF |
| `material.identity` | `QualityLinkResolver` — находит поля идентичности в составных типах для сопоставления материал↔документ качества |
| `material.qualityDocLink` | `QualityLinkResolver` — находит целевое поле и подмешивает данные документа |
| `type.qualityDocument` | `QualityLinkResolver`, `QualityEndpoints` — фильтрует типы документов качества без хардкода имён |

---

## Потребители (frontend)

| Тэг(и) | Где используется |
|---|---|
| `type.qualityDocument` | `QualityDocsPage`, `QualityDocForm` — определяет тип «документ качества» вместо хардкода GUID |
| `material.identity` | `QualityLinksTab` — находит поля идентичности в составных типах для отображения/сопоставления |
| `material.qualityDocLink` | `QualityLinksTab` — находит целевое поле ссылки в составных типах |
| `quality.validUntil` | `QualityLinksTab` — фильтрует просроченные документы при подборе |
| `quality.manufacturer` | `QualityLinksTab` — группировка библиотеки, оценка релевантности |
| `doc.printForm` | `editor` (InstanceEditor) — детектирует поле печатной формы для особой обработки загрузки |
| `doc.number` | отображение номера документа в списках |

### Frontend-хелперы (`src/client/src/shared/api/tags.ts`)

```ts
import { FUNCTIONAL_TAG, useTagRegistry, fieldTags, typeTags } from 'shared/api/tags';

// Получить реестр (кешируется 5 мин)
const { data: registry } = useTagRegistry();

// Тэги, применимые к полю типа "string"
const available = fieldTags(registry, 'string');

// Проверить тэг типа
import { typeHasTag } from 'shared/api/schema';
const isQuality = typeHasTag(docType, FUNCTIONAL_TAG.typeQualityDocument);
```

### Хелперы схемы (frontend)

```ts
// src/client/src/shared/api/schema.ts
findTaggedFieldPath(schema, FUNCTIONAL_TAG.materialIdentity)  // → путь к полю (в т.ч. в составных)
compositeFieldHasTag(field, FUNCTIONAL_TAG.materialIdentity)  // → boolean
typeHasTag(docType, FUNCTIONAL_TAG.typeQualityDocument)        // → boolean (с наследованием)
```

### Backend-аксессоры (`SchemaTags.cs`)

```csharp
// Все тэгированные поля типа (с наследованием по цепочке)
var tagged = SchemaTags.TaggedFields(docType, allDocTypes);

// Ключи полей с конкретным тэгом (только своя схема)
var keys = SchemaTags.FieldKeysWithTag(docType.Schema, FunctionalTag.MaterialIdentity);

// Тип или его предок несёт тэг типа?
bool isQuality = SchemaTags.TypeHasTag(docType, allDocTypes, FunctionalTag.TypeQualityDocument);

// Накложить метаданные генерации на реквизиты
var patched = SchemaTags.PatchMetadata(current, taggedFields, new Dictionary<string, object?> {
    [FunctionalTag.DocGeneratedAt] = DateTime.Today.ToString("yyyy-MM-dd"),
    [FunctionalTag.DocGeneratedBy] = userName,
});
```

---

## Как добавить новый тэг

1. **`FunctionalTag.cs`** — добавить строковую константу:
   ```csharp
   /// <summary>Описание.</summary>
   public const string MyNewTag = "group.tagName";
   ```

2. **`TagRegistry.cs`** — добавить запись в `All`:
   ```csharp
   new(FunctionalTag.MyNewTag, "Метка для UI",
       "Подробное описание — показывается в подсказке.",
       TagScope.Field, ["string", "text"], Multiple: false),
   ```

3. **`tags.ts`** (frontend) — добавить в `FUNCTIONAL_TAG`:
   ```ts
   myNewTag: 'group.tagName',
   ```

4. **Потребитель** — использовать `SchemaTags.FieldKeysWithTag` / `TaggedFields` / `TypeHasTag` (backend) или `findTaggedFieldPath` / `typeHasTag` (frontend). **Не обращаться к полям/типам по имени.**

5. **`PrimitiveType.AllowedTags`** (опционально) — если тэг должен быть доступен только для определённых примитивных типов полей, добавить его в `AllowedTags` нужного `PrimitiveType` через API или миграцию.

6. Обновить этот документ.

---

## Конвенция именования

```
<группа>.<сущность>
```

| Группа | Область |
|---|---|
| `doc.*` | Метаданные документа (генерация, печатная форма) |
| `material.*` | Поля в записях материалов |
| `quality.*` | Поля в документах качества |
| `type.*` | Тэги уровня типа документа |

Новые группы вводить по аналогии — сначала проверить, нет ли подходящей существующей.

---

## Известные особенности

- **Опечатка в старых шаблонах:** в ранних шаблонах встречается «ДокументПодтверждающийКачетво» (опечатка). После перехода на тэги правильное написание — «ДокументПодтверждающийКачество». При редактировании шаблонов использовать корректное написание.

- **Multiple = true для `material.identity`:** одному составному типу можно назначить несколько полей идентичности — порядок в массиве `tags` задаёт приоритет при матчинге (первый матч → успех).

- **Наследование тэгов типа:** `SchemaTags.TypeHasTag` идёт по цепочке `ParentId` вверх. Потомок типа `type.qualityDocument` автоматически считается документом качества без явного тэга.
