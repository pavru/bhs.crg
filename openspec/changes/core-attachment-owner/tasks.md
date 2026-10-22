# Tasks: владелец у файла хранилища

## 1. Владелец в реестре

- [x] 1.1 Колонка `Owner` у `BlobRegistryEntry` (обязательная, `core` или код модуля), миграция ядра
  `dotnet ef migrations add BlobRegistryOwner --project src/server/BHS.CRG.Infrastructure
  --startup-project src/server/BHS.CRG.Api --context AppDbContext` [CORE-37.2]. Проверка — на
  пересозданной базе старт применяет миграцию
- [x] 1.2 Разовый сбор владельцев в той же миграции: путь из колонки схемы модуля — код модуля,
  остальное — `core` [CORE-37.2]. Проверка — `BlobRegistryOwnerBackfillTests` (файлы ядра и `costs`
  раздаются верно, второй прогон ничего не меняет)
- [ ] 1.3 До слияния прогнать сбор на копии базы заказчика: каждый путь из колонок `IAppModule.Files`
  получил своего владельца, реквизитов ИД с путём файла `costs` нет. Проверка — отчёт в описании PR

## 2. Дверь хранилища

- [x] 2.1 `RegisteredBlobStorage`: запись с владельцем, `DownloadAsync(path)` — только `core`,
  `DownloadAsync(path, owner)` — с владельцем; чужой — то же исключение «нет файла» [CORE-37.2]
  [WORK-39]. Проверка — `BlobOwnerTests`: файл модуля общим адресом — 404, вложение ядра — 200,
  без `core.files.use` — 403
- [x] 2.2 Материализация картинок и отладочный ZIP не берут файл модуля; генерация называет поле
  [CORE-37.2]. Проверка — `BlobOwnerTests`, сценарий «путь файла модуля в реквизитах»
- [x] 2.3 Перепись читателей хранилища вне `Storage/` [CORE-37.2]. Проверка —
  `BlobReaderInventoryTests` красный на чтении файла модуля без владельца

## 3. Порт модуля и службы ядра

- [x] 3.1 `IModuleBlobs` — keyed-сервис на каждый включённый модуль, порт подставляет код модуля;
  без ключа сервиса нет [CORE-37.2]. Проверка — порт `work` не открывает файл `costs`; хост без ключа
  отказывает на старте
- [x] 3.2 `costs` берёт порты `IModuleBlobs` и `IModuleRenditions` по ключу; `RenditionStore` читает
  оригинал от имени `costs` [CORE-37.2].
  Проверка — `RenditionStoreTests` и `InvoiceScanHost`-тесты зелёные, скан счёта адресом модуля
  читается
- [x] 3.3 Резервная копия переносит владельца [CORE-37.2]. Проверка — `BackupManifestCoverageTests`,
  восстановление возвращает файлам `costs` их владельца

## 4. Документы

- [x] 4.1 `docs/DEV_NOTES.md`: владелец файла и keyed-порт модуля; доккомментарий `BlobRegistryEntry`
  обновлён — условие пересмотра исполнено для файлов модулей. Проверка — ревью PR

## 5. Верификация

- [x] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~BlobOwner|FullyQualifiedName~BlobRegistry|FullyQualifiedName~BlobReaderInventory|FullyQualifiedName~Rendition|FullyQualifiedName~InvoiceScan|FullyQualifiedName~BackupManifest"` — зелёный
- [x] 5.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся)
- [x] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
