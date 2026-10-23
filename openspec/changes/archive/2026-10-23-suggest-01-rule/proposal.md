# Proposal: правило `Suggestable` и тэг `input.noSuggest`

Дочерний change зонтика `suggest-00-epic`, общая архитектура — `../suggest-00-epic/design.md` (D3, D4).

## Why

Issue #1529 (подсказки 1/5: правило `Suggestable` и тэг `input.noSuggest`), часть эпика #1527.

Подсказывать или нет — решение, которое нужно в трёх местах: адресу (`suggest-02` отвечает пустым
списком), полю на клиенте (не зовёт адрес зря) и редактору типа (показывает флажок или причину). Три
разных проверки разошлись бы на первом же новом тэге. Поэтому правило одно, истина — на сервере, на
клиенте — зеркало, а расхождение реализаций ловит общий файл случаев (приём `invoiceLineCases.json`).

Ручное выключение — тэгом, а не свойством `SchemaField`: новые свойства поля трижды терялись на круге
«разобрали → сохранили» (#1004, #1008, `moduleTitle`), тэги этот путь проходят давно.

## What Changes

- `Suggestable` в ядре (C#) и в клиенте (TS) с общим файлом случаев: оба набора тестов читают один JSON.
- Правило возвращает **код причины**: подсказывается | `type` | `computed` | `locked` | `identity` |
  `inn` | `docNumber` | `personnelNumber` | `article` | `orgName` | `account` | `manual` — точный список по реестру тэгов на момент PR
  (`nomenclature.article` и `org.name` — тэги однозначности, решение В-4 от 22.10.2026). Подсказывается только `string`;
  `text`, `primitive` и прочие виды — `type`. `work.producesId` отдельного кода не получает: поле с ним —
  `boolean`, его отсекает `type`.
- Новый функциональный тэг ядра `input.noSuggest` в реестре (`FunctionalTag`, `TagRegistry`,
  `GET /api/tags`) с описанием; только для `string`, на любое число полей. Поле с тэгом — `manual`.
- Умолчание вычисляется, не хранится: флажка «включить» нет.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `types-and-tags`: перечень действующих тэгов ядра пополняется `input.noSuggest`; новое требование —
  правило «поле подсказывается» с кодами причин.

## Impact

- **Сервер:** `Domain/Schema/FunctionalTag.cs`, `Application/Schema/TagRegistry.cs`, новое
  `Application/Objects/FieldSuggestions/Suggestable.cs`; тесты в `BHS.CRG.Tests/Schema/`.
- **Клиент:** `shared/api/tags.ts` (код тэга), новое `shared/api/suggestable.ts` с тестом; общий файл
  случаев `shared/api/suggestableCases.json`; `schemaRoundTrip.test.ts` (тэг в круге).
- **API:** `GET /api/tags` отдаёт новый тэг; новых адресов нет.
- **Версия:** не меняется; PR — с меткой `version:minor`.
