# Tasks: поля заказчика «нужно к приёмке»

## 1. Признак в типе

- [ ] 1.1 `ForAcceptance` в `SchemaFieldInfo` и `FieldSnapshot`, разбор ключа `forAcceptance` в схеме
  [WORK-53] [CORE-19]. Проверка — тест: схема с признаком из `jsonb` и из тела запроса равны по смыслу
- [ ] 1.2 `ModuleRecordType.AcceptanceFields`; `work` объявляет его у `СтрокаРаботы.Сведения`; ответ
  `GET /api/document-types/{id}` отдаёт `acceptanceFields` [WORK-53]. Проверка — тест: у `Сотрудник`
  `false`, у `СтрокаРаботы.Сведения` `true`
- [ ] 1.3 Правила `SchemaEditPolicy`: отказ на поле модуля, на расчётном поле, на типе без приёмки, на
  закрытом типе; на своём поле расширяемого типа — разрешено [WORK-53] [CORE-19] [CORE-19.2]. Проверка —
  новые случаи `SchemaEditPolicyTests` зелёные; без правила отказа случай «тип без приёмки» красный

## 2. Счёт и очередь

- [ ] 2.1 `AcceptanceFields.Missing` на `RequiredFields.IsEmpty` [WORK-53]. Проверка — юнит-тест:
  пустое поле без признака не считается, пустой список и пробелы — пусто
- [ ] 2.2 `missingFields` и `missingFieldsCount` в чтении отчёта, расчёт по действующей схеме [WORK-53].
  Проверка — интеграционный тест: снятый признак снимает пометку; чужой отчёт — 404
- [ ] 2.3 Код `fields-missing` в каталоге очереди `work-16`: пометка, счётчик по коду, отбор [WORK-44]
  [WORK-28]. Проверка — тест очереди: отбор возвращает только такие отчёты своих строек; сторож
  каталога сервера и клиента зелёный
- [ ] 2.4 Приёмка с пустыми помеченными полями не отказывает [WORK-53]. Проверка — интеграционный тест:
  принятый отчёт сохраняет `missingFields`
- [ ] 2.5 Заполнение после подачи — запись «правка после подачи» в журнале; в закрытом периоде — отказ
  прямой правки и новая версия [WORK-44] [WORK-11] [WORK-15]. Проверка — интеграционные тесты: без
  If-Match — 400, устаревшая версия — 409, без права — 403, модуль выключен — 501

## 3. Клиент

- [ ] 3.1 `shared/api/schema.ts`: `forAcceptance` у поля, `acceptanceFields` у типа [WORK-53]. Проверка —
  `cd src/client && npx tsc -b`
- [ ] 3.2 Колонка признака в редакторе полей (только при `acceptanceFields`) флажком
  `shared/ui/Checkbox`; флаги строки поля — своим файлом, чтобы `FieldBuilder.tsx` не перерос базовый
  уровень [WORK-53]. Проверка — vitest на видимость колонки; `FileSizeRatchet` и `ui:ratchet` без роста
- [ ] 3.3 Пометка «нужно к приёмке» у пустого помеченного поля в форме строки (`work-22`), список
  незаполненных в карточке и чип `fields-missing` в очереди (`work-21`) — по выбранному макету Р3
  [WORK-44] [WORK-53]. Проверка — vitest: пометка берётся из `missingFields`, клиент её не вычисляет
- [ ] 3.4 `docs/ADMIN_GUIDE.md` (признак поля) и `docs/USER_GUIDE.md` (отбор и пометка в очереди),
  содержание — `npm run toc` в `docs/tools`. Проверка — проверка содержания в «Frontend» зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~SchemaEditPolicy|FullyQualifiedName~AcceptanceFields|FullyQualifiedName~WorkReview|FullyQualifiedName~WorkReport"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
