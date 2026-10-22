# Tasks: навигация этапа 4

## 1. Перед стартом

- [ ] 1.1 Сверить с master механизмы `plan-02-navigation` (`NavItem.entry`, `entryPage.ts`, «любое из») и
  `plan-21` (право контракта); расхождение вписать в `design.md` [AUTH-16.1]. Проверка — раздел
  «Context» `design.md` называет сверенный коммит
- [ ] 1.2 Вписать ссылку на выбранный владельцем макет Р0 в `design.md` [AUTH-16.1]. Проверка — ссылка в
  разделе «Макеты»

## 2. Раздел `work` и страница входа модуля (PR 1)

- [ ] 2.1 `RailId` += `'work'`, строка в `railSections` (подпись — по Р0), раздел без видимых пунктов и
  при одном праве контракта не строится [AUTH-16.1] [WORK-2]. Проверка — `railSections.test.ts`: пустой
  раздел `work` не попадает в рейл; `e2e/rail-checks.mjs` зелёный
- [ ] 2.2 Таблица рангов входа пунктов `work` в `navConfig.ts`: «Мои отчёты» — меньше ранга Главной (`HOME_ENTRY_RANK`) и пунктов ядра,
  остальные и их порядок — по Р0 [AUTH-16.1] [WORK-2]. Проверка — `entryPage.test.ts`:
  монтажник — «Мои отчёты», а не Главная; «Администратор» с `work.report.own` — Главная (правило одного
  модуля, `plan-02`); пункт `work` с рангом больше `HOME_ENTRY_RANK` уступает Главной; один
  `work.facts.read` — вход как без модуля; модуль выключен — вход как до модуля
- [ ] 2.3 Весь список живых прогонов: `cd src/client && MSYS_NO_PATHCONV=1 npm run test:e2e:ci` — правка
  оболочки [AUTH-16]. Проверка — все прогоны зелёные, в том числе `limited-smoke` под ограниченной ролью
- [ ] 2.4 `docs/USER_GUIDE.md`: раздел модуля в рейле, что открывается монтажнику после входа; перестроить
  содержание: `cd docs/tools && npm run toc` [AUTH-16.1]. Проверка — проверка содержания в «Frontend»
  зелёная

## 3. Верификация

- [ ] 3.1 `cd src/client && npm test -- railSections entryPage` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Весь список живых прогонов (`cd src/client && MSYS_NO_PATHCONV=1 npm run test:e2e:ci`) — зелёный
  под ролями «Администратор», «Менеджер проекта», «Монтажник», «Руководитель»
