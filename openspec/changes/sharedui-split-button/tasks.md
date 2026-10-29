# Tasks: split-кнопка — общий компонент `SplitButton`

Один PR после выбора варианта. Без `version:minor`: новый вид существующего действия, функциональности
не прибавляется.

## 0. До кода

- [x] 0.1 Три макета в Claude Design — варианты А, Б, В из `design.md`, «Макеты»: панель редактора
  шаблона, активная и историческая версия, меню открыто, идёт сохранение, светлая и тёмная темы.
  Разбор вместо Дизайнера и Архитектора — `design.md`, «Разбор». Проверка — ссылка на проект Claude
  Design в `design.md` и в описании PR планирования.
- [ ] 0.2 Выбор владельца (pavru) записан в `design.md` с датой и перенесён в [UI-83] через
  `/opsx:update sharedui-split-button`; при варианте В переименованы компонент и правило. Проверка —
  `openspec validate sharedui-split-button --strict`, в `design.md` назван выбранный вариант.
- [ ] 0.3 #1693 (`sharedui-icon-button-screens` PR 2/2) слит, ветка обновлена от master. Проверка — в
  `EditorPanel.tsx` стрелка «Сохранить» — `IconButton`, уровень `icon-button` в `ui-baseline.json` — 0.
- [ ] 0.4 Сверка с соседями: слит ли `sharedui-page-menus-radix` (меню стрелки уже Radix?) и
  `sharedui-md3-10-actions` PR 1 (`Button` 40 без `size`?). Ответы — строкой в описании PR, порядок —
  `design.md`, D6.

## 1. Компонент

- [ ] 1.1 `Button.tsx` экспортирует общие константы вида (база и `variantClass`); `Button` и `IconButton`
  не меняются. Проверка — `npm test -- Button` зелёный, `git diff` `Button.tsx` — только `export`.
- [ ] 1.2 `shared/ui/SplitButton.tsx` по выбранному варианту (D1–D4): основная часть и стрелка из общих
  констант, меню — Radix `DropdownMenu` с порталом, пункт с `description`, `Tooltip name` у стрелки,
  `disabled` и `loading` на обе части [UI-83]. Проверка — `SplitButton.test.tsx`:
  - нажатие основной части зовёт `onClick`, меню закрыто;
  - стрелка — `aria-haspopup="menu"`, `aria-expanded`, имя по `menuLabel`;
  - Enter на стрелке открывает меню, фокус на пункте, выбор зовёт `onSelect`, Esc возвращает фокус;
  - при `loading` обе части недоступны, меню не открывается;
  - у обеих частей одинаковые классы вида варианта (тест читает их из констант, а не строкой).
- [ ] 1.3 `docs/UI.md`: строка `SplitButton` в таблице контролов; при варианте Б — запись в разделе
  отступлений от MD3. Проверка — `cd docs/tools && npm run toc` без изменений или с закоммиченными.

## 2. Экран

- [ ] 2.1 `EditorPanel.tsx`: «Сохранить» и «Сохранить как новую» — `SplitButton`; удалены `saveMenuOpen`,
  `saveMenuRef`, свой `mousedown`, самодельный слой меню; Ctrl+S не тронут [UI-83]. Проверка — тест
  `EditorPanel` (или живой прогон редактора шаблонов): пункт меню открывает окно новой версии, Ctrl+S
  при открытом меню не открывает «Сохранить страницу»; скриншоты светлой и тёмной темы — в описании PR.
- [ ] 2.2 Уровень `manual-dropdown` (если правило уже слито из #1537) — `EditorPanel` в нём на одно место
  меньше. Проверка — `npm run ui:ratchet` без роста.

## 3. Храповик

- [ ] 3.1 Правило `split-button` (D5): `countElements` с опцией для тегов-компонентов, предикат
  одностороннего скругления у `button`, `a`, `Button`, `IconButton`; уровень ноль; подсказка называет
  `SplitButton` [UI-83]. Проверка — `ui-ratchet.test.mjs`: `<button className="rounded-l-full">` и
  `<IconButton className="rounded-l-none">` считаются, `<div className="rounded-r-md">` — нет;
  `npm run ui:ratchet` зелёный, раздел `split-button` пуст.
- [ ] 3.2 `docs/UI.md`, «Кто сторожит» — правило `split-button`, уровень ноль. Проверка — строка на месте.

## 4. Синхронизация дизайна

- [ ] 4.1 `/design-sync`: превью `SplitButton` в `.design-sync/previews` и README дизайн-системы.
  Проверка — превью открывается в Claude Design.

## 5. Верификация

- [ ] 5.1 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.2 `cd src/client && npm test` — зелёный
- [ ] 5.3 `cd src/client && npm run lint:ratchet` и `npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный (`SplitButton.tsx` и `ui-ratchet-lib.mjs` ниже 500 строк кода)

## Workflow follow-up

- После слияния — `/opsx:archive sharedui-split-button`: требования [UI-83] уходят в `specs/sharedui`.
- В описании PR `sharedui-page-menus-radix` — что его задача 1.2 закрыта здесь (если этот change пришёл
  первым).
