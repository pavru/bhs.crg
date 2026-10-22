# Tasks: встроенный профиль распознавания из копии заменяет тёзку

Функциональный PR (bug) — без метки `version:minor`, в описании `Closes #1570`.

## 1. Тёзки профилей при восстановлении

- [ ] 1.1 Обобщить проход по схеме из `BackupService.Restore.TypeNamesakes.cs`: держатель любого
  идентификатора с исключённой собственной колонкой (D6) [CORE-26]. Проверка — `RestoreModuleTypesTests`
  зелёные без правки
- [ ] 1.2 Partial `BackupService.Restore.ProfileNamesakes.cs`: `ReplaceProfileNamesakesAsync` — тёзка по
  точному `Code` среди профилей копии, которые восстановление положит; переписать
  `dataset_files."RecognitionProfiles"` и `dataset_files."Grouping"` на идентификатор из копии; проход по
  схеме (без `activity_log`) и `IRecordHolders` — держатель есть или порт не ответил → `ConflictException`
  с названием профиля и держателя; иначе удалить тёзку и записать предупреждение; правленный тёзка —
  предупреждение о потерянной правке (D1–D4) [CORE-25] [CORE-26]. Вызов в `BackupService.Import.cs` перед
  `RestoreRecognitionProfilesAsync`, в той же транзакции (D5). Проверка — задачи 1.3–1.5
- [ ] 1.3 Интеграционный тест: копия базы, где правлен встроенный `invoice`, восстанавливается в базу,
  засеянную заново (другие `Id` встроенных профилей) — успех, профиль `invoice` один, в редакции из
  копии, в отчёте предупреждение о замене [CORE-26]. Проверка — тест красный на master (отказ `23505`),
  зелёный с 1.2
- [ ] 1.4 Тест: набор экземпляра привязан к здешнему `invoice` картой профилей и группа листов — к
  здешнему `spec-table`; после восстановления обе ссылки указывают на профили из копии [CORE-25].
  Проверка — тест зелёный; без переписи (1.2) — красный
- [ ] 1.5 Тест: идентификатор тёзки лежит в колонке, которую восстановление не переписывает — отказ
  называет профиль и `схема.таблица.колонка`, база в прежнем состоянии (транзакция откатилась) [CORE-26].
  Проверка — тест зелёный
- [ ] 1.6 Доккомментарий нового partial — правило и причина, как у `ReplaceTypeNamesakesAsync`;
  `docs/DEV_NOTES.md`, раздел о резервной копии: тёзки встроенных профилей. Проверка — ревью PR

## 2. Верификация

- [ ] 2.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~RestoreModuleTypes|FullyQualifiedName~RestoreRecognitionProfile|FullyQualifiedName~BackupService|FullyQualifiedName~BackupManifest"` — зелёный
- [ ] 2.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся)
- [ ] 2.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 2.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный, `BackupService.Restore.cs` не вырос
