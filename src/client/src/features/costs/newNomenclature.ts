import type { IntakeBody, IntakeField, IntakeKind } from '@/shared/api/nomenclatureIntake';

/**
 * Новая позиция номенклатуры из строки счёта (задача C3, issue #1079) — то, что можно проверить без
 * окна: что подставить из строки, о чём спросить сервер и чего не хватает до создания.
 */

/** Слова строки счёта — то, с чем человек открыл окно. */
export interface LineWords {
  name?: string | null;
  code?: string | null;
  unit?: string | null;
}

export interface IntakeDraft {
  values: Record<string, string>;
  refs: Record<string, string>;
}

const text = (value: string | null | undefined) => (value ?? '').trim();

/** «шт.» и «ШТ» — одна единица: так же её сравнивает ядро. */
const unitKey = (value: string | null | undefined) => text(value).replace(/[.\s]+$/, '').toLowerCase();

/** Поле названия — первое поле ключа: им сервер называет запись. */
export function namingField(kind: IntakeKind): IntakeField | null {
  return kind.fields.find(f => f.identity && f.options === null) ?? null;
}

/**
 * Поле артикула. Ключей полей ядро не знает (схему ведёт человек), поэтому поле узнаётся по
 * названию — и только ради ПОДСТАНОВКИ: не узнали — человек впишет сам, ничего не сломано.
 */
export function articleField(kind: IntakeKind): IntakeField | null {
  const naming = namingField(kind);
  return kind.fields.find(f => f.options === null && f !== naming && /артикул/i.test(`${f.key} ${f.title}`)) ?? null;
}

/**
 * Вид по умолчанию: единственный, который отсюда можно завести. Из двух годных выбирает человек —
 * угаданный «Материал» вместо «Кабеля» лёг бы в справочник молча.
 */
export function defaultKind(kinds: IntakeKind[]): IntakeKind | null {
  const fit = kinds.filter(k => k.refusals.length === 0);
  return fit.length === 1 ? fit[0] : null;
}

/** Что подставить из строки счёта: наименование, артикул и единицу — если единица есть в справочнике. */
export function prefill(kind: IntakeKind, from: LineWords): IntakeDraft {
  const draft: IntakeDraft = { values: {}, refs: {} };
  const naming = namingField(kind);
  if (naming && text(from.name)) draft.values[naming.key] = text(from.name);
  const article = articleField(kind);
  if (article && text(from.code)) draft.values[article.key] = text(from.code);

  const wanted = unitKey(from.unit);
  for (const field of kind.fields) {
    if (field.options === null) continue;
    const same = wanted ? field.options.filter(o => unitKey(o.name) === wanted) : [];
    // Единственная запись в справочнике — выбирать не из чего, и требовать выбора незачем.
    if (same.length === 1) draft.refs[field.key] = same[0].id;
    else if (field.options.length === 1 && field.required) draft.refs[field.key] = field.options[0].id;
  }
  return draft;
}

/**
 * Подсказка под подставленным полем. Слова поставщика — не название для справочника, и код в счёте
 * бывает внутренним кодом поставщика; подставленное без оговорки сохранят не читая.
 */
export function prefillHint(kind: IntakeKind, field: IntakeField, from: LineWords): string | null {
  if (field === namingField(kind) && text(from.name))
    return 'Из счёта — это слова поставщика. Назовите позицию так, как она должна стоять в справочнике.';
  if (field === articleField(kind) && text(from.code))
    return 'Из счёта. Если это внутренний код поставщика, а не артикул изделия, — сотрите.';
  return null;
}

/** Вопрос о похожих: только поля ключа. `null` — спрашивать не о чем: названия ещё нет. */
export function similarBody(kind: IntakeKind, values: Record<string, string>): IntakeBody | null {
  const naming = namingField(kind);
  if (!naming || !text(values[naming.key])) return null;
  const asked: Record<string, string> = {};
  for (const field of kind.fields)
    if (field.identity && field.options === null && text(values[field.key])) asked[field.key] = text(values[field.key]);
  return { typeId: kind.typeId, values: asked };
}

/** Тело создания: пустые поля не едут — пустая строка в справочнике не «значение». */
export function createBody(kind: IntakeKind, draft: IntakeDraft): IntakeBody {
  const values: Record<string, string> = {};
  const refs: Record<string, string> = {};
  for (const field of kind.fields) {
    if (field.options === null) { if (text(draft.values[field.key])) values[field.key] = text(draft.values[field.key]); }
    else if (draft.refs[field.key]) refs[field.key] = draft.refs[field.key];
  }
  return { typeId: kind.typeId, values, refs };
}

/** Чего не хватает до создания — названиями полей. Название позиции обязательно всегда. */
export function missingFields(kind: IntakeKind, draft: IntakeDraft): string[] {
  const naming = namingField(kind);
  return kind.fields
    .filter(f => f.required || f === naming)
    .filter(f => f.options === null ? !text(draft.values[f.key]) : !draft.refs[f.key])
    .map(f => f.title);
}
