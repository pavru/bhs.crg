# Tasks

## 1. Сервер

- [x] 1.1 `AssignmentCandidateDto` несёт `Account` (`{ state: linked | none | deleted }`) из `ModuleEmployee.Account`
  уже прочитанного поиска — без чтения на каждого кандидата; значения строк — те же, что у `EmployeeAccountDto`
  ядра. Проверка — `dotnet build BHS.CRG.slnx` без ошибок.
- [x] 1.2 Тест кандидатов в `AssignmentRulesTests` на три состояния: связанный — `linked`, без связи — `none` и
  `eligible: true` у вида `installer`, связь с удалённой учётной записью — `deleted`. Проверка —
  `dotnet test --filter AssignmentRulesTests` зелёный.

## 2. Клиент и документация

- [x] 2.1 Тип кандидата назначения с полем `account` в `src/client/src/shared/api/work.ts` (состояние — тем же
  типом, что `EmployeeAccount['state']`). Проверка — `npx tsc -b` без ошибок.
- [x] 2.2 Строка `GET /api/work/assignments/candidates` в листинге REST API `docs/DEV_NOTES.md` называет
  `account: { state }`. Проверка — строка в diff PR.

## Workflow follow-up

- После слияния кода владельцем — архив change отдельным PR (`/openspec-archive-change`).
- После слияния архива — сообщить сессии `work-12`, что поле в master.
