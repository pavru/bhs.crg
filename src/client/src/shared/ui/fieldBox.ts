import { createContext, useContext, type ReactNode } from 'react';

/**
 * Размер поля на оболочке `OutlinedField` ([UI-69], sharedui-md3-11): высота, отступы и роль текста — у
 * пяти контролов одни. Отдельным файлом, а не рядом с оболочкой: модуль с компонентами отдаёт только
 * компоненты, иначе горячая замена в разработке перезагружает страницу целиком.
 *
 * `cell` — поле в клетке сетки правки ([UI-78], sharedui-md3-14): 40, без рамки и заливки, черта только у
 * фокуса и ошибки (`cellLine`). Клетку рисует сетка — рамка поля была бы рамкой в рамке.
 */
export type FieldDensity = 'form' | 'dense' | 'cell';

/**
 * Размер поля, который задаёт место: тело `EditGrid` отдаёт `cell` всем полям своих клеток, а шапке и итогу —
 * нет (поле в шапке колонки — плотное 40 с рамкой, В5). Свой `density` у поля сильнее места.
 */
export const FieldDensityContext = createContext<FieldDensity | null>(null);

/** Размер поля: свой, иначе места (`FieldDensityContext`), иначе — размер контрола по умолчанию. */
export function useFieldDensity<D extends FieldDensity>(own: D | undefined, fallback: D): D | 'cell' {
  const place = useContext(FieldDensityContext);
  return own ?? (place === 'cell' ? 'cell' : fallback);
}

/**
 * Правый отступ текста — под хвост поля и знак ошибки. Знак ставит оболочка, а ширину его места отдаёт
 * переменной `--field-mark` (без ошибки — ноль): поле не знает, есть ли ошибка, и узнавать не должно.
 * Хвост — `right-2` + кнопка места XS (32, при касании 40) + 4: 44 и 52.
 */
const BOX = {
  form: {
    box: 'h-14 pl-4 text-body-large',
    end: 'pr-[calc(1rem+var(--field-mark,0px))]',
  },
  dense: {
    box: 'h-10 pl-3 text-body-medium',
    end: 'pr-[calc(0.75rem+var(--field-mark,0px))]',
  },
  cell: {
    box: 'h-10 pl-2 text-body-medium',
    end: 'pr-[calc(0.5rem+var(--field-mark,0px))]',
  },
} as const;
const END_TRAILING = 'pr-[calc(2.75rem+var(--field-mark,0px))] pointer-coarse:pr-[calc(3.25rem+var(--field-mark,0px))]';
/**
 * Хвост со знаком перед кнопкой ([UI-86]): замок 18 и зазор 4 до кнопки места XS — ещё 22 к 44 и 52.
 * Знак, а не вторая кнопка: две кнопки в хвосте поля — уже панель действий, ей место не в поле.
 */
const END_TRAILING_MARK = 'pr-[calc(4.125rem+var(--field-mark,0px))] pointer-coarse:pr-[calc(4.625rem+var(--field-mark,0px))]';

/** Хвост поля: есть ли он и стоит ли в нём перед кнопкой ещё знак 18 (замок запертого поля). */
export interface FieldTrailing {
  trailing?: boolean;
  /** Перед кнопкой хвоста — знак 18. Без `trailing` не значит ничего. */
  trailingMark?: boolean;
}

/**
 * Правый отступ текста поля — без высоты и роли текста. Для контрола, которому рамку `fieldBox` не взять
 * (многострочная область растёт по строкам), но место под хвост и знак ошибки нужно то же.
 */
export function fieldEnd(density: FieldDensity = 'form', { trailing = false, trailingMark = false }: FieldTrailing = {}) {
  if (!trailing) return BOX[density].end;
  return trailingMark ? END_TRAILING_MARK : END_TRAILING;
}

/** Место знака ошибки с зазором до хвоста: знак 24 у поля формы, 20 у плотного и клетки, плюс 4. */
export const MARK_SPACE: Record<FieldDensity, string> = { form: '28px', dense: '24px', cell: '24px' };

/**
 * Черта поля в клетке ([UI-78]): в покое её нет, у фокуса и открытого списка — 2 px `primary`, у ошибки —
 * `error`. Псевдоэлементом обёртки (`relative`), а не нижней границей: граница сдвигала бы текст. Фокус
 * виден по `focus-within`; контрол, у которого фокус уходит в портал (открытый список, календарь), отдаёт
 * его `focused`.
 */
export function cellLine({ focused = false, bad = false }: { focused?: boolean; bad?: boolean } = {}) {
  const color = bad ? 'after:bg-error' : focused ? 'after:bg-primary' : 'after:bg-transparent focus-within:after:bg-primary';
  return `relative after:pointer-events-none after:absolute after:inset-x-0 after:bottom-0 after:h-0.5 after:content-[''] ${color}`;
}

/**
 * Высота, отступы и роль текста контрола на оболочке ([UI-69]): 56 и `body-large` у поля формы, 40 и
 * `body-medium` у плотного и клетки (у клетки отступы теснее — `px-2`, как у самой клетки). `trailing` — у поля есть хвост в слоте оболочки (кнопка, стрелка выбора):
 * правый отступ тогда держит и его; `trailingMark` — перед кнопкой хвоста ещё знак (см. `fieldEnd`).
 */
export function fieldBox(density: FieldDensity = 'form', tail: FieldTrailing = {}) {
  return `${BOX[density].box} ${fieldEnd(density, tail)}`;
}

/**
 * `aria-describedby` поля на оболочке: свои описания контрола и подпись под полем — ошибка или пояснение,
 * если она есть (issue #1468, [UI-69]). Одна сборка на все поля: иначе её копия у каждого контрола.
 */
export function fieldDescribedBy(hintId: string, below: string | null | undefined, own?: string) {
  return [own, below ? hintId : undefined].filter(Boolean).join(' ') || undefined;
}

/**
 * Знак ошибки для контрола со своим хвостом (`DateInput`: крестик и календарь). Такой контрол ставит
 * знак перед хвостом сам, а когда и какой — решает оболочка (`markInside`) и отдаёт его сюда: ни
 * условия, ни размера знака контрол не повторяет. Вне такой оболочки — `null`.
 */
export const FieldMarkContext = createContext<ReactNode>(null);
