/**
 * Размер поля на оболочке `OutlinedField` ([UI-69], sharedui-md3-11): высота, отступы и роль текста — у
 * пяти контролов одни. Отдельным файлом, а не рядом с оболочкой: модуль с компонентами отдаёт только
 * компоненты, иначе горячая замена в разработке перезагружает страницу целиком.
 */
export type FieldDensity = 'form' | 'dense';

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
} as const;
const END_TRAILING = 'pr-[calc(2.75rem+var(--field-mark,0px))] pointer-coarse:pr-[calc(3.25rem+var(--field-mark,0px))]';

/** Место знака ошибки с зазором до хвоста: знак 24 у поля формы, 20 у плотного, плюс 4. */
export const MARK_SPACE: Record<FieldDensity, string> = { form: '28px', dense: '24px' };

/**
 * Высота, отступы и роль текста контрола на оболочке ([UI-69]): 56 и `body-large` у поля формы, 40 и
 * `body-medium` у плотного. `trailing` — у поля есть хвост в слоте оболочки (кнопка, стрелка выбора):
 * правый отступ тогда держит и его.
 */
export function fieldBox(density: FieldDensity = 'form', { trailing = false }: { trailing?: boolean } = {}) {
  const box = BOX[density];
  return `${box.box} ${trailing ? END_TRAILING : box.end}`;
}
