# Proposal: split-кнопка — общий компонент `SplitButton`

## Why

В редакторе шаблона «Сохранить» — split-кнопка: слева действие (Ctrl+S), справа стрелка с меню
«Сохранить как новую версию…». Своего компонента у неё нет, и обе половины собраны по месту:

- левая — нативный `<button>` с `rounded-l-full bg-primary …`;
- правая после #1693 — `IconButton`, у которого перекрыты `rounded-l-none`, `bg-primary`,
  `[--_state-color:var(--color-on-primary)]`, `disabled-container`, а цвет значка задан по условию
  `saving`.

Вид правой половины держится на порядке правил в собранном CSS: класс экрана побеждает класс
компонента не потому, что так задумано, а потому, что так легло. `sharedui-md3-10-actions` переделывает
`IconButton` (40 px, варианты, `selected`) и сломает стрелку молча — ни тип, ни храповик, ни тест этого
не увидят. Меню при этом — самодельный `absolute`-слой со своим `mousedown` на `document`.

## What Changes

- **`SplitButton`** (новый, `shared/ui/SplitButton.tsx`) — действие и стрелка с меню одним компонентом:
  - вид из макета, выбранного владельцем (три варианта — `design.md`, «Макеты»);
  - цвета и высота — из общих с `Button` констант, а не перекрытием чужих классов: правка `Button` в
    `sharedui-md3-10-actions` переходит в `SplitButton` сама;
  - меню — Radix `DropdownMenu` с порталом; пункт с подписью и пояснением второй строкой;
  - подсказки у обеих половин, имя стрелки для скринридера, `disabled` и `loading` на обе половины.
- **`EditorPanel`** — «Сохранить» и «Сохранить как новую» становятся `SplitButton`. Свой `mousedown`,
  `saveMenuRef` и `saveMenuOpen` уходят. Ctrl+S не меняется.
- **Храповик** — правило `split-button`, уровень ноль: одностороннее скругление (`rounded-l-*`,
  `rounded-r-*`, `rounded-s-*`, `rounded-e-*`) у кнопки разметки, `Button` или `IconButton` в
  `features/`. Подсказка называет `SplitButton`.
- **`docs/UI.md`** — строка `SplitButton` в таблице контролов. Если выбран вариант, которого нет в MD3, —
  запись в разделе отступлений.

Других split-кнопок в `features/` нет (поиск — `design.md`, «Контекст»).

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: split-кнопка — общий компонент, самодельную не пропускает храповик [UI-83].

## Impact

- **Клиент:**
  - новый `shared/ui/SplitButton.tsx` и его тест;
  - `shared/ui/Button.tsx` — экспорт общих констант вида (без изменения поведения `Button`);
  - `features/templates/EditorPanel.tsx`;
  - `scripts/ui-ratchet-lib.mjs` (правило), `ui-baseline.json`, тест правила.
- **Соседние change:**
  - `sharedui-md3-10-actions` — после этого change его PR 1 меняет и `SplitButton` (через общие
    константы); порядок — `design.md`;
  - `sharedui-page-menus-radix` — его задачу 1.2 (меню стрелки «Сохранить» на Radix) этот change
    поглощает, если придёт первым;
  - `sharedui-md3-13-containers` — общий `Menu`; когда он появится, слой меню `SplitButton` переходит
    на него.
- **Документы:** `docs/UI.md`, `/design-sync` (превью `SplitButton`).
- **Сервер:** не меняется.
- **PR:** один, без `version:minor` — новый вид существующего действия, без новой функциональности.
