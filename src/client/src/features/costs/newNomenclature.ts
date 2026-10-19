import {
  NOMENCLATURE_ARTICLE, NOMENCLATURE_UNIT, type IntakeBody, type IntakeField, type IntakeKind,
} from '@/shared/api/nomenclatureIntake';
import type { NomenclatureCard } from '@/shared/api/nomenclatureCard';
import type { CompareRow } from './nomenclatureCardView';

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

/** Набранное без краевых пробелов; `null` и `undefined` — пустая строка. */
export const trimText = (value: string | null | undefined) => (value ?? '').trim();

/** «шт.» и «ШТ» — одна единица: так же её сравнивает ядро. */
const unitKey = (value: string | null | undefined) => trimText(value).replace(/[.\s]+$/, '').toLowerCase();

/** Поле названия — первое поле ключа: им сервер называет запись. */
export function namingField(kind: IntakeKind): IntakeField | null {
  return kind.fields.find(f => f.identity && f.options === null) ?? null;
}

/** Тэг сопоставлен в справочнике — хоть бы и полю, которого окно не спрашивает. */
const isMapped = (kind: IntakeKind, tag: string) => (kind.mapped ?? []).includes(tag);

/**
 * Поле артикула. Сопоставлено тэгом (issue #1512) — оно, и только оно; окно его не спрашивает — не
 * подставляется никуда: угаданное по названию было бы не тем полем, что размечено. Не сопоставлено —
 * узнаётся по названию, как до тэга, и только ради ПОДСТАНОВКИ: не узнали — человек впишет сам, ничего
 * не сломано, а неразмеченный справочник подставлять не перестал.
 */
export function articleField(kind: IntakeKind): IntakeField | null {
  if (isMapped(kind, NOMENCLATURE_ARTICLE)) {
    const tagged = kind.fields.find(f => f.tag === NOMENCLATURE_ARTICLE);
    return tagged && tagged.options === null ? tagged : null;
  }
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
  if (naming && trimText(from.name)) draft.values[naming.key] = trimText(from.name);
  const article = articleField(kind);
  if (article && trimText(from.code)) draft.values[article.key] = trimText(from.code);

  const wanted = unitKey(from.unit);
  // Поле единицы сопоставлено тэгом — единица строки ложится только в него, а окно его не спрашивает —
  // никуда: запись «шт» нашлась бы и в другом поле выбора, где она значит не единицу.
  const unitMapped = isMapped(kind, NOMENCLATURE_UNIT);
  for (const field of kind.fields) {
    if (field.options === null) continue;
    const forUnit = !unitMapped || field.tag === NOMENCLATURE_UNIT;
    const same = wanted && forUnit ? field.options.filter(o => unitKey(o.name) === wanted) : [];
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
  if (field === namingField(kind) && trimText(from.name))
    return 'Из счёта — это слова поставщика. Назовите позицию так, как она должна стоять в справочнике.';
  if (field === articleField(kind) && trimText(from.code))
    return 'Из счёта. Если это внутренний код поставщика, а не артикул изделия, — сотрите.';
  return null;
}

/** Вопрос о похожих: только поля ключа. `null` — спрашивать не о чем: названия ещё нет. */
export function similarBody(kind: IntakeKind, values: Record<string, string>): IntakeBody | null {
  const naming = namingField(kind);
  if (!naming || !trimText(values[naming.key])) return null;
  const asked: Record<string, string> = {};
  for (const field of kind.fields)
    if (field.identity && field.options === null && trimText(values[field.key])) asked[field.key] = trimText(values[field.key]);
  return { typeId: kind.typeId, values: asked };
}

/** Тело создания: пустые поля не едут — пустая строка в справочнике не «значение». */
export function createBody(kind: IntakeKind, draft: IntakeDraft): IntakeBody {
  const values: Record<string, string> = {};
  const refs: Record<string, string> = {};
  for (const field of kind.fields) {
    if (field.options === null) { if (trimText(draft.values[field.key])) values[field.key] = trimText(draft.values[field.key]); }
    else if (draft.refs[field.key]) refs[field.key] = draft.refs[field.key];
  }
  return { typeId: kind.typeId, values, refs };
}

/** Чего не хватает до создания — названиями полей. Название позиции обязательно всегда. */
export function missingFields(kind: IntakeKind, draft: IntakeDraft): string[] {
  const naming = namingField(kind);
  return kind.fields
    .filter(f => f.required || f === naming)
    .filter(f => f.options === null ? !trimText(draft.values[f.key]) : !draft.refs[f.key])
    .map(f => f.title);
}

/** «EKF » и «ekf» — одно и то же значение: регистр и лишние пробелы набранного сверку не ломают. */
const sameText = (a: string, b: string) =>
  a.replace(/\s+/g, ' ').trim().toLowerCase() === b.replace(/\s+/g, ' ').trim().toLowerCase();

/**
 * Сравнение «Набрано ↔ в справочнике» в карточке шага «Новая позиция» (issue #1517) — по полям КЛЮЧА:
 * они решают, та ли это позиция. По ключу поля, а не по заголовку: заголовки у полей бывают одинаковые.
 * Ненабранное не сравнивается — «отличается» про пустое поле было бы неправдой.
 *
 * Поле названия пустым у позиции не бывает: им сервер называет запись. Пусто оно в данных у записей,
 * заведённых одним именем, — тогда значение поля и есть имя записи, а «не заполнено» про позицию с
 * именем было бы неправдой.
 *
 * Не прочитанное сервером — не «не заполнено» (`unreadable`): сказано, что сверить не удалось. Иначе
 * лежащая позиция читалась бы отличной от набранной, и её завели бы вторым экземпляром.
 */
export function typedVsCard(
  kind: IntakeKind, draft: IntakeDraft, card: Pick<NomenclatureCard, 'lines' | 'name'>,
): CompareRow[] {
  const rows: CompareRow[] = [];
  const naming = namingField(kind);
  for (const field of kind.fields) {
    if (!field.identity) continue;
    const typed = field.options === null
      ? trimText(draft.values[field.key])
      : trimText(field.options.find(o => o.id === draft.refs[field.key])?.name);
    if (!typed) continue;
    const line = card.lines.find(l => l.key === field.key);
    const there = line?.text ?? (field === naming ? card.name : null);
    const row = { key: field.key, label: field.title };
    if (!there && line?.unreadable)
      rows.push({ ...row, text: 'в справочнике не прочитано — сверить не удалось', tone: 'plain' });
    else if (!there)
      rows.push({ ...row, text: 'в справочнике не заполнено', tone: 'plain' });
    else if (sameText(typed, there))
      rows.push({ ...row, text: `совпадает: ${there}`, tone: 'ok' });
    else
      rows.push({ ...row, text: `отличается — набрано «${typed}»`, tone: 'differs' });
  }
  return rows;
}
