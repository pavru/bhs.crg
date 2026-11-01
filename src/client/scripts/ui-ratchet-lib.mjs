// Счёт и сравнение для храповика интерфейса (issue #1349). Чистые функции: файловая система и
// процесс — в ui-ratchet.mjs, здесь только то, что проверяет тест.

import { posix } from 'node:path';
import ts from 'typescript';
import { containerTextHits, stripComments } from './class-scan.mjs';
import { isBadge, isChip, isSegmentRow } from './ui-ratchet-pills.mjs';
import { isNoticePlaque } from './ui-ratchet-plaque.mjs';
import { buttonRules } from './ui-ratchet-buttons.mjs';
import { inputRules } from './ui-ratchet-inputs.mjs';
import { isManualDropdown, portalContainersIn } from './ui-ratchet-layers.mjs';
import { countDarkVariants, countLegacyClasses, countLegacyRadii, countLegacyShadows, countLegacyType, countRawStates, LEGACY_HINT } from './ui-ratchet-md3.mjs';

const COLORS = 'slate|gray|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose';
const UTILITIES = 'bg|text|border|ring|ring-offset|outline|divide|fill|stroke|from|via|to|shadow|accent|caret|decoration|placeholder';

/** Каталог файлов из корня src/ (App.tsx, main.tsx) — для `countViolations({ dir })` и `dirsToScan`. */
export const ROOT = '.';

/**
 * Шкала размеров значка ([UI-60]) — копия `ICON_SIZES` из src/shared/ui/icons/types.ts: TypeScript
 * отсюда не прочесть. Расхождение ловит ui-ratchet.test.mjs.
 */
export const ICON_SCALE = [18, 20, 24, 40, 48];

/** Импорт прежнего набора или пакета Material Symbols — см. правило `lucide-import`. */
const ICON_PACKAGE_IMPORT = /\bfrom\s+['"](?:lucide-react|@material-symbols\/)[^'"]*['"]|\bimport\(\s*['"](?:lucide-react|@material-symbols\/)/g;

/**
 * Классы нарушений. У каждого — куда идти вместо: сообщение отказа обязано называть выход, иначе
 * его обходят первым попавшимся способом. `dirs` — каталоги src/, которые правило смотрит; без него —
 * область по умолчанию (`DEFAULT_SCOPE`): features/ и shared/, кроме shared/ui/ — там живут сами
 * DataGrid и Select, которые эти элементы и заворачивают (#1469).
 * `skip` — префиксы путей от src/, которые правило не смотрит внутри своих каталогов: законное место
 * названо путём, а не держится в уровне — уровень с единицей молча пропустил бы второе нарушение там же.
 */
export const RULES = [
  {
    id: 'table',
    pattern: /<table\b/g,
    instead: 'данные — DataGrid (shared/ui/DataGrid: cell, width, wrap, density="compact"); черновик строк — EditGrid (shared/ui/EditGrid: cell, add, onRemove, total); «поле — значение» и форма — раскладка (dl, grid), а не таблица (docs/UI.md, «Таблицы», #1354)',
  },
  {
    id: 'select',
    pattern: /<select\b/g,
    instead: 'выбор из списка — Select (shared/ui/Select): label — поле формы; без подписи — variant="chip" в отборе (с emptyItem «все»), density="dense" в строке настроек и шапке колонки, variant="inline" внутри фразы; emptyItem для «не выбрано» (#1353, [UI-69])',
  },
  {
    id: 'h1',
    // Свой заголовок страницы. Законные — карточки входа (features/auth, LoginPage): это карточка
    // посреди пустого экрана, а не страница с шапкой, и в уровне они стоят навсегда.
    pattern: /<h1\b/g,
    instead: 'заголовок страницы — PageHeader (shared/ui/PageHeader), в list-detail — ListDetailShell: docs/UI.md, «Заголовки»',
  },
  {
    id: 'text-size',
    // Произвольный размер текста: text-[11px], text-[0.7rem]. Цвет вида text-[#fff] ловит palette.
    pattern: /\btext-\[\d+(?:\.\d+)?(?:px|rem|em)\]/g,
    instead: 'размер текста — ролью MD3 (text-body-small, text-body-medium, text-label-small, text-title-small…): docs/UI.md, «Текст»',
  },
  {
    id: 'palette',
    // Сырая палитра Tailwind (bg-indigo-50, hover:text-purple-700/80) и hex в произвольном значении.
    pattern: new RegExp(`(?<![\\w-])(?:${UTILITIES})-(?:${COLORS})-\\d{2,3}\\b|-\\[#[0-9a-fA-F]{3,8}\\]`, 'g'),
    instead: 'цвет — только токеном (text-primary, bg-warning-container, text-tertiary…): docs/UI.md, «Цвет»',
  },
  {
    id: 'fixed-width',
    // Своя ширина (issue #1382): w-[…], min-w-[…], max-w-[…] и числовая ширина в style. Обёртка тела
    // с min-w-[520px] подпирает окно изнутри, и размер выходит не тем, что назван у Modal. По всему
    // features/, а не только в файлах с <Modal: тело окна обычно живёт в своём файле (форма,
    // редактор сопоставления), и сторож по файлу с окном его не видел бы. Различить обёртку и
    // колонку по тексту нельзя — законные (колонка сопоставления, поле поиска) стоят в уровне.
    // Доля (max-w-[45%]) — обрезка подписи в строке: ничего не подпирает и не считается.
    // Условие медиазапроса — `(min-width: 768px)` в скобках — не ширина элемента (#1469); та же запись
    // строкой CSS (`style.cssText = 'min-width: 520px'`) — ширина, и считается.
    pattern: /(?<![\w-])(?:min-|max-)?w-\[[^\]\s%]+\]|(?<!\(\s*(?:min-|max-))\b(?:minWidth|maxWidth|width)\s*:\s*['"]?\d/g,
    instead: 'размер окна — по содержимому: обычное, wide или fullScreen (docs/UI.md, «Окна»); ширину колонки — width у DataGrid / EditGrid классом шкалы (w-36); раскладку экрана задают каркасы (docs/UI.md, «Узкий экран»)',
  },
  {
    id: 'native-title',
    // Браузерная подсказка у элемента разметки (issue #1394): серая, с задержкой в секунду, к фокусу
    // глуха. Текстом её не поймать — `title` носят и наши компоненты (окно, заголовок страницы), и
    // атрибут бывает в любой строке многострочного тега; поэтому — по синтаксическому дереву.
    // Законна одна: у обрезанной строки (truncate, line-clamp) — её же полный текст, когда подсказка
    // совпадает с содержимым. Фреймов в разметке нет вовсе (native-pdf-frame, [UI-26]).
    count: countNativeTitles,
    instead: 'подсказка — Tooltip (shared/ui/Tooltip) или проп title у Button / IconButton: при наведении и фокусе, в нашем виде; у кнопки из одной иконки — <Tooltip name>. Нативный title — только полный текст обрезанной строки (docs/UI.md, «Подсказка»)',
  },
  {
    id: 'native-checkbox',
    // Нативный флажок в обход Checkbox (issue #1414): у каждого свой размер, цвет и отступ, а у
    // голого — серый браузерный вид. По синтаксическому дереву, как native-title: `type` стоит в
    // любой строке многострочного тега, а текстом `<input … type="checkbox"` не поймать — перед
    // ним бывают обработчики со стрелкой `=>`. Накопленные стоят в уровне до перевода экранов
    // (перечень — в #1414). Законных исключений нет: Checkbox и есть нативный `<input>` — он
    // годится и в ячейку таблицы (18 px, след слоя не раздвигает), и под Tooltip (передаёт ref), и флажку только для
    // чтения (aria-readonly и onClick уходят во флажок как есть).
    count: countNativeCheckboxes,
    instead: 'флажок — Checkbox (shared/ui/Checkbox): label и description — подпись и пояснение, размер один (18 px, и в строках таблиц), indeterminate — «выбраны не все»; внутри своей разметки строки — Checkbox без label в обёртке <label> или с aria-label (docs/UI.md, таблица контролов)',
  },
  // Поля и выбор — native-radio, raw-field (ui-ratchet-inputs.mjs).
  ...inputRules(countElements),
  {
    id: 'chip',
    // Свой чип (#1416): нажимаемая пилюля — rounded-full или rounded-sm с полями по бокам (px-…) или aria-pressed у
    // нативного элемента. Компоненты (Button, Chip) рисуют пилюлю сами и не считаются: правило
    // смотрит только элементы разметки.
    count: text => countElements(text, isChip),
    instead: 'чип — Chip (shared/ui/Chip): filter — selected (с menu — выбор из меню), input — onRemove с removeLabel, assist — icon без selected; о состоянии говорит знак Icon с iconTone, а не тон; выбор одного значения из 2–5 — SegmentedButton (shared/ui/SegmentedButton); переключатель панели — Tabs variant="segmented" (docs/UI.md, таблица контролов)',
  },
  {
    id: 'segmented',
    // Свой ряд сегментов ([UI-68]): группа в рамке со скруглением и кнопками внутри — так прошли мимо
    // `chip` и `tabs` ThemeToggle и VariantPicker. Вид сегментов — один (segmentedClasses.ts), и его
    // держат два компонента; смотрит и shared/ui, где жил ThemeToggle.
    count: text => countElements(text, isSegmentRow),
    dirs: ['features', 'shared'],
    instead: 'ряд сегментов — SegmentedButton (shared/ui/SegmentedButton: items, value, onChange — радиогруппа, стрелка выбирает); переключатель панели — Tabs variant="segmented" с TabPanel (docs/UI.md, таблица контролов)',
  },
  {
    id: 'badge',
    // Своя пометка состояния (#1416): пилюля без нажатия — span или div с rounded-full и фоном или рамкой.
    // Круги заданного размера (аватар, точка, спиннер, полоса загрузки) — не пометки: у них w-/h-/size-.
    count: text => countElements(text, isBadge),
    instead: 'пометка состояния — StatusText (shared/ui/StatusText): текст роли со знаком, tone по смыслу — neutral, primary, success, warning, error, quiet; icon — свой знак, когда смыслы одного тона рядом; число у подписи — CountText (shared/ui/CountText), на знаке и вкладке — Badge (shared/ui/Badge) (docs/UI.md, «Язык формы»)',
  },
  {
    id: 'notice-plaque',
    // Плашка-сообщение руками ([UI-73], sharedui-md3-12): div, p или section с заливкой контейнера роли,
    // рамкой и скруглением — у каждого экрана своя. Пометка-пилюля (span, text-label-*), подложка с
    // прозрачностью и пунктирное место для перетаскивания — не плашки. Весь src/: в shared/ui плашка была
    // своя (RereadFailedNotice). Переводятся в 17 — уровень только опускается.
    count: text => countElements(text, isNoticePlaque),
    everywhere: true,
    instead: 'сообщение области — Banner (shared/ui/Banner: tone — error, warning, info, success; actions — до двух text-действий), во всю ширину в начале страницы, панели или окна; про одно поле — текст под полем (TextField hint, error); результат действия — snackbar (docs/UI.md, «что сказать — чем»)',
  },
  // Кнопки — icon-button, button-size, dense-scope, split-button (ui-ratchet-buttons.mjs).
  ...buttonRules(countElements),
  {
    id: 'tabs',
    // Свои вкладки по ARIA (#1416): role="tab" / "tablist" / "tabpanel" у элемента разметки. Их клавиатуру
    // (стрелки, Home/End, ручная активация) Tabs уже делает — второй раз её пишут с ошибками.
    count: text => countElements(text, isTabRole),
    instead: 'вкладки — Tabs + TabPanel (shared/ui/Tabs): underline для разделов, segmented для режима; стрелки и ручную активацию компонент делает сам (docs/UI.md, «Язык формы»)',
  },
  {
    id: 'white-on-fill',
    // Белый текст, кольцо или точка на сплошной заливке роли (#1462). В тёмной теме заливки светлые
    // (brand — #a7c8ff), и белое на них — 1,7:1: подписи не прочесть. Пара к заливке — text-on-*.
    // Смотрит className ЦЕЛИКОМ, со всеми ветвями условий: `${on ? 'bg-primary text-white' : …}` и
    // `bg-primary ${x ? 'text-white' : ''}` — одно нарушение. И строки вне className (карта вариантов
    // кнопки, класс в переменной) — каждую отдельно. Белый с прозрачностью у фона (hover:bg-white/10)
    // — слой состояния, не цвет, и не считается. В отличие от прочих правил смотрит и shared/: кнопка
    // и календарь — первые, кто рисует заливку, и пропусти он их — ошибку размножили бы экраны.
    // ⚠️ Не видит пары, разнесённой по РАЗНЫМ элементам или строкам: заливка у кнопки, белая точка или
    // ползунок у ребёнка (так был пропущен Switch, ревью #1477), заливка через style. Её ловит ревью.
    count: countWhiteOnFill,
    dirs: ['features', 'shared'],
    instead: 'текст и значок на заливке — парная роль: bg-primary → text-on-primary, bg-error → text-on-error, bg-success → text-on-success, bg-warning → text-on-warning; кольцо — ring-on-*, точка — bg-on-* (docs/UI.md, «Цвет»)',
  },
  {
    id: 'portal-container',
    // Всплывающий слой, портированный в свой контейнер (#1521). Так календарь даты клали ВНУТРЬ окна
    // (#338) — и `overflow-hidden` с `transform` окна резали его по краю. Слой над окном держит дерево
    // React и единственная копия слоёв Radix (radix-single-copy.test), а не место в DOM. Смотрит и
    // shared/: там живут сами поля и меню, с которых ошибка расходится по экранам. По синтаксическому
    // дереву, как native-title: `container` стоит в любой строке тега, а перед ним бывают стрелки `=>`
    // и сравнения; `data-container` — не он. И `createPortal` из react-dom — тот же портал без Radix.
    // Законные — WindowStep и кнопки подвала с ролью (ModalRoles, во весь экран — в шапку): рисуют в места
    // СВОЕГО окна, а не слой над ним; оба стоят в уровне.
    count: countPortalContainers,
    dirs: ['features', 'shared'],
    instead: 'всплывающий слой (календарь, Select, меню, подсказка) — портал в body без container: над окном и затемнением, краем окна не обрезается; фокус и Esc внутри окна держит дерево React (docs/UI.md, «Слои над окном»)',
  },
  {
    id: 'manual-dropdown',
    // Ручной выпадающий список (#1537): `{xxxOpen && <div className="absolute …">}` без портала. Его режет
    // любой контейнер с overflow (окно — ещё и transform), а Esc закрывает не список, а окно под ним.
    // По синтаксическому дереву: элемент с `absolute` — правая часть `&&`, левая — `open` или `…Open`
    // (ui-ratchet-layers.mjs). Смотрит и shared/: меню и поля там — первые, с кого ошибка расходится.
    // ⚠️ Не видит список, открытый условием с другим именем (`showList &&`) или тернарником, и слой
    // во фрагменте или компоненте. Такие ловит ревью.
    count: text => countElements(text, isManualDropdown),
    dirs: ['features', 'shared'],
    instead: 'выпадающий список — Radix Popover (если в нём поле ввода: фильтр, поиск) или DropdownMenu (пункты-действия), портал в body без container; Esc и щелчок мимо закрывают слой, а не окно (docs/UI.md, «Слои над окном»)',
  },
  {
    id: 'native-date',
    // Нативное поле даты, месяца или времени (#1459, эпик #1458): формат у него — по языку ОС, а не
    // пользователя (на Windows с английской локалью — MM/DD/YYYY и AM/PM), вид — браузерный. По
    // синтаксическому дереву: `type` бывает выражением (`type={c ? 'date' : 'text'}` — так в отборе
    // реестров), и считается литерал, которым выражение может СТАТЬ (ветвь условия, правая часть
    // `&&`), а не литерал из сравнения в условии. Тег — `<input>` и `TextField` (с именем при импорте):
    // он отдаёт `type` полю как есть. Смотрит ВЕСЬ src/: законных мест нет и в shared/ — DateInput сам
    // рисует `type="text"`. ⚠️ Не видит `type`, пришедший переменной или разворотом `{...props}`, и
    // обёртку, которая отдаёт `type` полю под своим именем.
    count: countNativeDates,
    everywhere: true,
    instead: 'дата — DateField (форма) / DateInput compact (ячейка) / DateInput density="dense" (отбор и строка настроек); период — DateRange; месяц и год — тот же компонент с precision="month" | "year"; время суток ЧЧ:ММ — TimeField (форма) / TimeInput (docs/UI.md, «Дата и время»)',
  },
  {
    id: 'date-frame',
    // Своя рамка у поля даты (#1464): `<DateInput className="border …">`. Рамку, высоту и кольцо
    // фокуса рисует сам компонент по `density`, как Select, — свои рамки экранов разъезжались по высоте,
    // радиусу и цвету ошибки. Считается класс `border` и `border-*` без модификатора, с `!` тоже: пометки
    // поверх рамки компонента больше нет — переопределённое значение FieldBuilder называет словами ([UI-69]).
    count: countDateFrames,
    instead: 'рамку поля даты рисует DateInput density="dense" — плотное поле 40, как у Select ([UI-69]); ошибка — invalid, только чтение — readOnly; className — ширина и место в раскладке (docs/UI.md, «Дата и время»)',
  },
  {
    id: 'native-pdf-frame',
    // Фрейм вместо своего просмотрщика (issue #1490, [UI-23]): встроенный просмотрщик браузера во
    // фрейме у каждого браузера свой, на телефоне его нет вовсе, а текст из него в форму не скопировать
    // по-нашему. По синтаксическому дереву: `<iframe`, `<embed`, `<object` в разметке и создание их через
    // `createElement` (строкой, в любых кавычках). Смотрит и shared/ с уровнем 0 ([UI-26]): кнопки и
    // окна shared/ui — первые, кто мог бы завести фрейм. Законный — только скрытый фрейм печати самого
    // просмотрщика (`shared/ui/viewer/printPdf.ts`), и он назван путём (`skip`).
    count: countNativeFrames,
    dirs: ['features', 'shared'],
    skip: ['shared/ui/viewer/'],
    instead: 'показ PDF — PdfViewer (shared/ui/viewer: source — байты, documentKey, title, actions — выход места; overlay — во весь экран); «во вкладке» — openInNewTab, печать — Ctrl+P в просмотрщике (docs/UI.md, таблица контролов)',
  },
  {
    id: 'legacy-token',
    // Токены Fluent `--f-*` (эпик sharedui-md3, [UI-57]) и `--legacy-*` — значения старых имён без роли
    // MD3 (сняты sharedui-md3-05): удалены целиком, уровень 0 навсегда. Смотрит и стили (`styles`): тень
    // и цвет пишут и в CSS.
    pattern: /--(?:f|legacy)-[\w-]/g,
    everywhere: true,
    styles: true,
    skip: ['theme/'],
    instead: 'переменная MD3: --f-shadow4/16/28 → --md-sys-elevation-level1/2/3, --f-brand → --md-sys-color-primary, цвет — --md-sys-color-<роль> или класс роли (bg-primary, text-on-surface) (docs/UI.md, «Цвет»)',
  },
  {
    id: 'legacy-class',
    // Старые имена цвета (text-fg1, bg-brand…) — переходные псевдонимы ролей MD3 ([UI-58]): рисуют
    // прежнее, но расти им нельзя. Перечень — псевдонимы описания темы, граница класса — та же, что у
    // сторожа словаря (любая цветовая утилита, вариант, прозрачность). Плюс `var(--color-<старое>)` —
    // и в файлах стилей: там старое имя пишут именно так. `theme/` — сами псевдонимы.
    // `text-base` — размер текста, а не цвет. Уровень 0 (sharedui-md3-05); снимается финалом эпика вместе
    // с псевдонимами.
    count: countLegacyClasses,
    everywhere: true,
    styles: true,
    skip: ['theme/'],
    instead: `класс роли MD3 вместо старого имени: ${LEGACY_HINT}; brand-hover, brand-pressed → наведение и нажатие рисует state-layer, текст — text-primary; fg-disabled → text-on-surface/38; обводки по статусу нет — рамка формы outline-variant (docs/UI.md, «Цвет»)`,
  },
  {
    id: 'container-text',
    // Текст на контейнере роли цветом самой роли ([UI-62], sharedui-md3-04): `bg-warning-container
    // text-warning` в одном списке классов — то, что кодмод md3:migrate перевёл в on-*-container. Счёт —
    // `containerTextHits` из class-scan.mjs, тот же, что у кодмода: что он переводит, то правило и видит.
    // Разнесённое по элементам (плашка снаружи, текст внутри) не видно — как и кодмоду.
    count: text => containerTextHits(text).length,
    everywhere: true,
    instead: 'текст и значок на контейнере роли — парной ролью: bg-warning-container text-on-warning-container, bg-error-container text-on-error-container; цвет роли (text-warning) — для текста на поверхности (docs/UI.md, «Цвет»)',
  },
  {
    id: 'raw-state',
    // Самодельное состояние ([UI-64], sharedui-md3-05): подложка чёрным или белым с прозрачностью,
    // brightness-* и наведение заливкой поверхности (hover:bg-surface-container) — около 22 способов до
    // перехода, уровень 0 после него. По строкам кода (classTokens), не по тексту файла: класс в
    // комментарии не рисуется.
    count: countRawStates,
    everywhere: true,
    instead: 'состояние — слоем MD3: state-layer (наведение 8 %, фокус и нажатие 10 %, пункт меню Radix — сам по data-highlighted; часть элемента — state-layer-group у .group), другой цвет слоя — [--_state-color:…]; отключённое — disabled-container / disabled-content; затемнение под окном — bg-scrim/32 (docs/UI.md, «Состояния»)',
  },
  {
    id: 'dark-variant',
    // Вариант тёмной темы в классах ([UI-64]): роль MD3 знает обе темы сама, а dark: держал пару
    // самодельного наведения (hover:bg-black/5 dark:hover:bg-white/10). По строкам кода, как raw-state:
    // регулярное выражение со строкой dark: — не класс. Уровень 0, и варианта dark в теме больше нет:
    // класс dark: отвечал бы теме системы, а не выбранной в приложении.
    count: countDarkVariants,
    everywhere: true,
    instead: 'цвет обеих тем даёт роль MD3 — второй класс для тёмной не нужен; наведение — state-layer; то, чего ролью не сказать (белый лист просмотрщика), — правилом в CSS компонента селектором :root[data-theme="dark"] (docs/UI.md, «Цвет»)',
  },
  {
    id: 'legacy-radius',
    // Скругление вне шкалы формы MD3 ([UI-66], sharedui-md3-06): `rounded` без ступени, rounded-2xl/3xl,
    // произвольный rounded-[…], borderRadius в коде и border-radius числом в стилях. Ступени 6 и 10 px
    // исчезли вместе с прежней шкалой — кодмод md3:migrate перевёл их по значению; остаток ждёт роли
    // компонента (ребёнок 17).
    count: countLegacyRadii,
    everywhere: true,
    styles: true,
    instead: 'скругление — ступенью шкалы формы MD3: rounded-xs 4, rounded-sm 8, rounded-md 12, rounded-lg 16, rounded-xl 28, rounded-full, rounded-none; сторона и угол — так же (rounded-t-xl); какая ступень какому компоненту — docs/UI.md, «Форма и возвышение»',
  },
  {
    id: 'legacy-shadow',
    // Тень вне уровней возвышения MD3 ([UI-66], sharedui-md3-06): встроенные shadow, shadow-sm…2xl,
    // произвольный shadow-[…], drop-shadow-*, boxShadow в коде и box-shadow не уровнем в стилях (внутренняя
    // тень — не возвышение). Значение уровня одно на обе темы и цветом роли shadow — своя тень расходится
    // с ним в тёмной теме первой.
    count: countLegacyShadows,
    everywhere: true,
    styles: true,
    instead: 'тень — уровнем возвышения MD3 по роли слоя: shadow-elevation-1 (кнопка, карточка), shadow-elevation-2 (меню, список, поповер, палитра), shadow-elevation-3 (окно, плавающая кнопка), 4 и 5 — по таблице; без тени — shadow-none; черта места вставки строки — drop-mark-before / drop-mark-after (docs/UI.md, «Форма и возвышение»)',
  },
  {
    id: 'legacy-type',
    // Текст вне ролей MD3 ([UI-67], sharedui-md3-07): ступени Tailwind text-xs…9xl, свои caption и note,
    // начертания font-semibold и крупнее, uppercase и свой tracking-*. Мелкие ступени (xs, sm, base,
    // caption) кодмод md3:migrate перевёл по значению; note, капс и крупные ступени ждут роли по месту,
    // начертания — ребёнка 17. По строкам кода (classTokens): класс в комментарии не рисуется.
    count: countLegacyType,
    everywhere: true,
    instead: 'текст — ролью MD3 целиком: text-body-small 12, text-body-medium 14, text-body-large 16 — текст; text-label-small/medium/large — подпись, пометка, кнопка; text-title-small 14/500 — заголовок группы, строчными; text-title-large, text-headline-small — заголовок сводки; вес рядом с ролью — font-medium для акцента, font-normal — снять жирность браузера; стиль объектом — тем же классом (docs/UI.md, «Текст»)',
  },
  {
    id: 'lucide-import',
    // Прежний набор значков и пакет Material Symbols в обход набора shared/ui ([UI-60], sharedui-md3-03):
    // `from 'lucide-react'`, `from '@material-symbols/…'` и динамический `import()` их же. В бандл едут
    // только значки манифеста; пакет читает генератор (scripts/icons-generate.mjs), а он вне src/.
    // Комментарии не в счёт: пояснение «раньше здесь был lucide» не должно держать правило над нулём.
    count: text => stripComments(text).match(ICON_PACKAGE_IMPORT)?.length ?? 0,
    everywhere: true,
    instead: 'значок — Icon (shared/ui/Icon) со значком из набора shared/ui/icons: <Icon icon={Delete} size={18} />; значка нет в наборе — имя файла пакета в src/shared/ui/icons/manifest.json и npm run icons:generate (docs/UI.md, «Значки»)',
  },
  {
    id: 'spinner-icon',
    // Вращающийся значок вместо индикатора хода ([UI-61]): `animate-spin` у компонента (значок набора,
    // прежний Loader2) или у `<svg>`. Сам индикатор крутится своим классом `md-progress` (index.css) —
    // исключение ему не нужно, и вернувшийся в него `animate-spin` правило заметит.
    count: countSpinnerIcons,
    everywhere: true,
    instead: 'ход операции — CircularProgress (shared/ui/CircularProgress: size — ступень шкалы, label — когда индикатор стоит один); кнопка в ожидании ответа — Button loading (docs/UI.md, «Значки»)',
  },
  {
    id: 'icon-size',
    // Размер `<Icon>` вне шкалы ([UI-60]): литерал, которым `size` может стать (ветвь условия, `||`, `??`),
    // и размер мимо `size` — классом (`h-3.5 w-3.5`) или `style`: CSS перекрывает атрибуты width/height.
    // Переменную не считает: её держит тип `IconSize`, а размер, пришедший пропом, сверен у источника.
    count: countIconSizes,
    everywhere: true,
    instead: `размер значка — только пропом size, ступень шкалы ${ICON_SCALE.join(', ')}, не классом h-/w-/size- и не style: 18 — текст, кнопка, чип, строка; 20 и 24 — кнопка-иконка и навигация; 40 и 48 — только пустые состояния (docs/UI.md, «Значки»)`,
  },
];

function isTabRole({ attrs }) {
  return /^["'{`]*(?:tab|tablist|tabpanel)["'}`]*$/.test(attrs.get('role') ?? '');
}

// Правила по дереву читают один и тот же файл подряд — дерево разбирается однажды.
let parsed = { text: null, sf: null };
function parse(text) {
  if (parsed.text !== text) parsed = { text, sf: ts.createSourceFile('x.tsx', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX) };
  return parsed.sf;
}

/** Значение атрибута разметки строкой: `"x"`, `{'x'}`, `{"x"}`; выражение — null. */
function literalOf(init) {
  if (!init) return null;
  if (ts.isStringLiteral(init)) return init.text;
  const e = init.expression;
  return e && (ts.isStringLiteral(e) || ts.isNoSubstitutionTemplateLiteral(e)) ? e.text : null;
}

/** `<input type="checkbox">` — см. правило `native-checkbox`. */
function countNativeCheckboxes(text) {
  if (!text.includes('checkbox')) return 0;
  const sf = parse(text);
  let n = 0;
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && node.tagName.getText(sf) === 'input'
      && node.attributes.properties.some(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'type'
        && literalOf(p.initializer) === 'checkbox'))
      n++;
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

// Сплошная заливка роли (с наведением и нажатием) — классом или произвольным значением на переменной
// (`bg-[var(--md-sys-color-primary)]`), — и белое поверх неё. Роли MD3 и старые имена-псевдонимы
// (brand, danger — до финала эпика) в обоих словарях. `bg-primary-container` — контейнер, на нём свой
// текст (text-on-primary-container), и заливкой он не считается. Белый фон с прозрачностью — слой состояния.
const ROLE = 'primary|error|success|warning|brand(?:-hover|-pressed)?|danger';
const FILL = new RegExp(`(?<![\\w-])(?:[\\w-]+:)*bg-(?:(?:${ROLE})(?![\\w-])|\\[var\\(--(?:color-(?:${ROLE})|md-sys-color-(?:primary|error)|md-ext-color-(?:success|warning))\\)\\])`);
const WHITE = /(?<![\w-])(?:[\w-]+:)*(?:(?:text|ring|border|outline|fill|stroke|divide|decoration|shadow|accent|caret|from|via|to)-white(?![\w-])|bg-white(?![\w/-]))/;

/** Белое на сплошной заливке — см. правило `white-on-fill`. */
function countWhiteOnFill(text) {
  if (!text.includes('white')) return 0;
  const sf = parse(text);
  let n = 0;
  const hit = node => { const t = node.getText(sf); return FILL.test(t) && WHITE.test(t); };
  const visit = node => {
    if (ts.isJsxAttribute(node) && node.name.getText(sf) === 'className') {
      if (node.initializer && hit(node.initializer)) n++;
      return;
    }
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node) || ts.isTemplateExpression(node)) {
      if (hit(node)) n++;
      return;
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/** `<….Portal container=…>` и `createPortal(узел, контейнер)` — см. правило `portal-container`. */
function countPortalContainers(text) {
  return text.includes('Portal') ? portalContainersIn(parse(text)) : 0;
}

const NATIVE_DATE = new Set(['date', 'month', 'time', 'datetime-local', 'week']);

/**
 * Может ли выражение `type` дать нативный тип даты: литерал — сам, условие — любой своей ветвью,
 * `a && b` — правой частью, `a || b` и `a ?? b` — любой. Условие и сравнения не в счёт:
 * `kind === 'date' ? 'text' : 'number'` для даты рисует как раз текстовое поле.
 */
function yieldsNativeDate(node) {
  if (!node) return false;
  if (ts.isJsxExpression(node) || ts.isParenthesizedExpression(node)) return yieldsNativeDate(node.expression);
  if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) return NATIVE_DATE.has(node.text);
  if (ts.isConditionalExpression(node)) return yieldsNativeDate(node.whenTrue) || yieldsNativeDate(node.whenFalse);
  if (ts.isBinaryExpression(node)) {
    const op = node.operatorToken.kind;
    if (op === ts.SyntaxKind.AmpersandAmpersandToken) return yieldsNativeDate(node.right);
    if (op === ts.SyntaxKind.BarBarToken || op === ts.SyntaxKind.QuestionQuestionToken)
      return yieldsNativeDate(node.left) || yieldsNativeDate(node.right);
  }
  return false;
}

/** Теги, отдающие `type` полю как есть: `input`, `TextField` и его имена при импорте (`TextField as X`). */
function typedInputTags(sf) {
  const tags = new Set(['input', 'TextField']);
  for (const st of sf.statements) {
    const named = ts.isImportDeclaration(st) && st.importClause?.namedBindings;
    if (named && ts.isNamedImports(named))
      for (const el of named.elements)
        if ((el.propertyName ?? el.name).text === 'TextField') tags.add(el.name.text);
  }
  return tags;
}

/** `<input type="date">` и `type`, который может им стать — см. правило `native-date`. */
function countNativeDates(text) {
  if (!/date|month|time|week/.test(text)) return 0;
  const sf = parse(text);
  const tags = typedInputTags(sf);
  let n = 0;
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && tags.has(node.tagName.getText(sf))
      && node.attributes.properties.some(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'type'
        && yieldsNativeDate(p.initializer)))
      n++;
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/**
 * `<DateInput>` со своей рамкой в className — см. правило `date-frame`. Класс, вынесенный в константу
 * файла (`className={cls}`, `` `w-full ${cls}` ``), читается по её значению: иначе рамка уходила бы
 * от правила одним переименованием (ревью PR #1553). Константы из других файлов не видны.
 */
function countDateFrames(text) {
  if (!text.includes('DateInput')) return 0;
  const sf = parse(text);
  const consts = new Map();
  const collect = node => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer) {
      consts.set(node.name.text, node.initializer.getText(sf));
    }
    ts.forEachChild(node, collect);
  };
  collect(sf);
  let n = 0;
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && node.tagName.getText(sf) === 'DateInput') {
      const cls = node.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'className');
      const named = [];
      const names = node => {
        if (ts.isIdentifier(node)) named.push(consts.get(node.text) ?? '');
        ts.forEachChild(node, names);
      };
      if (cls?.initializer) names(cls.initializer);
      const raw = cls?.initializer ? cls.initializer.getText(sf) : '';
      const tokens = [raw, ...named].join(' ').split(/[\s'"`{}()?:]+/);
      if (tokens.some(t => /^border(?:-[\w/-]+)?!?$/.test(t))) n++;
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/** Значение атрибута тега по имени (узел инициализатора) или undefined. */
function attrOf(node, name, sf) {
  return node.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === name)?.initializer;
}

/** `animate-spin` у компонента или `<svg>` — см. правило `spinner-icon`. */
function countSpinnerIcons(text) {
  if (!text.includes('animate-spin')) return 0;
  const sf = parse(text);
  let n = 0;
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && /^(?:[A-Z]|svg$)/.test(node.tagName.getText(sf))
      && /(?<![\w-])animate-spin(?![\w-])/.test(attrOf(node, 'className', sf)?.getText(sf) ?? ''))
      n++;
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/** Числа, которыми может стать выражение `size`: литерал, ветви условия, части `||`, `??`, правая `&&`. */
function sizeLiterals(node) {
  if (!node) return [];
  if (ts.isJsxExpression(node) || ts.isParenthesizedExpression(node)) return sizeLiterals(node.expression);
  if (ts.isNumericLiteral(node)) return [Number(node.text)];
  if (ts.isStringLiteral(node)) return [Number(node.text)];
  if (ts.isConditionalExpression(node)) return [...sizeLiterals(node.whenTrue), ...sizeLiterals(node.whenFalse)];
  if (ts.isBinaryExpression(node)) {
    const op = node.operatorToken.kind;
    if (op === ts.SyntaxKind.AmpersandAmpersandToken) return sizeLiterals(node.right);
    if (op === ts.SyntaxKind.BarBarToken || op === ts.SyntaxKind.QuestionQuestionToken)
      return [...sizeLiterals(node.left), ...sizeLiterals(node.right)];
  }
  return [];
}

/** Модуль значка от src/: `Icon` из другого модуля — чужой компонент, его размер не наш. */
const ICON_MODULE = 'shared/ui/Icon';

/**
 * Ведёт ли импорт `spec` из файла `file` (путь от src/) в shared/ui/Icon: `@/shared/ui/Icon` или
 * относительный путь, который туда приходит. Без пути файла — относительный, кончающийся на него.
 */
function isIconModule(spec, file) {
  if (spec === `@/${ICON_MODULE}`) return true;
  if (!spec.startsWith('.')) return false;
  return file ? posix.join(posix.dirname(file), spec) === ICON_MODULE : spec.endsWith(`/${ICON_MODULE}`);
}

/** Имена, под которыми файл импортирует `Icon` из shared/ui/Icon (с `as` тоже). */
function iconTags(sf, file) {
  const tags = new Set();
  for (const st of sf.statements) {
    const named = ts.isImportDeclaration(st) && isIconModule(st.moduleSpecifier.text, file) && st.importClause?.namedBindings;
    if (named && ts.isNamedImports(named))
      for (const el of named.elements)
        if ((el.propertyName ?? el.name).text === 'Icon') tags.add(el.name.text);
  }
  return tags;
}

/** Размер классом (`h-4`, `sm:w-5`, `size-3.5`, `min-h-6`) — в тексте значения className. */
const SIZE_CLASS = /(?<![\w-])(?:(?:min-|max-)?[wh]|size)-[^\s'"`}]/;
/** Размер в `style`: ключ ширины или высоты. */
const SIZE_STYLE = /\b(?:min|max)?(?:[wW]idth|[hH]eight)\s*:/;

/** `<Icon size={…}>` с литералом вне шкалы или размер мимо `size` — см. правило `icon-size`. */
function countIconSizes(text, file) {
  if (!text.includes('Icon')) return 0;
  const sf = parse(text);
  const tags = iconTags(sf, file);
  if (!tags.size) return 0;
  let n = 0;
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && tags.has(node.tagName.getText(sf))
      && (sizeLiterals(attrOf(node, 'size', sf)).some(v => !ICON_SCALE.includes(v))
        || SIZE_CLASS.test(attrOf(node, 'className', sf)?.getText(sf) ?? '')
        || SIZE_STYLE.test(attrOf(node, 'style', sf)?.getText(sf) ?? '')))
      n++;
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/**
 * Сколько элементов разметки (тег со строчной буквы) подходят под `match`. `components(sf)` — какие
 * компоненты файла правило смотрит наравне с разметкой: ответ — проверка имени тега, как в файле.
 */
function countElements(text, match, { components = () => () => false } = {}) {
  if (!text.includes('<')) return 0;
  const sf = parse(text), component = components(sf);
  let n = 0;
  const visit = node => {
    if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
      const tag = node.tagName.getText(sf);
      if (/^[a-z]/.test(tag) || component(tag)) {
        const attrs = new Map(node.attributes.properties.filter(ts.isJsxAttribute)
          .map(p => [p.name.getText(sf), p.initializer?.getText(sf) ?? '']));
        const element = ts.isJsxSelfClosingElement(node) ? node : node.parent;
        if (match({ tag, cls: attrs.get('className') ?? '', attrs, element, sf })) n++;
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/** Подсказка — полный текст самой строки: `title` совпадает с её содержимым. */
function repeatsContent(init, element) {
  if (!init || !ts.isJsxElement(element)) return false;
  const kids = element.children.flatMap(c => ts.isJsxElement(c) ? [c, ...c.children] : [c]);
  const norm = s => s.replace(/\s+/g, ' ');
  if (ts.isStringLiteral(init)) return kids.some(c => ts.isJsxText(c) && c.text.trim() === init.text);
  const want = init.expression && norm(init.expression.getText());
  return kids.some(c => ts.isJsxExpression(c) && c.expression && norm(c.expression.getText()) === want);
}

/** Нативные `title` у элементов разметки — кроме законных (см. правило `native-title`). */
function countNativeTitles(text) {
  if (!text.includes('title')) return 0;
  const sf = parse(text);
  let n = 0;
  const visit = node => {
    if (ts.isJsxAttribute(node) && node.name.getText(sf) === 'title') {
      const opening = node.parent.parent;
      const tag = opening.tagName.getText(sf);
      const element = ts.isJsxSelfClosingElement(opening) ? opening : opening.parent;
      const cls = opening.attributes.properties
        .find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'className')?.initializer?.getText(sf) ?? '';
      const truncated = /\b(?:truncate|line-clamp-\d+)\b/.test(cls) && repeatsContent(node.initializer, element);
      if (/^[a-z]/.test(tag) && !truncated) n++;
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

const FRAME_TAGS = new Set(['iframe', 'embed', 'object']);

/** `<iframe>`, `<embed>`, `<object>` и `createElement('iframe')` — см. правило `native-pdf-frame`. */
function countNativeFrames(text) {
  if (!/iframe|embed|object/.test(text)) return 0;
  const sf = parse(text);
  let n = 0;
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && FRAME_TAGS.has(node.tagName.getText(sf)))
      n++;
    if (ts.isCallExpression(node) && /(?:^|\.)createElement$/.test(node.expression.getText(sf))) {
      const tag = node.arguments[0];
      if (tag && (ts.isStringLiteral(tag) || ts.isNoSubstitutionTemplateLiteral(tag)) && FRAME_TAGS.has(tag.text.toLowerCase()))
        n++;
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/**
 * Область правила без своих `dirs` (#1469): экраны и обвязка shared/ — без shared/ui/. Граница — по
 * пути, а не перечнем каталогов shared/: новый shared/<x> попадает под присмотр сам.
 */
export const DEFAULT_SCOPE = { dirs: ['features', 'shared'], skip: ['shared/ui/'] };

/** Область по умолчанию строкой для сводки: `features/ и shared/ без shared/ui/`. */
export const defaultScopeNote = () =>
  `${DEFAULT_SCOPE.dirs.map(d => `${d}/`).join(' и ')} без ${DEFAULT_SCOPE.skip.join(', ')}`;

/**
 * Каталоги, которые правило называет (см. `dirs` у RULES); правило с `everywhere` смотрит любой. Файл
 * (путь от src/) под префиксом из `skip` правило не смотрит; без пути — решает один каталог. Правило без
 * `dirs` и без `everywhere` пропускает и префиксы области по умолчанию. Файл стилей (`.css`) смотрят
 * только правила со `styles`: прочие ищут разметку и классы в коде.
 */
export const dirsOf = rule => rule.dirs ?? DEFAULT_SCOPE.dirs;
export const skipOf = rule => rule.dirs || rule.everywhere === true ? rule.skip ?? [] : [...DEFAULT_SCOPE.skip, ...rule.skip ?? []];
export const watches = (rule, dir, file) => (rule.everywhere === true || dirsOf(rule).includes(dir))
  && !(file && skipOf(rule).some(prefix => file.startsWith(prefix)))
  && !(file?.endsWith('.css') && rule.styles !== true);

/**
 * Какие каталоги src/ обходить. `topDirs` — каталоги в корне src/ с диска: правило с `everywhere`
 * добавляет их все и корень (`ROOT`), чтобы новый каталог не выпал из надзора молча.
 */
export function dirsToScan(rules, topDirs) {
  if (rules.some(r => r.everywhere === true)) return [ROOT, ...topDirs];
  return [...new Set(rules.flatMap(dirsOf))];
}

/**
 * Область правила, отличная от области по умолчанию, — строкой для сводки: шире (`white-on-fill — и в
 * shared/ui/`: свои каталоги сверх неё и пропуски по умолчанию, которые правило смотрит) или уже
 * (`x — без shared/`: каталоги по умолчанию, которых правило не смотрит). Совпадает — null.
 */
export function scopeNote(rule) {
  if (rule.everywhere === true)
    return `${rule.id} — весь src/${rule.styles ? ' со стилями' : ''}${rule.skip?.length ? `, кроме ${rule.skip.join(', ')}` : ''}`;
  if (!rule.dirs) return null;
  const more = [
    ...rule.dirs.filter(d => !DEFAULT_SCOPE.dirs.includes(d)).map(d => `${d}/`),
    ...DEFAULT_SCOPE.skip.filter(p => !skipOf(rule).includes(p) && rule.dirs.some(d => p.startsWith(`${d}/`))),
  ];
  const fewer = DEFAULT_SCOPE.dirs.filter(d => !rule.dirs.includes(d)).map(d => `${d}/`);
  const except = rule.skip?.length ? `, кроме ${rule.skip.join(', ')}` : '';
  const parts = [more.length ? `и в ${more.join(', ')}${except}` : '', fewer.length ? `без ${fewer.join(', ')}` : '']
    .filter(Boolean);
  return parts.length ? `${rule.id} — ${parts.join('; ')}` : null;
}

/**
 * Сколько нарушений каждого класса в тексте одного файла. Нулевые классы не возвращаются.
 * `dir` — каталог src/, из которого файл (`ROOT` — его корень), `file` — путь от src/: считаются только
 * правила, которые его смотрят (`watches`).
 */
export function countViolations(text, { dir = 'features', file } = {}) {
  const counts = {};
  for (const rule of RULES.filter(r => watches(r, dir, file))) {
    const n = rule.count ? rule.count(text, file) : text.match(rule.pattern)?.length ?? 0;
    if (n > 0) counts[rule.id] = n;
  }
  return counts;
}

/**
 * Сравнение по ФАЙЛАМ, а не по общему числу: иначе «убрал таблицу здесь, завёл в новом экране»
 * прошло бы молча, а новый экран как раз и есть то, что храповик обязан остановить.
 *
 * @param baseline { [ruleId]: { [file]: count } }
 * @param actual   { [ruleId]: { [file]: count } }
 */
export function compare(baseline, actual) {
  const worse = [];
  const better = [];
  for (const rule of RULES) {
    const was = baseline[rule.id] ?? {};
    const now = actual[rule.id] ?? {};
    for (const [file, n] of Object.entries(now))
      if (n > (was[file] ?? 0)) worse.push({ rule: rule.id, file, was: was[file] ?? 0, now: n });
    for (const [file, n] of Object.entries(was))
      if ((now[file] ?? 0) < n) better.push({ rule: rule.id, file, was: n, now: now[file] ?? 0 });
  }
  return { worse, better };
}

/** Устойчивый порядок ключей — чтобы базовый уровень менялся в диффе только по делу. */
export function normalize(actual) {
  const out = {};
  for (const rule of RULES) {
    const files = actual[rule.id] ?? {};
    out[rule.id] = Object.fromEntries(Object.keys(files).sort().map(f => [f, files[f]]));
  }
  return out;
}
