import type {
  PrintFit, PrintMargins, PrintOrientation, PrintPaper, PrintProfile, PrintProfileInput, PrintProfileParams,
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

/** «A4 · книжная · по ширине · поля 5 мм»; разные поля — «поля 5/5/10/10 мм» (верх/низ/лево/право). */
export function profileSummary(p: PrintProfileParams): string {
  const orientation = p.orientation === 'landscape' ? 'альбомная' : 'книжная';
  const fit = p.fit === 'width' ? 'по ширине' : 'не вписывать';
  const { top, bottom, left, right } = p.margins;
  const margins = top === bottom && top === left && top === right ? `${top}` : `${top}/${bottom}/${left}/${right}`;
  return `${p.paper} · ${orientation} · ${fit} · поля ${margins} мм`;
}

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
 * Нехватку места меряем, только когда обе стороны поля годны: иначе «остаётся N мм» считалось бы
 * от числа, которого в поле нет.
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
  const shortfall = (rest: number) =>
    `Слишком широкие поля: для страницы остаётся ${rest} мм. Нужно не меньше ${USEFUL_MIN_MM} мм.`;
  if (mm.left !== undefined && mm.right !== undefined) {
    const rest = page.width - mm.left - mm.right;
    if (rest < USEFUL_MIN_MM) issues['margins.right'] = shortfall(rest);
  }
  if (mm.top !== undefined && mm.bottom !== undefined) {
    const rest = page.height - mm.top - mm.bottom;
    if (rest < USEFUL_MIN_MM) issues['margins.bottom'] = shortfall(rest);
  }
  return issues;
}
