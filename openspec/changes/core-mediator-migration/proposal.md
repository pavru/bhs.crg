# Proposal: переход с MediatR 12.5 на Mediator (martinothamar, MIT)

> **В работе по поручению владельца 23.11.2026.** Прежняя отсрочка от 17.10.2026 снята.

## Why

Issue #1406 (enhancement, dependencies). В #1405 решение вернулось на MediatR 12.5.0 — последнюю ветку
под Apache 2.0. С 13.0 MediatR выходит под коммерческой лицензией Lucky Penny или RPL‑1.5, обновлений
ветки 12.x больше не будет. Живая замена с близким API — Mediator (martinothamar): MIT, обработчики
находит генератор кода при сборке, а не отражение при старте.

## What Changes

- Пакет `MediatR` уходит из решения:
  - в `BHS.CRG.Application` и прочие проекты с сообщениями и обработчиками — `Mediator.Abstractions`;
  - генератор `Mediator.SourceGenerator` — **только** в `BHS.CRG.Api`.
- `Handle` у каждого обработчика возвращает `ValueTask<T>` вместо `Task<T>`; пространство имён
  `Mediator` вместо `MediatR`. Имена интерфейсов (`IRequest`, `IRequestHandler`, `IMediator`,
  `ISender`) почти те же.
- Регистрация `AddMediatR(…)` → `AddMediator(o => …)` с **`ServiceLifetime.Scoped`**: у Mediator по
  умолчанию Singleton, у MediatR — Transient, а наши обработчики берут `DbContext`.
- Сторожа:
  - `MediatRLicenseTests` переписан на «пакета `MediatR` в решении нет»;
  - новый тест времени жизни;
  - новый тест «генератор нашёл все обработчики».
- Убираются диапазон версии MediatR в `BHS.CRG.Application.csproj` и пункт 5 `.github/dependabot.yml`
  про MediatR. `docs/DEV_NOTES.md` («Поставка») называет новую библиотеку и главную ловушку — время
  жизни.
- Поведение продукта не меняется: адреса, ответы и сценарии те же.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- Нет: замена библиотеки без изменения поведения, дельты спецификаций нет (`skip_specs: true`).

## Impact

- **Объём (пересчитан 23.11.2026, master 2.135.0):**
  - обработчиков `IRequestHandler<…>` — 156, все в `BHS.CRG.Application` (37 файлов); без ответа
    (`IRequestHandler<TCommand>`) — 20; запросов `IRequest` — 156;
  - файлов с `using MediatR`: Application — 49, Api — 39, Infrastructure — 6, тесты — 116. Модули
    (`BHS.CRG.Modules*`), Domain и Plugins MediatR не используют;
  - обращений к `IMediator` / `ISender` / `IPublisher`: в коде — 216 (Api 206, Infrastructure 6,
    Application 4), в тестах — 231;
  - вызовов `.Send(`: в коде — 238 (Api 215, Infrastructure 16, Application 7), в тестах — 1115;
  - уведомлений (`INotification`, `Publish`), pipeline behaviors, потоковых и обобщённых запросов —
    0. Регистрация — одна сборка (`Configuration/ServiceRegistration.Domain.cs`).
- **Проекты:** `BHS.CRG.Application.csproj`, `BHS.CRG.Api.csproj`, `BHS.CRG.Infrastructure.csproj`,
  `BHS.CRG.Tests.csproj`.
- **Зависит от:** #1405 (слит — `MediatRLicenseTests` и диапазон версии на месте).
