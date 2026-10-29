// Счёт словаря MD3 для храповика интерфейса (эпик sharedui-md3): старые имена цвета, самодельные
// состояния и вариант тёмной темы. Отдельно от ui-ratchet-lib.mjs: там правила по разметке, здесь —
// по классам в строках кода, и словарь растёт с каждым ребёнком эпика.

import { COLOR_UTILITIES, classTokens, scanCandidates, stripComments } from './class-scan.mjs';
import theme from '../src/theme/md3-theme.json' with { type: 'json' };
import classMap from './md3-class-map.json' with { type: 'json' };

// Старые имена цвета (правило `legacy-class`) — псевдонимы из описания темы и имена карты md3:migrate:
// снятый псевдоним (warning-border, stroke-subtle) класса больше не даёт, и пришедший из старой ветки
// `border-warning-border` молча рисовал бы рамку цветом текста. Длинные — первыми.
const LEGACY = Object.entries(theme.aliases).filter(([k]) => !k.startsWith('//'));
const LEGACY_NAMES = [...new Set([...LEGACY.map(([k]) => k), ...Object.keys(classMap.names)])]
  .sort((a, b) => b.length - a.length).join('|');
const LEGACY_CLASS = new RegExp(`^(?:${COLOR_UTILITIES})-(?:${LEGACY_NAMES})(?:/[\\w.]+)?$`);
const LEGACY_VAR = new RegExp(`var\\(--color-(?:${LEGACY_NAMES})\\)`, 'g');

/** Совет правила `legacy-class`: старое имя → роль того же цвета. */
export const LEGACY_HINT = LEGACY.filter(([, to]) => typeof to === 'string').map(([k, to]) => `${k} → ${to}`).join(', ');

/** Старые имена цвета классом и `var(--color-…)` вне комментариев — см. правило `legacy-class`. */
export function countLegacyClasses(source) {
  const text = stripComments(source);
  const classes = scanCandidates(text).filter(c => LEGACY_CLASS.test(c.cls) && !/^text-base(?:\/|$)/.test(c.cls));
  return classes.length + (text.match(LEGACY_VAR)?.length ?? 0);
}

/**
 * Сырое состояние вместо слоя MD3 — см. правило `raw-state`: подложка чёрным или белым с прозрачностью
 * (`bg-black/5`, `bg-white/10`), фильтр яркости и наведение (подсветка пункта) заливкой другой поверхности.
 * Затемнение под окном — роль `scrim` (`bg-scrim/32`) — не считается.
 */
const RAW_FILL = /^bg-(?:black|white)\/\S+$/;
const SURFACE_FILL = /^bg-(?:surface(?:-[\w-]+)?|base|muted(?:-hover)?)(?:\/\S+)?$/;
const HOVER_VARIANT = /hover|highlighted/;
const isRawState = ({ cls, variants }) => RAW_FILL.test(cls) || /^brightness-/.test(cls)
  || (SURFACE_FILL.test(cls) && variants.some(v => HOVER_VARIANT.test(v)));

/** Самодельные состояния в строках кода — см. правило `raw-state`. */
export const countRawStates = text => classTokens(text).filter(isRawState).length;

/** Классы с вариантом `dark:` в строках кода — см. правило `dark-variant`. */
export const countDarkVariants = text => classTokens(text).filter(t => t.variants.includes('dark')).length;

// Форма и возвышение ([UI-66], sharedui-md3-06): ступень шкалы — none, xs…xl, full; без ступени
// (`rounded`, `rounded-t`) — прежние 4 px, вне шкалы. Сторона или угол — не ступень: `rounded-sm` —
// ступень small, а не сторона s (сторона без ступени — `rounded-s`).
const RADIUS = /^rounded(?:-(?:t|r|b|l|s|e|tl|tr|br|bl|ss|se|es|ee))?(?:-(.+))?$/;
const RADIUS_STEPS = new Set(['none', 'xs', 'sm', 'md', 'lg', 'xl', 'full']);
const isLegacyRadius = ({ cls }) => { const m = RADIUS.exec(cls); return !!m && !RADIUS_STEPS.has(m[1] ?? ''); };
// Тень фильтром (drop-shadow) выглядит тем же возвышением и так же идёт мимо уровней. Внутренняя тень
// (inset-shadow-*, box-shadow: inset — черта места вставки) возвышением не бывает и не считается.
const LEGACY_SHADOW = /^(?:drop-)?shadow(?:-(?:2xs|xs|sm|md|lg|xl|2xl|inner|\[.+\]|\(.+\)))?$/;
// В файле стилей — значение свойства: своё число вместо переменной ступени или уровня. Пробелы — внутри
// просмотра: снаружи `\s*` отступил бы на ноль, и « none» уже не начиналось бы с none.
const CSS_RADIUS = /\bborder(?:-[a-z]+)*-radius\s*:(?!\s*(?:0\s*[;}]|inherit\b|var\(--md-sys-shape-corner-[a-z-]+\)\s*[;}!]))/g;
const CSS_SHADOW = /\bbox-shadow\s*:(?!\s*(?:none\b|inset\b|var\(--md-sys-elevation-level[0-5]\)\s*[;}!]))/g;
const count = (text, re) => stripComments(text).match(re)?.length ?? 0;

/** Радиус вне шкалы формы: класс в строках кода, `borderRadius` в коде, `border-radius` числом в стилях — см. `legacy-radius`. */
export const countLegacyRadii = text =>
  classTokens(text).filter(isLegacyRadius).length + count(text, /\bborderRadius\b/g) + count(text, CSS_RADIUS);

/** Тень вне уровней возвышения: класс в строках кода, `boxShadow` в коде, `box-shadow` не уровнем в стилях — см. `legacy-shadow`. */
export const countLegacyShadows = text =>
  classTokens(text).filter(({ cls }) => LEGACY_SHADOW.test(cls)).length + count(text, /\bboxShadow\b/g) + count(text, CSS_SHADOW);

// Роли текста ([UI-67], sharedui-md3-07): ступени Tailwind (text-xs…9xl) и свои caption и note — вне
// ролей MD3; font-semibold и крупнее — начертания вне шкалы (600 не загружен и рисуется 700); uppercase
// и свой tracking-* — overline Material 2. Цвет `text-<роль цвета>` не путается с размером: правило
// ловит только имена ступеней. Высота строки через дробь (`text-sm/6`) — та же ступень. Те же свойства
// обходом — тоже: вес числом (`font-[600]`), свойство в скобках (`[letter-spacing:.08em]`; `inherit` —
// не своё значение, им клетка-заголовок снимает жирность браузера) и стиль объектом (`fontWeight: 600`;
// размер — только строкой с единицами: число `fontSize: 13` — настройка Monaco, а не CSS).
const LEGACY_TYPE = new RegExp('^(?:text-(?:xs|sm|base|lg|[2-9]?xl|caption|note)(?:/.+)?'
  + '|font-(?:semibold|bold|extrabold|black|\\[(?:number:)?\\d+\\])|uppercase|tracking-(?!normal$).+'
  + '|\\[(?:font-size|font-weight|letter-spacing|text-transform):(?!inherit\\]).+\\])$');
const STYLE_TYPE = /\b(?:fontWeight|letterSpacing|textTransform)\s*:|\bfontSize\s*:\s*['"`]/g;

/** Текст вне ролей MD3 в строках кода и в стилях объектом — см. правило `legacy-type`. */
export const countLegacyType = text =>
  classTokens(text).filter(({ cls }) => LEGACY_TYPE.test(cls)).length + count(text, STYLE_TYPE);
