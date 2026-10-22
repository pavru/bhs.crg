# Tasks: переход с MediatR 12.5 на Mediator

**Не к выпуску; брать при условиях из issue #1406** (см. proposal). PR без изменения поведения — без
метки `version:minor`, в описании `Closes #1406`.

## 1. Пакеты и регистрация

- [ ] 1.1 Перед началом пересчитать объём grep по `IRequestHandler<`, `using MediatR`, `IMediator|ISender|IPublisher`, `.Send(` (по проектам) и поправить
  таблицу, если код ушёл. Проверка — таблица в design.md совпадает с grep на день начала
- [ ] 1.2 Пакеты:
  - `Mediator.Abstractions` — в `BHS.CRG.Application` и проекты с обработчиками и сообщениями;
  - `Mediator.SourceGenerator` — только в `BHS.CRG.Api`;
  - `MediatR` и его диапазон версии убраны (D1).
  Проверка — `cd src/server && dotnet build BHS.CRG.slnx` без ошибок генератора; grep `MediatR` в
  `*.csproj` пуст
- [ ] 1.3 `AddMediator(o => { o.ServiceLifetime = ServiceLifetime.Scoped; … })` вместо `AddMediatR`;
  имя метода регистрации без «MediatR» (D2). Проверка — тестовый хост стартует с проверкой областей
  контейнера; одним классом тестов, создающим обработчик через `new`, подтверждено, что генератор
  тестам не нужен (D1)

## 2. Обработчики и вызовы

- [ ] 2.1 `Task<T>` → `ValueTask<T>` в 152 обработчиках, `using MediatR` → `using Mediator`; 20 команд
  без ответа — по D4. Проверка — сборка решения без ошибок
- [ ] 2.2 Поиск сохранённых и дважды ожидаемых результатов `Send` в коде и тестах (D3); найденное —
  `.AsTask()` с комментарием. Проверка — список мест (или «нет») в описании PR; `CA2012` без
  предупреждений в затронутых проектах

## 3. Сторожа

- [ ] 3.1 `MediatRLicenseTests` → «пакета `MediatR` в решении нет» (csproj и загруженные сборки) (D5).
  Проверка — тест зелёный; возврат `PackageReference` на MediatR делает его красным
- [ ] 3.2 Тест времени жизни: два одновременных запроса к пишущему адресу получают разные `DbContext`
  (D5). Проверка — тест зелёный; без `ServiceLifetime.Scoped` — красный (проверено руками, отмечено в PR)
- [ ] 3.3 Тест «генератор нашёл все обработчики»: реализации `IRequestHandler<,>` / `IRequestHandler<>`
  по отражению совпадают с зарегистрированными (D5). Проверка — тест зелёный; обработчик в
  незаявленной сборке делает его красным

## 4. Документы

- [ ] 4.1 `docs/DEV_NOTES.md` («Поставка»): Mediator вместо MediatR, причина перехода, ловушка времени
  жизни и «генератор только в Api»; `.github/dependabot.yml` — пункт 5 про MediatR убран. Проверка —
  ревью PR; grep `MediatR` в `docs/` и `.github/` — только в историческом абзаце DEV_NOTES

## 5. Верификация

- [ ] 5.1 `bash scripts/backend-tests.sh` — весь backend двумя потоками, как в CI, зелёный
- [ ] 5.2 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~MediatRLicense|FullyQualifiedName~Mediator|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 5.3 Все живые прогоны CI на PR зелёные (условие «Готово» из issue)
- [ ] 5.4 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся)
- [ ] 5.5 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.6 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
