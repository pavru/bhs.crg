# Tasks: фото строки работы под изоляцией отчёта

## 1. Условие слияния

- [ ] 1.1 Слит change ядра `core-attachment-owner` (условие безопасности — локальный отчёт аудита, раздел 2). Проверка —
  отметка в локальном отчёте аудита; в PR и issue — только ссылка на раздел

## 2. Хранение и загрузка

- [ ] 2.1 Таблица `work.line_photos` (клиентский `id`, `state`, `blob_path`, `preview_blob_path`),
  миграция схемы `work`, чтение только методом с параметром доступа [WORK-33] [WORK-40]. Проверка —
  перепись изоляции `work-07` зелёная, `ModuleDbContextInventoryTests` зелёный
- [ ] 2.2 `PUT /api/work/reports/{id}/lines/{lineId}/photos/{photoId}` через порт фото `work-05`: лимиты
  до записи в хранилище, повтор не дублирует, занятый идентификатор — 409 [WORK-33] [MOB-15] [WORK-55].
  Проверка — `Work/PhotoUploadTests`: повтор — одно фото; сверх размера — 413 и пустое хранилище;
  сверх числа — 400; оригинал сохраняет геометку
- [ ] 2.3 `DELETE …/photos/{photoId}` с `If-Match` (ссылка, потом файл); загрузка — названное исключение
  без версии, перечислено в тесте адресов модуля [AUTH-8] [AUTH-9]. Проверка — тест: удаление без
  заголовка 400, с устаревшей версией 409; загрузка после правки отчёта проходит; руководитель 403;
  выключенный модуль 501
- [ ] 2.4 Строка перечисляет `photos: [{id}]`; объявленное до подачи — доставка, новое после подачи —
  «правка после подачи» в журнал [WORK-11] [WORK-34]. Проверка — тест на обе ветки

## 3. Выдача и носители

- [ ] 3.1 `GET /api/work/photos/{photoId}` и `…/preview`: отчёт читается с параметром доступа, иначе
  404; фото черновика неавтору — 404 [WORK-39] [WORK-54]. Проверка — `Work/PhotoAccessTests`: чужой
  монтажник 404 на оба адреса, менеджер своей стройки получает превью
- [ ] 3.2 `state` у фото, `photosPending` у отчёта; данные пометки `photos-pending` очереди [WORK-34]
  [WORK-28]. Проверка — тест: подача с двумя недогруженными — пометка «фото не загружены: 2», после
  догрузки — снята; приёмка при `photosPending: 1` проходит
- [ ] 3.3 Типы фото в `src/client/src/shared/api/work.ts`. Проверка — `npx tsc -b` без ошибок

## 4. Копия, место и документы

- [ ] 4.1 `WorkModule.Files` — `blob_path` и `preview_blob_path` с `Carried: true`; `WorkDbContext`
  вписан в `ModuleFileInventoryTests.Modules()` [WORK-35] [CORE-26]. Проверка — перепись зелёная;
  убранное объявление её роняет
- [ ] 4.2 Круг «снял — восстановил» с фото [WORK-35]. Проверка — `Work/PhotoBackupRoundTripTests`:
  фото и превью открываются по прежнему идентификатору, отчёт копии без необъявленных колонок;
  `BackupManifestCoverageTests` зелёный
- [ ] 4.3 `docs/ADMIN_GUIDE.md`: лимиты фото, место хранилища, геометка и уведомление сотрудников
  [WORK-55]; содержание — `cd docs/tools && npm run toc`. Проверка — проверка содержания в «Frontend»
  зелёная

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Work.Photo|FullyQualifiedName~ModuleFileInventory|FullyQualifiedName~BackupManifestCoverage|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
