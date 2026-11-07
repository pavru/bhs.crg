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
/**
 * Место хвоста — одной таблицей на все поля оболочки: правый отступ текста (`end`) и ширина подписи в покое
 * (`rest`, на 0.5rem больше отступа: левый край метки 0.75rem минус её `px-1`). Новый состав хвоста — строка
 * здесь, а не пара констант по файлам. Строками целиком: Tailwind собирает классы по тексту файла.
 *
 * - `button` — кнопка места XS: `right-2` + 32 (при касании 40) + 4 = 44 и 52.
 * - `markButton` — знак 18 и зазор 4 перед кнопкой ([UI-86], замок запертого поля; знак «со скана» [UI-88]):
 *   ещё 22 — 66 и 74. Знак, а не вторая кнопка: две кнопки в хвосте поля — уже панель действий, ей место не в поле.
 * - `mark` — один знак «со скана» без кнопки ([UI-88]): `right-2` + 18 + 4 = 30. Знак не кнопка — при касании
 *   место то же.
 */
const TRAIL = {
  button: {
    end: 'pr-[calc(2.75rem+var(--field-mark,0px))] pointer-coarse:pr-[calc(3.25rem+var(--field-mark,0px))]',
    rest: 'max-w-[calc(100%-3.25rem-var(--field-mark,0px))] pointer-coarse:max-w-[calc(100%-3.75rem-var(--field-mark,0px))]',
  },
  markButton: {
    end: 'pr-[calc(4.125rem+var(--field-mark,0px))] pointer-coarse:pr-[calc(4.625rem+var(--field-mark,0px))]',
    rest: 'max-w-[calc(100%-4.625rem-var(--field-mark,0px))] pointer-coarse:max-w-[calc(100%-5.125rem-var(--field-mark,0px))]',
  },
  mark: {
    end: 'pr-[calc(1.875rem+var(--field-mark,0px))]',
    rest: 'max-w-[calc(100%-2.375rem-var(--field-mark,0px))]',
  },
} as const;
/** Подпись в покое у поля без хвоста — до текста (отступ поля формы 1rem + 0.5rem). */
const REST_NO_TRAIL = 'max-w-[calc(100%-1.5rem-var(--field-mark,0px))]';

/**
 * Хвост поля для расчёта места: есть ли он, стоит ли в нём перед кнопкой ещё знак 18 и есть ли знак «со скана»
 * ([UI-88]) — он тоже знак 18, с кнопкой хвоста или без неё.
 */
export interface FieldTrailing {
  trailing?: boolean;
  trailingMark?: boolean;
  fromScan?: boolean;
}

/**
 * Свойства хвоста у полей на оболочке (`TextField`, `TextAreaField`). Знак перед кнопкой бывает только при
 * самом хвосте: `trailingMark` без `trailing` — ошибка типов, а не тихо зарезервированное пустое место.
 * Знак и кнопку передают фрагментом, соседями: зазор между ними ставит слот оболочки (`gap-1`), своя
 * обёртка его сняла бы, и хвост вышел бы уже рассчитанного места.
 *
 * `fromScan` — значение со скана, не проверено ([UI-88]): знак ставит оболочка. Рядом с замком (`trailingMark`)
 * его не бывает — знаков в хвосте не больше двух ([UI-86]), и тип это держит.
 */
export type FieldTrailingProps =
  | { trailing?: ReactNode; trailingMark?: false; fromScan?: boolean }
  | { trailing: ReactNode; trailingMark: true; fromScan?: false };

const trailOf = ({ trailing = false, trailingMark = false, fromScan = false }: FieldTrailing) =>
  trailing ? TRAIL[trailingMark || fromScan ? 'markButton' : 'button'] : fromScan ? TRAIL.mark : null;

/**
 * Правый отступ текста поля — без высоты и роли текста. Для контрола, которому рамку `fieldBox` не взять
 * (многострочная область растёт по строкам), но место под хвост и знак ошибки нужно то же.
 */
export function fieldEnd(density: FieldDensity = 'form', tail: FieldTrailing = {}) {
  return trailOf(tail)?.end ?? BOX[density].end;
}

/** Ширина подписи поля формы в покое (`raise="auto"`): кончается до знака ошибки и хвоста. */
export function fieldLabelRest(tail: FieldTrailing = {}) {
  return trailOf(tail)?.rest ?? REST_NO_TRAIL;
}

/**
 * Приписка «со скана» к имени поля для диктора ([UI-88]) — одна на все поля. Оболочка кладёт её скрытым текстом
 * в подпись (её слышат `<label for>` и `aria-labelledby`); где имя даёт свойство контрола — группа сегментов
 * даты, клетка сетки, — имя строит `fieldName`.
 */
export const SCAN_NOTE = 'со скана, не проверено';

/** Имя поля для `aria-label`: подпись и, у значения со скана, приписка `SCAN_NOTE`. */
export function fieldName(label: string, fromScan = false) {
  return fromScan ? `${label} (${SCAN_NOTE})` : label;
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
 * Знаки поля для контрола со своим хвостом (`DateInput`: крестик и календарь; триггер `Select` — перед
 * стрелкой): знак «со скана» ([UI-88]) и знак ошибки, фрагментом в этом порядке. Такой контрол ставит их
 * перед хвостом сам, с зазором `gap-1`, а когда и какие — решает оболочка (`markInside`) и отдаёт сюда: ни
 * условия, ни размера знаков контрол не повторяет. Вне такой оболочки — `null`.
 */
export const FieldMarkContext = createContext<ReactNode>(null);
