# Tasks: навигация этапа 4

## 1. PR 1: «любое из прав» и раздел рейла

- [ ] 1.1 Сервер: `AppPolicies.AnyPermission(...)`, разбор списка в провайдере с `Declared()` по
  каждому коду, `AnyPermissionRequirement` и обработчик; атрибут `McpPermission` со списком [AUTH-8]
  [AUTH-8.2] [AUTH-12.1]. Проверка — новый `AnyPermissionPolicyTests` на тестовом модуле: адрес открыт
  носителю каждого права, закрыт без обоих; необъявленный код роняет сборку политики
- [ ] 1.2 Переписи `PermissionGateTests`, `EndpointGateInventoryTests`, `ReadAllWritesNothingTests` и
  `McpGateInventoryTests` понимают составное имя [AUTH-9] [AUTH-5.2]. Проверка — тестовый пишущий адрес
  под «любым из» с правом из `*.read.all` называется `ReadAllWritesNothingTests`
- [ ] 1.3 Клиент: `NavItem.permission: string | string[]`, `allowed` и `missingFor` по списку,
  `NoAccessPage.tsx` — «нужно одно из прав: …»; палитра команд отбирает тем же `visibleNav` [AUTH-16]
  [AUTH-15]. Проверка — vitest `navAccess.test.ts`: пункт виден при любом праве списка, `missingFor`
  называет все коды
- [ ] 1.4 `RailId` += `'work'`, строка в `railSections` (подпись — по Р0, до выбора — рабочая «Работы»),
  раздел без видимых пунктов не строится [AUTH-16.1] [WORK-2]. Проверка — `railSections.test.ts`: пустой
  раздел `work` не попадает в рейл; `e2e/rail-checks.mjs` без изменений зелёный
- [ ] 1.5 Весь список живых прогонов: `cd src/client && MSYS_NO_PATHCONV=1 npm run test:e2e:ci` — правка оболочки
  [AUTH-16]. Проверка — все прогоны зелёные, в том числе `limited-smoke` под ограниченной ролью

## 2. PR 2: страница входа и панели карточки стройки, после выбора макета Р0

- [ ] 2.1 Вписать ссылку на выбранный владельцем макет Р0 в `design.md` этого change [AUTH-16.1]. Проверка —
  ссылка в разделе «Макеты»
- [ ] 2.2 `NavItem.entry?: number` и `shared/ui/entryPage.ts`: наименьший ранг среди видимых в текущем
  пространстве, иначе первый видимый, иначе страница `AUTH-15`; индекс-маршрут `App.tsx` зовёт её
  вместо `/document-sets` [AUTH-16.1] [AUTH-16.3] [WORK-2]. Проверка — vitest `entryPage.test.ts`: роль без
  `core.constructions.read`, пункт вне пространства, пустая навигация
- [ ] 2.3 `shared/ui/constructionPanels.ts` — реестр панелей с правом или модулем и разделом ядра;
  `ConstructionDetail.tsx` строит по нему пункты и содержимое; «Каталог», «Наборы данных», «Подписчики» —
  в реестре, адреса прежние; панель вне пространства по ссылке — с плашкой [AUTH-16.1] [AUTH-16.2].
  Проверка — vitest на отбор панелей по доступу и пространству; `npm run ui:ratchet` без роста
- [ ] 2.4 Живые прогоны: `e2e/limited-smoke.mjs` — под каждой системной ролью `/` ведёт на видимую
  страницу, все видимые пункты открываются без 403 в сети; `e2e/routing-smoke.mjs` — прежние адреса
  панелей карточки стройки [AUTH-16] [AUTH-16.1]. Проверка — `cd src/client && MSYS_NO_PATHCONV=1 npm run test:e2e:ci`, весь
  список зелёный
- [ ] 2.5 `docs/USER_GUIDE.md`: какая страница открывается после входа, раздел модуля в рейле, панели
  модулей в карточке стройки. Перестроить содержание: `cd docs/tools && npm run toc` [AUTH-16.1].
  Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AnyPermissionPolicy|FullyQualifiedName~PermissionGate|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ReadAllWritesNothing|FullyQualifiedName~McpGateInventory|FullyQualifiedName~WorkspaceIsNotApiParameter"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Весь список живых прогонов (`cd src/client && MSYS_NO_PATHCONV=1 npm run test:e2e:ci`) — зелёный под ролями
  «Администратор», «Менеджер проекта», «Монтажник», «Руководитель»
