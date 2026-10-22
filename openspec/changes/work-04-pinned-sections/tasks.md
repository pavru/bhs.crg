# Tasks: закрепление разделов навигации

## 1. Макет и серверный ключ

- [ ] 1.1 Вписать ссылку на выбранный владельцем макет Р0 (закрепление) в `design.md` этого change
  [AUTH-16.4]. Проверка — ссылка в разделе «Макеты»
- [ ] 1.2 `PinnedSectionValues.IsList` и ключ `pinnedSections` в `UserSettingKeys.All`: не больше 20
  адресов, без повторов, только путь без хоста [AUTH-16.4] [CORE-25.3]. Проверка — `AccountSettingsTests`:
  21 адрес и адрес с хостом — 400 и набор не применён; правильный список читается обратно

## 2. Навигация и профиль

- [ ] 2.1 `shared/ui/pinnedSections.ts` — чистая функция «закрепления + доступ + пространство → меню и
  профиль» со строками `ok`, `no-access`, `gone` [AUTH-16.4] [AUTH-16.3]. Проверка — vitest
  `pinnedSections.test.ts`: снятое право убирает раздел из меню и оставляет в профиле с причиной; адрес
  вне навигации — `gone`; вне пространства — в меню с пометкой
- [ ] 2.2 Группа «Закреплённые» вверху навигации и булавка у пункта в `SectionRail.tsx`; телефон и
  палитра — по макету Р0. Запись через `shared/api/userSettings.ts` [AUTH-16.4]. Проверка —
  `npm run ui:ratchet` без роста; кнопка-булавка — `IconButton` с подсказкой `Tooltip`
- [ ] 2.3 `features/account/PinnedSectionsSection.tsx` в `ProfilePage.tsx`: список с пометками «нет доступа:
  нужно право X» / «нужен модуль X» / «раздела больше нет» и «Открепить» [AUTH-16.4] [AUTH-16.7].
  Проверка — vitest секции по трём видам строк
- [ ] 2.4 Живой прогон `e2e/pinned-checks.mjs` (в составе `settings-smoke`): закрепил раздел, вошёл
  заново с чистым браузерным хранилищем — раздел в группе «Закреплённые»; под ролью без права раздел
  виден только в профиле [AUTH-16.4]. Проверка — `MSYS_NO_PATHCONV=1 npm run test:e2e:settings` зелёный;
  с зеркалом в `localStorage` вместо сервера — красный
- [ ] 2.5 Правка оболочки — весь список живых прогонов: `cd src/client && MSYS_NO_PATHCONV=1 npm run test:e2e:ci`
  [AUTH-16]. Проверка — все прогоны зелёные
- [ ] 2.6 `docs/USER_GUIDE.md`: закрепление разделов булавкой и раздел профиля. Перестроить содержание:
  `cd docs/tools && npm run toc` [AUTH-16.4]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AccountSettings|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
