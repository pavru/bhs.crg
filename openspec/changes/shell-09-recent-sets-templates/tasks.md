# Tasks: комплекты и шаблоны в «Недавних» Главной

Один PR, метка `version:minor`. Код — после ответов владельца на 0.1–0.4 и слияния архива `shell-08-home`
(PR #1918). Задачи ниже написаны под рекомендации (`design.md`, Р-2, Р-3, Р-5); другой ответ — сначала
правка `design.md`, дельт и этого списка, потом код.

## 0. До кода — решения владельца

- [ ] 0.1 Комплекты и шаблоны в палитре (Ctrl K): **Б** (рекомендация) — группы «только карточки», палитра
  не меняется, поиск ими — отдельный change с макетом ряда чипов; **А** — группы ищутся этим же change,
  палитра получает чипы «Комплекты» и «Шаблоны» (тогда нужны три макета палитры, дельта `core` отпадает, а
  `sharedui` правит [UI-30] и [UI-31]) [AUTH-16.1] [CORE-34] [UI-30]. Проверка — ответ вписан в `design.md`, Р-2
- [ ] 0.2 Права карточек: комплект — `id.document.read`; шаблон — `id.config.edit` (рекомендация: право
  экрана «Шаблоны») или `id.document.read` (право `GET /api/templates`; бывший администратор получал бы
  строку «не показано») [ID-13] [AUTH-8]. Проверка — ответ вписан в `design.md`, Р-3
- [ ] 0.3 Куда ведёт строка шаблона: **Б** (рекомендация) — на действующую версию, версии одного шаблона —
  одной строкой; **А** — на открытую версию, каждая версия своей строкой; **В** — погашенная версия не
  показывается [ID-13] [UI-35.1]. Проверка — ответ вписан в `design.md`, Р-5; сценарий «Шаблон получил новую
  версию» в дельте `id` ему соответствует
- [ ] 0.4 Макеты: подтвердить, что change под правило трёх макетов не попадает (нового экрана и
  переделки взаимодействия нет, строки «Недавних» те же; `design.md`, «Макеты»). При ответе А на 0.1 —
  три макета палитры до кода [UI-35.1]. Проверка — ответ вписан в `design.md`, «Макеты»
- [ ] 0.5 Строка change в зонтике `shell-00-epic/tasks.md` (после слияния #1918 — он правит тот же файл).
  Проверка — строка с этим change и его метками в разделе 1 зонтика

## 1. Контракт «Поиск записей»

- [ ] 1.1 `RecordSearchGroup` += необязательный `Searchable = true`; `RecordSearchService.SearchAsync`
  не спрашивает группы с `false`, сужение `group=` до такой — 400 «неизвестная группа поиска»; `CardsAsync`
  признака не смотрит [AUTH-16.1] [CORE-34]. Проверка — `RecordSearchTests`:
  группа «только карточки» не приходит в поиске, `group=` — 400, её карточки отдаются; прежние группы
  ищутся как были
- [ ] 1.2 `RecordSearchHit` += необязательный `Opens: RecordSearchOpens?` (`Under`, `Id`); ответ
  `GET /api/account/recent` несёт его в `RecentRecordView` [CORE-25.3]. Проверка — серверный тест: у счёта,
  стройки и набора данных `opens` нет (прежний ответ не изменился)

## 2. Сервер: поставщик модуля `id`

- [ ] 2.1 `IdRecordSearch` в `BHS.CRG.Api` (рядом с `IdModule`): группы `sets` («Комплекты»,
  `КомплектДокументов`, `id.document.read`) и `templates` («Шаблоны», `ШаблонДокумента`, ключ по 0.2), обе
  `Searchable: false` (по 0.1), `Archive` — `None` у комплекта, у шаблона — по 0.3; регистрация
  `AddModuleContract<IRecordSearch, IdRecordSearch>("id")` в `IdModule.RegisterServices` [ID-13] [CORE-34].
  Проверка — старт: `RecordSearchCatalog` принимает обе группы; переписи `ModuleContractProbes` и
  `ContractPermissionGateTests` дописаны и зелёные
- [ ] 2.2 `CardsAsync("sets")`: комплекты по идентификаторам одним запросом с разделом и стройкой —
  `Title` = название, `Detail` = раздел, `Place` = стройка, `Opens.Under` = стройка; ответ «скрыто» без
  ключа [ID-13] [CORE-25.3]. Проверка — `RecentRecordsTests`: отметка комплекта — 204, карточка с местом;
  удалённый комплект пропадает; без `id.document.read` комплектов нет, остальное отдаётся
- [ ] 2.3 `CardsAsync("templates")`: по любой версии — шаблон `(DocumentTypeId, Name)` и его действующая
  версия, одним запросом на всех спрошенных; `Title` = название, `Detail` = тип документа и «v<N>»,
  `Opens` = (тип документа, действующая версия) — по 0.3 [ID-13] [CORE-25.3]. Проверка —
  `RecentRecordsTests`: отметка v3, затем новая версия v4 — карточка ведёт на v4; шаблон удалён — карточки нет
- [ ] 2.4 `RecentRecordEndpoints.ReadAsync` сворачивает строки одного `(RecordType, Opens.Id)`, оставляя
  позднее открытие (только при Б на 0.3) [CORE-25.3]. Проверка — `RecentRecordsTests`: отметки v3 и v4
  одного шаблона — одна строка, со временем позднего открытия
- [ ] 2.5 Отказ поставщика `id` — группа в `refused`, остальные записи отдаются [ID-13]. Проверка —
  `RecentRecordsTests` с поставщиком, бросающим исключение (как у отказа группы `costs`)

## 3. Клиент

- [ ] 3.1 `RecordGroupDef` += `inPalette?: false`; `openGroups` такие группы пропускает; `RECORD_GROUPS` +=
  `sets` («Комплекты», знак из `shared/ui/icons`) и `templates` («Шаблоны», `Description`), обе
  `inPalette: false` (по 0.1) [UI-35.1] [UI-31]. Проверка — `paletteSearch.test.ts` (или `CommandPalette.test.tsx`): чипов и групп «Комплекты» и
  «Шаблоны» нет
- [ ] 3.2 `RECORD_ROUTES` += `КомплектДокументов` (экран `/document-sets`, `open` →
  `/document-sets/<Under>/sets/<id>`) и `ШаблонДокумента` (экран `/templates`, `open` →
  `/templates?type=<Under>&template=<Id ?? id>`); `RecordRoute.open` по образцу `at`; `recentTarget`
  зовёт его; тип `RecentRecord` += `opens` [UI-35.1]. Проверка — новый `recentLink.test.ts`: адреса обоих типов;
  без `core.constructions.read` у комплекта и без `id.config.edit` у шаблона — `null`
- [ ] 3.3 `useRecentTouch('КомплектДокументов', set?.id)` в `SetDetail`, `useRecentTouch('ШаблонДокумента',
  selectedTemplate?.id)` в `TemplatesPage` — когда запись показана [UI-35.1]. Проверка —
  `recordRoutesRecent.test.ts` зелёный без правки (перепись видит оба типа сама)
- [ ] 3.4 Блок «Недавние» показывает строки комплекта и шаблона с местом справа; записи без экрана —
  в строке «не показано» [UI-35.1]. Проверка — `HomePage.test.tsx`: строка комплекта со стройкой, строка
  шаблона с типом документа; комплект без права строек — в счёте «не показано»

## 4. Документы и живой прогон

- [ ] 4.1 `docs/USER_GUIDE.md`, Главная: «Недавние» помнят и комплекты, и шаблоны (оглавление — `cd
  docs/tools && npm run toc`, если менялись заголовки) [UI-35.1]. Проверка — ревью PR; проверка «Frontend»
  (оглавление) зелёная
- [ ] 4.2 Живой прогон: открыть комплект → Главная → комплект первым в «Недавних», щелчок открывает его
  [UI-35.1]. Проверка — прогон в списке живых прогонов CI зелёный

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~RecentRecords|FullyQualifiedName~RecordSearch|FullyQualifiedName~ModuleContract|FullyQualifiedName~ContractPermissionGate|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 5.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 5.5 Весь список живых прогонов CI зелёный (зонтик `shell-00-epic`, D6)

## Workflow follow-up

- Архив — `/opsx:archive shell-09-recent-sets-templates` отдельным PR после слияния кода; при архиве
  требования [ID-13], [UI-35.1] и требование группы «только карточки» в `core` ложатся в `openspec/specs`.
- Issue не заводится: change — продолжение `shell-08-home` (#1436 закрывает его последний PR).
