# Tasks: split-кнопка — общий компонент `SplitButton`

Один PR. Без `version:minor`: новый вид существующего действия, функциональности
не прибавляется.

## 0. До кода

- [x] 0.1 Три макета в Claude Design — варианты А, Б, В из `design.md`, «Макеты»: панель редактора
  шаблона, активная и историческая версия, меню открыто, идёт сохранение, светлая и тёмная темы.
  Разбор вместо Дизайнера и Архитектора — `design.md`, «Разбор». Проверка — ссылка на проект Claude
  Design в `design.md` и в описании PR планирования.
- [x] 0.2 Выбор владельца (pavru) — вариант А, 30.10.2026 — записан в `design.md` («Макеты», D0) и
  перенесён в [UI-83] через `/opsx:update sharedui-split-button`. Проверка —
  `openspec validate sharedui-split-button --strict`, в `design.md` назван выбранный вариант.
- [x] 0.3 #1693 (`sharedui-icon-button-screens` PR 2/2) слит, ветка обновлена от master. Проверка — в
  `EditorPanel.tsx` стрелка «Сохранить» — `IconButton`, уровень `icon-button` в `ui-baseline.json` — 0.
- [x] 0.4 Сверка с соседями: слит ли `sharedui-page-menus-radix` (меню стрелки уже Radix?) и
  `sharedui-md3-10-actions` PR 1 (`Button` 40 без `size`?). Ответы — строкой в описании PR, порядок —
  `design.md`, D6.

## 1. Компонент

- [x] 1.1 `Button.tsx` (D1): `rounded-full` уходит из `BASE` в `Button`, тень filled — из `variantClass`
  в отдельную константу, её добавляет только `Button`; `BASE`, `variantClass`, `SIZE` экспортируются —
  из `shared/ui/buttonClasses.ts` (D1: функция из файла компонентов — ошибка `only-export-components`).
  Вид `Button` и `IconButton` не меняется. Проверка — тест `Button`: набор классов кнопки каждого
  варианта (как множество) тот же, что до правки; `npm test -- Button` зелёный.
- [x] 1.2 `shared/ui/SplitButton.tsx` — вариант А (D0–D2): основная часть и стрелка из общих
  констант без своих полей и роли текста; зазор 2 px, внутренние углы `xs` (нажатая — `sm`), у
  открытого меню стрелка `data-[state=open]:rounded-l-full` и знак `group-data-[state=open]:rotate-180`; без тени; `variant`
  по умолчанию `filled`; меню — Radix `DropdownMenu` с порталом, пункт с `description`, `Tooltip name` у
  стрелки, `disabled` и `loading` на обе части [UI-83]. Проверка — `SplitButton.test.tsx`:
  - нажатие основной части зовёт `onClick`, меню закрыто;
  - без `variant` — классы `variantClass('filled')`;
  - ни у одной половины нет `rounded-full` вместе с односторонним углом и нет класса тени;
  - у открытого меню у стрелки `data-state="open"` (на нём держатся круглая форма и поворот);
  - стрелка — `aria-haspopup="menu"`, `aria-expanded`, имя по `menuLabel`;
  - Enter на стрелке открывает меню, фокус на пункте, выбор зовёт `onSelect`, Esc возвращает фокус;
  - при `loading` обе части недоступны, щелчок и Enter на стрелке меню не открывают; у основной части
    `aria-disabled` и подсказка `title`;
  - пустой `items` — стрелки нет;
  - у обеих частей одинаковые классы вида варианта (тест читает их из констант, а не строкой).
- [x] 1.3 `docs/UI.md`: строка `SplitButton` в таблице контролов — форма split button M3 Expressive,
  когда ставится (основное действие на одно нажатие и меню вариантов того же действия), чем не
  является (не `IconButton` рядом с `Button`; кнопка, которая только открывает меню, — не split; нет
  вариантов — обычная `Button`). Проверка — `cd docs/tools && npm run toc` без изменений или с закоммиченными.

## 2. Экран

- [x] 2.1 `EditorPanel.tsx` (D2a): у активной версии «Сохранить» — `SplitButton` с пунктом «Сохранить
  как новую версию…»; у исторической — filled `Button` «Сохранить как новую» без стрелки; удалены
  `saveMenuOpen`, `saveMenuRef`, свой `mousedown`, самодельный слой меню; Ctrl+S не тронут [UI-83].
  Проверка — тест `EditorPanel`:
  - пункт меню открывает окно новой версии;
  - у исторической версии кнопки-стрелки «Ещё варианты сохранения» нет;
  - `keydown` Ctrl+S (`code: 'KeyS'`) при открытом меню — у события `defaultPrevented`, сохранение не
    вызвано (фокус не в Monaco).

  Скриншоты светлой и тёмной темы — в описании PR, с отметкой про стрелку у исторической версии
  (уточнение к макету А, D2a).
- [x] 2.2 Уровень `manual-dropdown` (если правило уже слито из #1537) — `EditorPanel` в нём на одно место
  меньше. Проверка — `npm run ui:ratchet` без роста. На 30.10.2026 правила нет (#1537 не слит) — править
  нечего; придёт позже — самодельного слоя в `EditorPanel` уже нет.

## 3. Храповик

- [x] 3.1 Правило `split-button` (D5): `countElements` с опцией для тегов-компонентов, предикат
  одностороннего скругления у `button`, `a`, `Button`, `IconButton`; уровень ноль; подсказка называет
  `SplitButton` [UI-83]. Проверка — `ui-ratchet.test.mjs`: `<button className="rounded-l-full">` и
  `<IconButton className="rounded-l-none">` считаются, `<div className="rounded-r-md">` — нет;
  `npm run ui:ratchet` зелёный, раздел `split-button` пуст.
- [x] 3.2 `docs/UI.md`, «Кто сторожит» — правило `split-button`, уровень ноль. Проверка — строка на месте.

## 4. Синхронизация дизайна

- [ ] 4.1 `/design-sync`: превью `SplitButton` в `.design-sync/previews` и README дизайн-системы.
  Проверка — превью открывается в Claude Design.

## 5. Верификация

- [x] 5.1 `cd src/client && npx tsc -b` — без ошибок
- [x] 5.2 `cd src/client && npm test` — зелёный
- [x] 5.3 `cd src/client && npm run lint:ratchet` и `npm run ui:ratchet` — без роста
- [x] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный (`SplitButton.tsx` и `ui-ratchet-lib.mjs` ниже 500 строк кода; предикаты кнопок вынесены в
  `ui-ratchet-buttons.mjs` — D5)

## Workflow follow-up

- После слияния — `/opsx:archive sharedui-split-button`: требования [UI-83] уходят в `specs/sharedui`.
- В описании PR `sharedui-page-menus-radix` — что его задача 1.2 закрыта здесь (если этот change пришёл
  первым).
