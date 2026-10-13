import { describe, it, expect } from 'vitest';
import type { CoverageField, CoverageGroup, CoverageRow, CoverageType } from '@/shared/api/fieldMapping';
import type { RawSchema } from './fieldMapping';
import { saveGroup, type SchemaIo } from './fieldMappingSave';

const ORG: CoverageType = { id: 'org', name: 'Организация', code: 'Организация', kind: 'Composite' };
const PARTY: CoverageType = { id: 'party', name: 'Сторона', code: 'Сторона', kind: 'Composite' };

function field(key: string, over: Partial<CoverageField> = {}): CoverageField {
  return { key, title: key, type: 'string', declaredIn: ORG, fits: true, tagged: false, ...over };
}

/** Справочник, у которого ИНН назначен полю ПРЕДКА: перенос на своё поле — две записи. */
function inherited(): CoverageGroup {
  const row: CoverageRow = {
    tag: 'org.inn', label: 'ИНН организации', description: '', required: true, withoutIt: '',
    state: 'mapped', fault: 'None', reason: null, key: 'ИНН', kinds: ['string'],
    fields: [field('ИНН', { tagged: true, declaredIn: PARTY }), field('Код')],
  };
  return {
    role: 'role.organization', label: 'Справочник организаций', purpose: '', withoutIt: '',
    state: 'mapped', reason: null, type: ORG, candidates: [], rows: [row],
  };
}

/** Схемы в памяти и журнал записей. `refuse` — тип, запись которого сервер не принимает. */
function stand(schemas: Record<string, RawSchema>, refuse: (typeId: string, call: number) => boolean = () => false) {
  const writes: string[] = [];
  const io: SchemaIo = {
    read: async id => ({ name: id === 'org' ? 'Организация' : 'Сторона', schema: schemas[id] }),
    write: async (id, schema) => {
      writes.push(id);
      if (refuse(id, writes.length)) throw new Error('тип закрыт для правки');
      schemas[id] = schema;
    },
  };
  return { io, writes, schemas };
}

const tagsOf = (schema: RawSchema) =>
  Object.fromEntries((schema.fields as { key: string; tags?: string[] }[]).map(f => [f.key, f.tags ?? []]));

const fresh = () => ({
  party: { fields: [{ key: 'ИНН', type: 'string', tags: ['org.inn'] }] } as RawSchema,
  org: { fields: [{ key: 'Код', type: 'string' }] } as RawSchema,
});

describe('запись группы', () => {
  it('перенос с поля предка: сначала снятие, потом постановка', async () => {
    const s = stand(fresh());

    expect(await saveGroup(inherited(), { 'org.inn': 'Код' }, s.io)).toEqual({ ok: true });

    expect(s.writes).toEqual(['party', 'org']);
    expect(tagsOf(s.schemas.party)).toEqual({ ИНН: [] });
    expect(tagsOf(s.schemas.org)).toEqual({ Код: ['org.inn'] });
  });

  // Вторая запись отказала — первая уже сняла тэг. Оставь мы так, обязательное поле осталось бы
  // без назначения вовсе: хуже и того, что было, и того, что просили.
  it('вторая запись отказала — первая возвращена, назначение прежнее', async () => {
    const s = stand(fresh(), (id, call) => id === 'org' && call === 2);

    const outcome = await saveGroup(inherited(), { 'org.inn': 'Код' }, s.io);

    expect(outcome).toMatchObject({ ok: false, partial: false });
    expect(s.writes).toEqual(['party', 'org', 'party']);
    expect(tagsOf(s.schemas.party)).toEqual({ ИНН: ['org.inn'] });
    expect(tagsOf(s.schemas.org)).toEqual({ Код: [] });
  });

  it('и вернуть не удалось — итог назван частичным', async () => {
    const s = stand(fresh(), (_, call) => call >= 2);

    expect(await saveGroup(inherited(), { 'org.inn': 'Код' }, s.io)).toMatchObject({ ok: false, partial: true });
  });

  // Откат возвращает только свой тэг: поле, которое коллега добавил между записями, остаётся.
  it('откат не трогает то, что в типе изменили тем временем', async () => {
    const s = stand(fresh(), (id, call) => id === 'org' && call === 2);
    const write = s.io.write;
    s.io.write = async (id, schema) => {
      await write(id, schema);
      if (id === 'party')
        s.schemas.party = { ...schema, fields: [...(schema.fields as object[]), { key: 'Новое', type: 'string' }] };
    };

    await saveGroup(inherited(), { 'org.inn': 'Код' }, s.io);

    expect(tagsOf(s.schemas.party)).toEqual({ ИНН: ['org.inn'], Новое: [] });
  });

  // Всё, что клиент видит сам, проверяется ДО первой записи: иначе тэг с предка снялся бы, а
  // поставить его оказалось бы некуда.
  it('поле пропало или сменило вид — отказ до первой записи', async () => {
    const gone = stand({ ...fresh(), org: { fields: [] } });
    const outcome = await saveGroup(inherited(), { 'org.inn': 'Код' }, gone.io);
    expect(outcome).toMatchObject({ ok: false, partial: false });
    expect(String((outcome as { error: Error }).error.message)).toContain('больше нет поля «Код»');
    expect(gone.writes).toEqual([]);

    const complex = stand({ ...fresh(), org: { fields: [{ key: 'Код', type: 'complex' }] } });
    const unfit = await saveGroup(inherited(), { 'org.inn': 'Код' }, complex.io);
    expect(String((unfit as { error: Error }).error.message)).toContain('больше не подходит');
    expect(complex.writes).toEqual([]);
  });
});
