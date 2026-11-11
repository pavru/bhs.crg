# Tasks: закрепление страниц навигации

## 1. Макет и серверный ключ

- [x] 1.1 Вписать ссылку на выбранный владельцем макет Р0 (закрепление) в `design.md` этого change
  [AUTH-16.4]. Проверка — ссылка в разделе «Макеты»
- [x] 1.2 `PinnedSectionValues.IsList` и ключи `pinnedSections`, `pinnedGroup` в `UserSettingKeys.All`:
  не больше 20 адресов, без повторов, только путь без хоста и строки запроса; `pinnedGroup` —
  `expanded | collapsed` [AUTH-16.4] [CORE-25.3]. Проверка — `AccountSettingsTests`: 21 адрес и адрес
  с хостом — 400 и набор не применён; правильный список читается обратно в том же порядке

## 2. Навигация и профиль

- [ ] 2.1 `shared/ui/pinnedSections.ts` — чистая функция «закрепления + доступ + пространство → группа
  и профиль»; строки профиля `ok`, `permission`, `module-not-granted`, `module-off`, `gone`, `unknown`
  [AUTH-16.4] [AUTH-16.3]. Проверка — vitest `pinnedSections.test.ts`: снятое право убирает страницу
  из группы и оставляет в профиле с причиной; адрес вне навигации — `gone`; вне пространства — в
  группе с пометкой; модуль не выдан и модуль выключен различаются; порядок — списка
- [ ] 2.2 Группа «Закреплённые» первой в подпанели рабочих разделов (сворачивается, `pinnedGroup`) и
  пункт «Закрепить страницу» / «Открепить страницу» в «⋮» пункта подпанели; значок `keep` в
  `shared/ui/icons`; на телефоне группа — в меню страниц заголовка; палитра — группа «Закреплённые»
  при пустом запросе, только переход. Запись через `shared/api/userSettings.ts` [AUTH-16.4].
  Проверка — vitest группы и меню; `npm run ui:ratchet` без роста; кнопка «⋮» — `RowActionsMenu`
- [ ] 2.3 `features/account/PinnedSectionsSection.tsx` в `ProfilePage.tsx`: «Закреплённые страницы N из
  20», шесть видов строки, перетаскивание и «Выше» / «Ниже», «Открепить», пустое состояние
  [AUTH-16.4] [AUTH-16.7]. Проверка — vitest секции по видам строк и порядку
- [ ] 2.4 Живой прогон `e2e/pinned-checks.mjs` (в составе `settings-smoke`): закрепил страницу, вошёл
  заново с чистым браузерным хранилищем — страница в группе «Закреплённые»; под ролью без права
  страница видна только в профиле [AUTH-16.4]. Проверка — `MSYS_NO_PATHCONV=1 npm run test:e2e:settings`
  зелёный; с зеркалом в `localStorage` вместо сервера — красный
- [ ] 2.5 Правка оболочки — весь список живых прогонов: `cd src/client && MSYS_NO_PATHCONV=1 npm run test:e2e:ci`
  [AUTH-16]. Проверка — все прогоны зелёные
- [ ] 2.6 `docs/USER_GUIDE.md`: закрепление страниц и раздел профиля. Перестроить содержание:
  `cd docs/tools && npm run toc` [AUTH-16.4]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AccountSettings|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
