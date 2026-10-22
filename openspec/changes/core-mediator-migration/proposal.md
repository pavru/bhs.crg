# Proposal: переход с MediatR 12.5 на Mediator (martinothamar, MIT)

> **Не к выпуску; брать при условиях из issue.** Решение владельца 17.10.2026: не сейчас. Берём, когда
> будет свободное время или раньше срока, если:
> - в MediatR 12.5.0 найдена уязвимость или он несовместим с новой платформой .NET;
> - понадобилось то, чего в 12.x нет;
> - профилировщик показал стоимость отражения и выделений памяти.

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

- **Объём (пересчитан 22.10.2026, master 2.17.11):**
  - обработчиков `IRequestHandler<…>` — 152, все в `BHS.CRG.Application` (34 файла); без ответа
    (`IRequestHandler<TCommand>`) — 20; запросов `IRequest` — 152;
  - файлов с `using MediatR`: Application — 46, Api — 26, Infrastructure — 6, тесты — 95. Модули
    (`BHS.CRG.Modules*`), Domain и Plugins MediatR не используют;
  - обращений к `IMediator` / `ISender` / `IPublisher`: в коде — 175 (Api 166, Infrastructure 6,
    Application 3), в тестах — 203;
  - вызовов `.Send(`: в коде — 205 (Api 183, Infrastructure 16, Application 6), в тестах — 984;
  - уведомлений (`INotification`, `Publish`), pipeline behaviors, потоковых и обобщённых запросов —
    0. Регистрация — одна сборка (`ServiceRegistration.Domain.cs:35`).
- **Проекты:** `BHS.CRG.Application.csproj`, `BHS.CRG.Api.csproj`, `BHS.CRG.Infrastructure.csproj`,
  `BHS.CRG.Tests.csproj`.
- **Зависит от:** #1405 (слит — `MediatRLicenseTests` и диапазон версии на месте).
