import type {
  PrintFit, PrintMargins, PrintOrientation, PrintPaper, PrintProfile, PrintProfileInput,
} from '@/shared/api/printProfiles';

/**
 * Правила страницы «Профили печати» (issue #1455): сводка строки, черновик формы и проверка до
 * отправки. Пределы — те же, что у сервера (`RenditionLayoutProfile`): проверка здесь лишь ставит
 * ошибку под поле раньше, источник истины — отказ сервера с адресом поля.
 */

export const NAME_MAX = 80;
export const MARGIN_MAX = 40;
/** Сколько миллиметров страницы обязано остаться под текст по ширине и по высоте. */
export const USEFUL_MIN_MM = 100;

/** Бумага по возрастанию — так её и выбирают. Стороны листа — короткая и длинная, мм. */
export const PAPERS: { value: PrintPaper; shortMm: number; longMm: number }[] = [
  { value: 'A5', shortMm: 148, longMm: 210 },
  { value: 'A4', shortMm: 210, longMm: 297 },
  { value: 'A3', shortMm: 297, longMm: 420 },
];

export const ORIENTATIONS: { value: PrintOrientation; label: string }[] = [
  { value: 'portrait', label: 'Книжная' },
  { value: 'landscape', label: 'Альбомная' },
];

export const FITS: { value: PrintFit; label: string; hint: string }[] = [
  {
    value: 'width', label: 'По ширине',
    hint: 'Все колонки на одной странице по ширине; по высоте — сколько страниц выйдет. Текст становится мельче, если лист широкий.',
  },
  {
    value: 'none', label: 'Не вписывать',
    hint: 'Масштаб 100 %: что не поместилось по ширине, уходит на следующую страницу.',
  },
];

export const MARGIN_SIDES: { key: keyof PrintMargins; label: string }[] = [
  { key: 'top', label: 'Верхнее' },
  { key: 'bottom', label: 'Нижнее' },
  { key: 'left', label: 'Левое' },
  { key: 'right', label: 'Правое' },
];

/** Размер страницы с учётом ориентации, мм. */
export function pageSize(paper: PrintPaper, orientation: PrintOrientation): { width: number; height: number } {
  const p = PAPERS.find(x => x.value === paper) ?? PAPERS[1];
  return orientation === 'landscape' ? { width: p.longMm, height: p.shortMm } : { width: p.shortMm, height: p.longMm };
}

// Сводка нужна и счёту (меню «Раскладка», #1456) — живёт рядом с типом профиля.
export { profileSummary } from '@/shared/api/printProfiles';

/** Профиль по умолчанию правили: его параметры разошлись с заводскими. У своего профиля такого нет. */
export function isModified(p: PrintProfile): boolean {
  return p.isSystem && !p.isFactory;
}

/** Черновик формы. Поля — строками: пустое поле — ошибка, а не молча подставленный ноль. */
export interface PrintProfileDraft {
  name: string;
  paper: PrintPaper;
  orientation: PrintOrientation;
  fit: PrintFit;
  margins: Record<keyof PrintMargins, string>;
}

export function toDraft(p: PrintProfileInput): PrintProfileDraft {
  const m = p.margins;
  return {
    name: p.name, paper: p.paper, orientation: p.orientation, fit: p.fit,
    margins: { top: String(m.top), bottom: String(m.bottom), left: String(m.left), right: String(m.right) },
  };
}

/** Черновик → тело запроса. Звать только после {@link validateDraft} без ошибок. */
export function fromDraft(d: PrintProfileDraft): PrintProfileInput {
  return {
    name: d.name.trim(), paper: d.paper, orientation: d.orientation, fit: d.fit,
    margins: {
      top: Number(d.margins.top), bottom: Number(d.margins.bottom),
      left: Number(d.margins.left), right: Number(d.margins.right),
    },
  };
}

export function sameDraft(a: PrintProfileDraft, b: PrintProfileDraft): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

const INTEGER = /^\d+$/;

/** Значение поля в миллиметрах; `null` — в поле не целое неотрицательное число. */
export function marginMm(value: string): number | null {
  const v = value.trim();
  return INTEGER.test(v) ? Number(v) : null;
}

export function nameIssue(name: string, otherNames: string[]): string | null {
  const trimmed = name.trim();
  if (!trimmed) return 'Укажите название';
  if (trimmed.length > NAME_MAX) return `Не длиннее ${NAME_MAX} знаков`;
  const lower = trimmed.toLocaleLowerCase('ru');
  if (otherNames.some(n => n.trim().toLocaleLowerCase('ru') === lower)) return 'Профиль с таким названием уже есть';
  return null;
}

/**
 * Ошибки черновика: путь поля → текст, пути — как у отказа сервера (`name`, `margins.top`…).
 *
 * <p>Нехватка места — ошибка ПАРЫ полей, и пишется она, как пишет сервер, на обе стороны пары
 * (`margins.left` и `margins.right`) одним текстом: исправляют её любым из двух. Мерить её можно,
 * только когда обе стороны годны — иначе «остаётся N мм» считалось бы от числа, которого в поле нет.
 * Отличить её от ошибки самого поля помогает {@link pairIssue}.</p>
 *
 * @param otherNames Названия остальных профилей — сверка без учёта регистра, как у сервера.
 */
export function validateDraft(d: PrintProfileDraft, otherNames: string[]): Record<string, string> {
  const issues: Record<string, string> = {};
  const name = nameIssue(d.name, otherNames);
  if (name) issues.name = name;

  const mm: Partial<Record<keyof PrintMargins, number>> = {};
  for (const { key } of MARGIN_SIDES) {
    const value = marginMm(d.margins[key]);
    if (value === null) issues[`margins.${key}`] = 'Целое число миллиметров';
    else if (value > MARGIN_MAX) issues[`margins.${key}`] = `От 0 до ${MARGIN_MAX} мм`;
    else mm[key] = value;
  }

  const page = pageSize(d.paper, d.orientation);
  const shortfall = (axis: string, rest: number) =>
    `Слишком широкие поля: ${axis} остаётся ${rest} мм. Нужно не меньше ${USEFUL_MIN_MM} мм.`;
  if (mm.left !== undefined && mm.right !== undefined) {
    const rest = page.width - mm.left - mm.right;
    if (rest < USEFUL_MIN_MM) issues['margins.left'] = issues['margins.right'] = shortfall('по ширине', rest);
  }
  if (mm.top !== undefined && mm.bottom !== undefined) {
    const rest = page.height - mm.top - mm.bottom;
    if (rest < USEFUL_MIN_MM) issues['margins.top'] = issues['margins.bottom'] = shortfall('по высоте', rest);
  }
  return issues;
}

/** Пары противоположных полей: место под текст меряется по каждой. */
export const MARGIN_PAIRS: [keyof PrintMargins, keyof PrintMargins][] = [['left', 'right'], ['top', 'bottom']];

/**
 * Ошибка пары полей — один текст на обеих сторонах (так её пишут и проверка здесь, и сервер). `null` —
 * ошибки пары нет: у сторон свои ошибки, или ошибок нет вовсе.
 */
export function pairIssue(issues: Record<string, string>, [a, b]: [keyof PrintMargins, keyof PrintMargins]): string | null {
  const first = issues[`margins.${a}`];
  return first && first === issues[`margins.${b}`] ? first : null;
}

/** Ошибка самого поля — та, что не принадлежит паре. */
export function fieldIssue(issues: Record<string, string>, side: keyof PrintMargins): string | undefined {
  const pair = MARGIN_PAIRS.find(p => p.includes(side))!;
  return pairIssue(issues, pair) ? undefined : issues[`margins.${side}`];
}
