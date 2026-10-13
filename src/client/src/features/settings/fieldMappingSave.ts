import type { CoverageGroup } from '@/shared/api/fieldMapping';
import {
  missingKey, restoreTags, schemaWrites, unfitKey, withTagPlacements,
  type MappingDraft, type RawSchema, type SchemaWrite,
} from './fieldMapping';

/**
 * Запись группы экрана «Сопоставление полей» (issue #1101) — отдельно от экрана и без React, чтобы
 * порядок шагов можно было проверить: он и есть содержание этого файла.
 *
 * Правка идёт адресом редактора типов и кладёт схему ЦЕЛИКОМ, а версии у схемы типа нет. Отсюда три
 * предосторожности, каждая — от своей потери (ревью PR #1318):
 *
 * 1. Схема перечитывается прямо перед записью, и двигается в ней только тэг.
 * 2. Всё, что клиент способен проверить сам (поле на месте, вид подходит), проверяется по ВСЕМ
 *    затронутым типам ДО первой записи.
 * 3. Группа может затронуть два типа — поле объявлено у предка. Это две записи, не транзакция;
 *    если вторая отказала, первая возвращается назад.
 */

/** Чтение и запись схемы типа — то, что экран делает через сеть. */
export interface SchemaIo {
  read: (typeId: string) => Promise<{ name: string; schema: RawSchema }>;
  write: (typeId: string, schema: RawSchema) => Promise<void>;
}

/**
 * Итог записи группы. `partial` — в схемах осталась часть правки: вторая запись отказала, и вернуть
 * первую не удалось. Экран обязан сказать это прямо: назначение сейчас не то, что было, и не то, что
 * просили.
 */
export type SaveOutcome = { ok: true } | { ok: false; partial: boolean; error: unknown };

/** Сначала правки, которые тэг только СНИМАЮТ: сервер считает «одно поле на тэг» по типу вместе с
 *  предками, и постановка раньше снятия была бы отказом. */
function removalsFirst(writes: SchemaWrite[]): SchemaWrite[] {
  const places = (w: SchemaWrite) => Number(w.placements.some(p => p.key !== null));
  return [...writes].sort((a, b) => places(a) - places(b));
}

export async function saveGroup(group: CoverageGroup, draft: MappingDraft, io: SchemaIo): Promise<SaveOutcome> {
  const kinds = Object.fromEntries(group.rows.map(r => [r.tag, r.kinds]));
  const done: { typeId: string; before: RawSchema; tags: string[] }[] = [];

  try {
    const plan = [];
    for (const write of removalsFirst(schemaWrites(group, draft))) {
      const { name, schema } = await io.read(write.typeId);
      // Что делать дальше, эти два отказа не говорят: экран следом перечитается, снимет выбор,
      // которого больше нет, и сам назовёт строку и позовёт выбрать заново.
      const gone = missingKey(schema, write.placements);
      if (gone !== null)
        throw new Error(`В типе «${name}» больше нет поля «${gone}» — тип тем временем изменили.`);
      const unfit = unfitKey(schema, write.placements, kinds);
      if (unfit !== null)
        throw new Error(`Поле «${unfit}» типа «${name}» больше не подходит — его тем временем изменили.`);
      plan.push({ write, schema });
    }

    for (const { write, schema } of plan) {
      const next = withTagPlacements(schema, write.placements);
      if (next === schema) continue;
      await io.write(write.typeId, next);
      done.push({ typeId: write.typeId, before: schema, tags: write.placements.map(p => p.tag) });
    }
    return { ok: true };
  } catch (error) {
    return { ok: false, partial: !(await rolledBack(done, io)), error };
  }
}

/**
 * Вернуть уже записанное — в обратном порядке. Возвращаются только тэги группы, и снова по свежей
 * схеме: остальное в типе могли тем временем править. `false` — вернуть не удалось.
 */
async function rolledBack(done: { typeId: string; before: RawSchema; tags: string[] }[], io: SchemaIo) {
  try {
    for (const step of [...done].reverse()) {
      const { schema } = await io.read(step.typeId);
      const back = restoreTags(schema, step.before, step.tags);
      if (back !== schema) await io.write(step.typeId, back);
    }
    return true;
  } catch {
    return false;
  }
}
