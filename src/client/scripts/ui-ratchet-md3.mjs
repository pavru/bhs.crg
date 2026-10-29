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
