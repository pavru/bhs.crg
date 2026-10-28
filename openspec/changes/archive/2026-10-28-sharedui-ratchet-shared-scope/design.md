# Design: храповик интерфейса смотрит `shared/`, кроме `shared/ui`

## Context

- `src/client/scripts/ui-ratchet-lib.mjs`: у правила `dirs` — каталоги `src/`, которые оно смотрит;
  без `dirs` — только `features` (`dirsOf`). `white-on-fill` и `portal-container` — `['features',
  'shared']` (вместе с `shared/ui`), `native-date` — `everywhere` (весь `src/`). `ui-ratchet.mjs`
  обходит каталог целиком; исключать подкаталог он не умеет.
- `shared/filter` — отбор всех реестров: `ConditionEditor` (строка диалога `RowFilterDialog` и поповер
  чипа `FilterChips`), `RowFilterValue` (поле значения). `Select` (`shared/ui/Select.tsx`) — Radix,
  с `size` `md` | `sm` | `inline`, `emptyItem`, `SelectItem disabled`, `stayOpenOn`.

### Что выяснилось сверкой (22.10.2026)

Прогон правил храповика по `shared/` без `shared/ui` (их счётом, вне репозитория):

| Правило | Где | Мест |
|---|---|---|
| `select` | `filter/ConditionEditor.tsx` (2), `filter/RowFilterValue.tsx:49` (1) | 3 |
| `fixed-width` | `filter/ConditionEditor.tsx` — ширины в `style` (4); `hooks/useMedia.ts:9` — ложное | 5 |
| `icon-button` | `filter/FilterChips.tsx` (1), `filter/RowFilterValue.tsx` (2), `siteFilter/SiteFilterNotes.tsx` (1) | 4 |
| `chip` | `siteFilter/SiteFilterNotes.tsx` | 1 |

- Строки нативных `<select>` из issue сдвинулись: `ConditionEditor.tsx:33` и `:62` на месте,
  `RowFilterValue.tsx` — `:49` (было `:43`).
- `native-title`, `native-checkbox`, `table`, `text-size`, `palette`, `h1`, `badge`, `tabs` в новой
  области — ноль. Флажки `shared/` переведены ещё в #1420.
- **`useMedia.ts:9` — ложное срабатывание `fixed-width`:** образец `\b(?:minWidth|maxWidth|width)\s*:`
  ловит `width: 768` внутри `'(min-width: 768px)'` — `\b` стоит и после дефиса. В `features/` таких
  строк нет (проверено поиском), поэтому сужение образца уровня `features/` не меняет.

## Goals / Non-Goals

**Goals:** граница храповика — `shared/ui`, а не `shared/`; в новой области — ноль мест.

**Non-Goals:**
- `shared/ui` под общими правилами — там компоненты рисуют то, что правила запрещают экранам;
- `App.tsx`, `main.tsx` и прочий корень `src/` — их смотрит только `native-date`; общие правила там
  ничего не находят, расширять незачем.

## Decisions

- **D1. Область — по пути файла, а не по каталогу обхода.** Правило без `dirs` получает область
  «`features/**` и `shared/**`, кроме `shared/ui/**`»; `watches(rule, file)` решает по пути. Каталог
  обхода у всех один (`features`, `shared`, плюс корень для `everywhere`). Альтернатива — перечислить
  каталоги `shared/` поимённо — отклонена: новый `shared/<x>` выпал бы из надзора молча, ровно то, от
  чего `dirsToScan` защищает `everywhere`.
- **D2. Правила со своей областью не меняются.** `white-on-fill` и `portal-container` смотрят и
  `shared/ui` (кнопка, календарь, меню — первые, кто ошибается); `native-date` — весь `src/`.
- **D3. `fixed-width` — без медиазапросов.** Образец стиля не считает свойство сразу после `(` с
  `min-`/`max-`: `(min-width: 768px)` — условие медиазапроса, а `'min-width: 520px'` строкой CSS — по-прежнему
  ширина (ревью PR #1672: сужение по одному дефису пропускало и её). Уровень `features/` не меняется
  (проверить `--update` на чистом дереве — дифф `ui-baseline.json` пуст до переводов).
- **D4. Переводы — здесь же, их мало (13 мест в 4 файлах).**
  - `ConditionEditor`: колонка и оператор → `Select size="sm"`; «— колонка —» — `emptyItem`; колонка
    без доступа — `SelectItem disabled` с причиной в подписи; ширины `style` → классы шкалы
    (`w-30`/`w-36`/`max-w-40`) в строке диалога, `w-full` столбиком в поповере.
  - `RowFilterValue`: «да / нет» и выбор из списка → `Select size="sm"`; `onBlur` нативного поля
    заменяет закрытие списка (`onOpenChange(false)`) — фиксация значения в чипе по уходу из поля
    сохраняется.
  - Звезда избранного `SiteFilterNotes` → `IconButton size="sm"` с именем: она и «кнопка-иконка», и
    «чип» по `aria-pressed` — одно место, два правила.
  - Крестики «снять» внутри пилюль (`FilterChips` — чип условия, `RowFilterValue` — жетоны значений) →
    `Chip` с `onRemove`, а не `IconButton`: кнопка-иконка — 32 px, и пилюля в ~24 px выросла бы на
    треть. Крестик снятия — работа `Chip` (`shared/ui`). Решено владельцем при реализации, 28.10.2026.
    Чип условия — `menu` и в цвете бренда, пока условие годно («отбор стоит»); пометка UI-56 — с
    заливкой `bg-warning-subtle!` поверх тона. Жетон значения — `Chip removeOnly` (новый проп): нажать в
    нём нечего, текст — подпись, а не кнопка. Подсказка правила `icon-button` называет этот выход.
  - Поле текста рядом с `Select` — той же коробкой (`fieldClass.ts`: h-7, рамка, кольцо фокуса).
  - Пустое слово перечня пунктом не рисуется (Radix не берёт пустое значение пункта); служебное
    значение «не выбрано» у `Select` — с управляющим символом, чтобы не совпасть со словом данных.
  - `Select` получает `onOpenChange` — закрытие списка фиксирует значение в списке условий вместо
    `onBlur` нативного поля.
- **D5. Поповер чипа и `Select` в нём.** Оба — Radix с порталом в body (`docs/UI.md`, «Слои над
  окном»): щелчок по пункту списка лежит вне поповера в DOM, но внутри дерева React, и поповер его
  промахом не считает. Esc закрывает верхний слой — список. Проверка — живой прогон `table-chips` и
  тест `FilterChips` (выбор колонки в поповере не закрывает поповер).
- **D6. Макетов нет:** перевод на готовые компоненты и правило храповика (`docs/UI.md`, «Новый экран:
  порядок»). Разбор с Архитектором — по правилу для нового правила храповика: граница области в
  `design.md` (D1) и есть предмет разбора, отметка — в PR.
- **Сторож.** `ui-ratchet.test.mjs`: файл `shared/filter/x.tsx` с `<select>` считается правилом
  `select`; `shared/ui/x.tsx` — нет; `shared/ui/x.tsx` с белым на заливке — считается `white-on-fill`;
  строка `'(min-width: 768px)'` — не `fixed-width`.
- **Чем ломается.** Поповер чипа закрывается на выборе в `Select` — `table-chips` красный. `onBlur`
  потерян — значение условия в чипе не фиксируется уходом из поля (тест `RowFilterValue`). Правило
  без `dirs` перестало видеть `features/` — тест обхода красный.

## Risks / Trade-offs

- [Радикс-`Select` внутри Радикс-`Popover`: два слоя с порталом, две копии `DismissableLayer`] →
  единственная копия слоёв Radix (`radix-single-copy.test.mjs`) держит порядок; `stayOpenOn` не нужен.
- [Колонок в источнике сотни — длинный список `Select` без поиска] → как и у нативного select сегодня;
  поиск в списке — отдельная задача, если о ней попросят.
- [Уровень `shared/` сначала растёт] → в этом же change он опускается до нуля; промежуточный
  `--update` не коммитится.
