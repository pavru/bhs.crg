# Tasks: график — работы, построение, зависимости

## 0. Условие

- [x] 0.1 Сверены `PLAN-Q22`, раздел решений ТЗ и В-14 зонтика: шапка графика — запись модуля.
  `proposal.md`, `design.md` и дельта описывают принятое хранение.

## 1. PR 1: работы и построение

- [x] 1.1 `Schedules/Schedule.cs`, `ScheduleWork.cs`, исполнители; миграция `Schedules` (`--context
  PlanDbContext`); ссылки `CORE-34.1` на позицию, стройку, раздел, сотрудника [PLAN-Q7] [PLAN-16].
  Проверка — `ModuleReferenceInventoryTests` зелёный; новая ссылка на архивного сотрудника — отказ
- [x] 1.2 `ScheduleBuilder.cs`: работы по частям распределения и остатку, позиции портом `plan-04`,
  `skipped.unmapped`, фиксация редакции, ключ построения [PLAN-15] [PLAN-5] [CORE-12]. Проверка — тесты:
  700 + 500 + 200 — три работы; 17 несопоставленных названы; повтор — тот же график; правка
  зафиксированной редакции — отказ с названием графика
- [x] 1.3 Пометка `distribution-changed`, предпросмотр и перестроение с `If-Match` и отпечатком; сроки
  совпавших частей сохраняются [PLAN-15]. Проверка — тест: перенос 100 м даёт пометку; устаревший
  отпечаток — 409
- [x] 1.4 Адреса графика и работ: построение, чтение, ручная работа, правка работы с `If-Match` [PLAN-15]
  [PLAN-16] [AUTH-8]. Проверка — тесты отказов: без заголовка 400, устаревшая 409, только чтение — 403,
  модуль выключен — отказ; `RecordWriteGuardCoverageTests` и `EndpointGateInventoryTests` зелёные

## 2. PR 2: зависимости и предупреждения

- [ ] 2.1 `ScheduleLink.cs`, миграция `ScheduleLinks`; адреса связей; вид не `fs` — 422; другая стройка —
  422 [PLAN-16.1]. Проверка — тесты: задержка −2, вид «начало → начало» — отказ, связь на другую стройку — отказ
- [ ] 2.2 `CycleCheck.cs` по графу связей стройки в одной транзакции с вставкой [PLAN-16.1]. Проверка —
  тесты: цикл через два графика — отказ с перечнем; две параллельные вставки, замыкающие цикл, — одна отвергнута
- [ ] 2.3 `ScheduleWarnings.cs`: `starts-before-predecessor` у обеих работ, `ends-on-day-off`,
  `calendar-missing`, `no-dates`; коды — в каталоге пометок (D17) [PLAN-19] [PLAN-19.1]. Проверка — тесты на
  каждый код по календарю `plan-18`; сроки после чтения не меняются
- [ ] 2.4 `plan-16`: блок графиков в производных редакции (покрытие, «устарело») [PLAN-22]. Проверка —
  тест `RevisionDerivatives` с графиком
- [ ] 2.5 `docs/DEV_NOTES.md`: почему работы — записи модуля, почему проверка цикла по стройке и в
  транзакции. Проверка — ревью PR

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Schedule|FullyQualifiedName~CycleCheck|FullyQualifiedName~RevisionDerivatives|FullyQualifiedName~RecordWriteGuard|FullyQualifiedName~ModuleReferenceInventory"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
