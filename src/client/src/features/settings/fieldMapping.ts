import type { CoverageField, CoverageGroup, CoverageRow } from '@/shared/api/fieldMapping';
import { tagCode } from '@/shared/api/tags';
import { TYPE_LABELS } from './schemaConstants';

/**
 * Логика экрана «Сопоставление полей» (issue #1101) — отдельно от экрана, чтобы её можно было
 * проверить.
 *
 * Запись идёт тем же адресом, что из редактора типа: экран берёт схему типа, двигает в ней ОДИН тэг
 * и кладёт обратно. Всё остальное в схеме остаётся как было — поэтому правка работает над сырым
 * JSON, а не над разобранными полями редактора: разбор и сборка обратно теряли бы то, чего этот
 * экран не знает.
 */

/** Сырая схема типа — как её отдал сервер. */
export type RawSchema = Record<string, unknown>;

/** Черновик группы: тэг потребности → ключ выбранного поля; `null` — снять назначение. */
export type MappingDraft = Record<string, string | null>;

/** Значение пункта «Не назначено»: пустую строку Radix в пунктах списка запрещает. */
export const NOT_ASSIGNED = '__none__';

/** Куда поставить тэг в схеме одного типа: ключ поля либо `null` — у этого типа тэг только снять. */
export interface TagPlacement {
  tag: string;
  key: string | null;
}

/** Правка схемы одного типа. Группа обычно даёт одну; две — когда поле объявлено у предка. */
export interface SchemaWrite {
  typeId: string;
  typeName: string;
  placements: TagPlacement[];
}

/**
 * Набранное в строке — единственное место, где читают черновик.
 *
 * Выбор действителен, пока поле есть в списке строки. Список перечитывается, и поле тем временем
 * могли удалить; оставшийся в черновике ключ экран считал бы правкой, а запись — снятием тэга:
 * поставить его некуда, снять с прежнего есть откуда. Повторное «Сохранить» после отказа «поля
 * больше нет» молча снимало обязательное назначение и отвечало «сохранено» (проверка правок PR
 * #1318). Тот же приём, что у выбранного типа справочника: выбор жив, пока есть из чего выбирать.
 *
 * `undefined` — строку не трогали; `null` — «Не назначено».
 */
function chosenKey(row: CoverageRow, draft: MappingDraft): string | null | undefined {
  const chosen = draft[row.tag];
  return chosen != null && !row.fields.some(f => f.key === chosen) ? undefined : chosen;
}

/**
 * Строки, в которых выбор отличается от действующего. Действующим считается только сопоставленное:
 * у нарушенной строки (два поля с тэгом) любой выбор — правка, даже если он совпал с одним из двух.
 */
export function changedRows(group: CoverageGroup, draft: MappingDraft): CoverageRow[] {
  return group.rows.filter(row => {
    const chosen = chosenKey(row, draft);
    if (chosen === undefined) return false;
    return row.state === 'mapped' ? chosen !== row.key : chosen !== null || row.fields.some(f => f.tagged);
  });
}

/**
 * Правки схем по черновику — по одной на тип.
 *
 * Затронуты тип, где объявлено выбранное поле, и каждый тип, где тэг стоит сейчас: с прежних мест
 * он обязан сняться тем же сохранением, иначе вышло бы «назначено дважды».
 */
export function schemaWrites(group: CoverageGroup, draft: MappingDraft): SchemaWrite[] {
  const writes = new Map<string, SchemaWrite>();
  for (const row of changedRows(group, draft)) {
    const chosen = chosenKey(row, draft) ?? null;
    const target = chosen === null ? undefined : row.fields.find(f => f.key === chosen);
    const touched = [...row.fields.filter(f => f.tagged), ...(target ? [target] : [])];
    for (const field of touched) {
      const { id, name } = field.declaredIn;
      const write = writes.get(id) ?? { typeId: id, typeName: name, placements: [] };
      if (!write.placements.some(p => p.tag === row.tag))
        write.placements.push({ tag: row.tag, key: target?.declaredIn.id === id ? chosen : null });
      writes.set(id, write);
    }
  }
  return [...writes.values()];
}

/** Собственные поля схемы — как лежат. */
function ownFields(schema: RawSchema): Record<string, unknown>[] {
  return Array.isArray(schema.fields)
    ? schema.fields.filter((f): f is Record<string, unknown> => !!f && typeof f === 'object')
    : [];
}

/**
 * Ключ, которого в свежей схеме типа уже нет, — или `null`, если все на месте. Список полей на
 * экране мог устареть: тип тем временем правили в редакторе. Записать тэг несуществующему полю
 * значило бы молча снять назначение.
 */
export function missingKey(schema: RawSchema, placements: TagPlacement[]): string | null {
  const keys = new Set(ownFields(schema).map(f => f.key));
  return placements.find(p => p.key !== null && !keys.has(p.key))?.key ?? null;
}

/**
 * Ключ поля, которое в свежей схеме тэгу уже не подходит, — или `null`. Вид поля на экране мог
 * устареть так же, как список: полю тем временем сменили вид или сделали его расчётным. Поставить
 * тэг такому полю значило бы ответить «сохранено» и тут же показать «нарушено» — а если тэг
 * переносили, с прежнего, годного поля он к этому времени уже снят (ревью PR #1318).
 *
 * @param kinds виды, которые допускает каждый тэг; пусто — любой.
 */
export function unfitKey(
  schema: RawSchema, placements: TagPlacement[], kinds: Record<string, string[]>,
): string | null {
  const byKey = new Map(ownFields(schema).map(f => [f.key, f]));
  for (const { tag, key } of placements) {
    const field = key === null ? undefined : byKey.get(key);
    if (!field) continue;
    const allowed = kinds[tag] ?? [];
    // Поле без вида сервер читает строкой — и здесь так же: иначе годное поле старой схемы
    // получило бы отказ, которого сервер не дал бы.
    const kind = typeof field.type === 'string' ? field.type : 'string';
    if (field.computed === true || (allowed.length > 0 && !allowed.includes(kind))) return key;
  }
  return null;
}

/**
 * Схема, в которой названные тэги стоят там же, где стояли в `before`, — откат записи, за которой
 * не прошла следующая. Трогает только эти тэги: остальное в свежей схеме могли тем временем
 * править. Тот же объект, если возвращать нечего.
 */
export function restoreTags(schema: RawSchema, before: RawSchema, tags: string[]): RawSchema {
  const had = new Map(ownFields(before).map(f => [f.key, Array.isArray(f.tags) ? (f.tags as string[]) : []]));
  let changed = false;
  const fields = ownFields(schema).map(field => {
    const now = Array.isArray(field.tags) ? (field.tags as string[]) : [];
    const was = had.get(field.key) ?? [];
    let next = now;
    for (const tag of tags) {
      const old = was.find(t => tagCode(t) === tag);
      const has = next.some(t => tagCode(t) === tag);
      if (old !== undefined && !has) next = [...next, old];
      else if (old === undefined && has) next = next.filter(t => tagCode(t) !== tag);
    }
    if (next === now) return field;
    changed = true;
    return { ...field, tags: next };
  });
  return changed ? { ...schema, fields } : schema;
}

/**
 * Схема с переставленными тэгами. Возвращает ТОТ ЖЕ объект, если менять нечего, — так вызывающий
 * отличает правку от её отсутствия и не шлёт пустое сохранение (оно легло бы в журнал действий).
 *
 * Запись тэга с параметром («код:3») снимается целиком: сравнение идёт по коду.
 */
export function withTagPlacements(schema: RawSchema, placements: TagPlacement[]): RawSchema {
  let changed = false;
  const fields = ownFields(schema).map(field => {
    const before = Array.isArray(field.tags) ? (field.tags as string[]) : [];
    let tags = before;
    for (const { tag, key } of placements) {
      const has = tags.some(t => tagCode(t) === tag);
      if (field.key === key && !has) tags = [...tags, tag];
      else if (field.key !== key && has) tags = tags.filter(t => tagCode(t) !== tag);
    }
    if (tags === before) return field;
    changed = true;
    return { ...field, tags };
  });
  return changed ? { ...schema, fields } : schema;
}

/** Схема с тэгом типа — назначение справочника. Тот же объект, если тэг уже стоит. */
export function withTypeTag(schema: RawSchema, tag: string): RawSchema {
  const tags = Array.isArray(schema.tags) ? (schema.tags as string[]) : [];
  return tags.some(t => tagCode(t) === tag) ? schema : { ...schema, tags: [...tags, tag] };
}

/** Вид поля словами: «Строка». Незнакомый вид показывается как есть — это лучше, чем пусто. */
export function kindLabel(type: string): string {
  return TYPE_LABELS[type] ?? type;
}

/**
 * Подпись поля в списке: «ИНН · Строка». Ключ дописывается, только когда в типе два поля с одним
 * названием — иначе их не различить; в остальных случаях ключ человеку не нужен.
 */
export function fieldLabel(field: CoverageField, all: CoverageField[]): string {
  const twin = all.some(f => f !== field && f.title === field.title);
  return `${field.title} · ${kindLabel(field.type)}` + (twin ? ` · ключ «${field.key}»` : '');
}

/**
 * Поля, которые стоит показать в списке строки: подходящие по виду и те, что несут тэг сейчас —
 * даже негодные (их выбрать нельзя, но назвать надо). Помеченные идут первыми.
 */
export function listedFields(row: CoverageRow): CoverageField[] {
  return [...row.fields.filter(f => f.tagged), ...row.fields.filter(f => !f.tagged && f.fits)];
}

/** Что стоит в списке строки: набранное, а если не трогали — действующее назначение. */
export function selectedKey(row: CoverageRow, draft: MappingDraft): string | undefined {
  const chosen = chosenKey(row, draft);
  if (chosen !== undefined) return chosen ?? NOT_ASSIGNED;
  if (row.key !== null) return row.key;
  // Одно негодное поле с тэгом — показываем его, с пометкой: пустой список скрыл бы, что тэг стоит.
  const tagged = row.fields.filter(f => f.tagged);
  if (tagged.length === 1) return tagged[0].key;
  // Тэг на двух полях — не «не назначено», и у необязательной строки тоже: выбранный пункт «Не
  // назначено» нельзя было бы выбрать ещё раз, и снять тэг с обоих было бы нечем (ревью PR #1318).
  return row.required || tagged.length > 1 ? undefined : NOT_ASSIGNED;
}

/**
 * Причина сервера — законченной фразой: с заглавной буквы и с точкой. Сервер отдаёт её серединой
 * предложения, а экран ставит между своими. Не `sentence` из настроек модуля: та точку не ставит —
 * её текст стоит под полем один.
 */
export function fullSentence(text: string): string {
  const t = text.trim();
  return t && t.charAt(0).toUpperCase() + t.slice(1) + (/[.!?]$/.test(t) ? '' : '.');
}

/** Виды, которые тэг допускает, словами — для «в типе нет поля вида …». */
export function kindsText(row: CoverageRow): string {
  return row.kinds.map(k => `«${kindLabel(k).toLowerCase()}»`).join(', ');
}
