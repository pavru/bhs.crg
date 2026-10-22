# Tasks: порт обработки фото, лимиты и учёт места

## 1. Подготовка фото

- [ ] 1.1 Контракт `BHS.CRG.Modules/Ports/IModulePhotos.cs`: `PrepareAsync`, `PhotoLimits`, `PhotoResult` —
  подготовленное фото или отказ значением [WORK-33] [CORE-34]. Проверка — `ModuleBoundaryTests` зелёный:
  контракт без ссылок на наши проекты
- [ ] 1.2 `Infrastructure/Generation/PhotoPreparer.cs`: чтение с пределом `MaxBytes + 1`, проверка пикселей по
  заголовку, поворот по `EncodedOrigin`, уменьшение и превью, JPEG всегда [WORK-33] [WORK-55]. Проверка —
  `PhotoPreparerTests`: 30 МБ при лимите 15 — отказ, прочитано не больше лимита; PNG 20000×20000 — отказ без
  декодирования; ориентация 6 — книжный результат; веб-фото 4000×3000 — 2400×1800
- [ ] 1.3 `Infrastructure/Generation/ExifReader.cs`: GPS-широта, долгота, высота и `DateTimeOriginal`
  из одного сегмента APP1 не больше 64 КБ, каждое смещение проверено [WORK-55]. Проверка — тесты:
  снимок с координатами даёт `geo`, обрезанный и зацикленный EXIF — `geo: null` без исключения, в
  результате нет APP1
- [ ] 1.4 `Api/Modules/Ports/ModulePhotosPort.cs`, регистрация в `ModulePorts.cs` [CORE-34]. Проверка —
  `ModulePortsTests`: тестовый модуль получает порт, отказ лимита не оставляет записи в реестре блобов

## 2. Учёт места

- [ ] 2.1 Код модуля у холдера файла модуля (`BackupService.BlobSelection.cs`), суммы по модулям в
  `MeasureAsync`, поле `Modules` в `BackupSizeVariant` — все включённые модули, в том числе с нулём
  [WORK-35] [CORE-26]. Проверка — `BackupServiceTests.Manifest`: скан счёта `costs` в разбивке, модуль без
  файлов — `bytes: 0, count: 0`
- [ ] 2.2 Клиент: тип ответа в `shared/api/backup.ts`, строка «из них файлы модулей: …» в
  `features/settings/BackupSizeLine.tsx` [WORK-35]. Проверка — vitest строки веса; `npm run ui:ratchet` без
  роста
- [ ] 2.3 `docs/ADMIN_GUIDE.md`: в главе о резервной копии — что такое «файлы модулей» в весе копии.
  Перестроить содержание: `cd docs/tools && npm run toc` [WORK-35]. Проверка — проверка содержания в
  «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PhotoPreparer|FullyQualifiedName~ExifReader|FullyQualifiedName~ImageDownscaler|FullyQualifiedName~ModulePorts|FullyQualifiedName~BackupService|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
