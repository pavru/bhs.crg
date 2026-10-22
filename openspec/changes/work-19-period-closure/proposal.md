# Proposal: закрытие периода в учёте работ

Дочерний change зонтика `work-00-epic`, общая архитектура — `../work-00-epic/design.md` (D14, D11, D8).

> **Базовый тип версии записи** (`CORE-36`) строит этап 3 — `plan-12-record-versions`, первым
> потребителем идут версии КП (решение нарезки этапа 3 от 22.10.2026, D18 зонтика). Здесь версии отчёта
> строятся на готовом типе.
> Core-дельта базового типа снята: её требования (`CORE-36`) заводит `plan-12`.

## Why

После закрытия периода отчёты не меняются, исправление — новой версией с причиной (`WORK-15`); досылка
к принятому возвращает его в приёмку (`WORK-16`). Служба закрытия ядра построена этапом 2
(`CORE-35`), но раздел модуля умеет только перечислять (`ModuleClosingSection` — `Unfinished` и
`Frozen`, `BHS.CRG.Modules/Ports/IModuleClosingReport.cs:48-55`). Решение владельца — отчёты в
открытом окне подачи **запрещают** закрытие (Р-6, `WORK-52`): граница считается по поясу компании, дата
смены — по поясу стройки, и стройка западнее компании иначе закрылась бы раньше, чем истекло её окно;
автоподача упёрлась бы в закрытый период, и черновик остался бы черновиком навсегда. Версии записей —
один приём ядра (`CORE-36`): базовый тип `ModuleRecordVersion` и перепись приносит `plan-12` (до него
тип был только у счёта, `Modules.Costs/Data/InvoiceVersion.cs`).

## What Changes

- **PR 1 — ядро: запрещающие строки закрытия.**
  - Раздел модуля получает строки, запрещающие закрытие. Предпросмотр несёт их полем `blocking` и
    `canClose`, закрытие при ненулевой строке — 409 без записи.
  - Диалог закрытия показывает запрещающие строки и рядом с правилом даты модуля — правило ядра «граница
    по поясу компании»; кнопка «Закрыть» заперта с причиной.
  - ~~Базовый тип записи версии и перепись в `BHS.CRG.Modules/Data`~~ — строит
    `plan-12-record-versions` (D18 зонтика); здесь только проверка, что перепись видит наследника `work`.
- **PR 2 — модуль `work`.**
  - Раздел `work`: правило даты, «в окне подачи: N» (запрещает), «ждут приёмки: N» (оба запрещают, `WORK-59`), принятые.
  - Отказ любой правки отчёта закрытого периода, включая приёмку и возврат: ждущих там не бывает.
  - `POST /api/work/reports/{id}/correction` — новая версия с причиной под `work.report.review` и
    `If-Match`; история `versions` и взгляд `view=at-closing` под изоляцией отчёта.
  - Досылка к принятому в открытом периоде — снова «подан» с пометкой `accepted-changed`.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `core`: запрещающие строки закрытия, правило даты вслух. ~~Базовый тип записи версии~~ — заводит
  `plan-12-record-versions` (D18 зонтика).
- `work`: раздел закрытия, отказ правки закрытого, исправление новой версией, досылка к принятому.

## Impact

- **Сервер, PR 1:** `BHS.CRG.Modules/Ports/IModuleClosingReport.cs`, `Domain/Periods/ClosingReport.cs`,
  `Api/Modules/Ports/ModuleClosingReports.cs`, `Infrastructure/Periods/PeriodClosureService.cs`,
  `Api/Endpoints/Core/PeriodEndpoints.cs`. `BHS.CRG.Modules/Data/ModuleRecordVersion.cs` — готовый,
  из `plan-12`.
- **Сервер, PR 2:** `BHS.CRG.Modules.Work` — раздел закрытия, версия отчёта, адрес исправления, правило
  досылки; миграция схемы `work` (таблица версий).
- **Клиент:** `features/periods/ClosingSections.tsx`, `PeriodsPage.tsx`, `shared/api/periods.ts`.
- **Тесты:** `PeriodClosureTests`, `PeriodClosureInventoryTests`, `RecordVersionInventoryTests` (`plan-12`),
  `WorkClosingReportTests`, `ReportCorrectionTests`.
- **Документы:** `docs/USER_GUIDE.md` — закрытие периода с учётом работ, исправление отчёта.
