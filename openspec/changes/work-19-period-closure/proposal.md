# Proposal: закрытие периода в учёте работ

Дочерний change зонтика `work-00-epic`, общая архитектура — `../work-00-epic/design.md` (D14, D11, D8).

> **Базовый тип версии записи** (`CORE-36`) строит этап 3 — `plan-12-record-versions`, первым
> потребителем идут версии КП (решение нарезки этапа 3 от 22.10.2026, D18 зонтика). Здесь версии отчёта
> строятся на готовом типе.

## Why

После закрытия периода отчёты не меняются, исправление — новой версией с причиной (`WORK-15`); досылка
к принятому возвращает его в приёмку (`WORK-16`). Служба закрытия ядра построена этапом 2
(`CORE-35`), но раздел модуля умеет только перечислять (`ModuleClosingSection` — `Unfinished` и
`Frozen`, `BHS.CRG.Modules/Ports/IModuleClosingReport.cs:48-55`). Решение владельца — отчёты в
открытом окне подачи **запрещают** закрытие (Р-6, `WORK-52`): граница считается по поясу компании, дата
смены — по поясу стройки, и стройка западнее компании иначе закрылась бы раньше, чем истекло её окно;
автоподача упёрлась бы в закрытый период, и черновик остался бы черновиком навсегда. Версии записей —
один приём ядра (`CORE-36`), но базовый тип есть только у счёта (`Modules.Costs/Data/InvoiceVersion.cs`).

## What Changes

- **PR 1 — ядро.**
  - Раздел модуля получает строки, запрещающие закрытие. Предпросмотр несёт их полем `blocking` и
    `canClose`, закрытие при ненулевой строке — 409 без записи.
  - Диалог закрытия показывает запрещающие строки и рядом с правилом даты модуля — правило ядра «граница
    по поясу компании»; кнопка «Закрыть» заперта с причиной.
  - Базовый тип записи версии и перепись в `BHS.CRG.Modules/Data`. `costs` на него не переносится.
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
- `core`: запрещающие строки закрытия, правило даты вслух, базовый тип записи версии.
- `work`: раздел закрытия, отказ правки закрытого, исправление новой версией, досылка к принятому.

## Impact

- **Сервер, PR 1:** `BHS.CRG.Modules/Ports/IModuleClosingReport.cs`, `Domain/Periods/ClosingReport.cs`,
  `Api/Modules/Ports/ModuleClosingReports.cs`, `Infrastructure/Periods/PeriodClosureService.cs`,
  `Api/Endpoints/Core/PeriodEndpoints.cs`; новый `BHS.CRG.Modules/Data/ModuleRecordVersion.cs`.
- **Сервер, PR 2:** `BHS.CRG.Modules.Work` — раздел закрытия, версия отчёта, адрес исправления, правило
  досылки; миграция схемы `work` (таблица версий).
- **Клиент:** `features/periods/ClosingSections.tsx`, `PeriodsPage.tsx`, `shared/api/periods.ts`.
- **Тесты:** `PeriodClosureTests`, `PeriodClosureInventoryTests`, `RecordVersionInventoryTests`,
  `WorkClosingReportTests`, `ReportCorrectionTests`.
- **Документы:** `docs/USER_GUIDE.md` — закрытие периода с учётом работ, исправление отчёта.
