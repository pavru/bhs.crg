import { describe, it, expect } from 'vitest';
import { toCamelKey, nextAutoKey, nextSavedKey, schemaToJson } from './schemaConstants';
import { FIELD_UID, withFieldUid, parseSchemaFields, type SchemaField } from '@/shared/api/schema';

describe('nextAutoKey (issue #355)', () => {
  it('новое поле: пустой ключ следует за именем', () => {
    expect(nextAutoKey('', '', 'Номер', true)).toBe('Номер');
  });
  it('новое поле: ключ достраивается по мере ввода имени (пока совпадает с камелизацией старого)', () => {
    expect(nextAutoKey('Номер', 'Номер', 'Номер документа', true)).toBe('НомерДокумента');
  });
  it('новое поле с РУЧНО заданным ключом: не трогаем при переименовании', () => {
    expect(nextAutoKey('myKey', 'Номер', 'Номер документа', true)).toBe('myKey');
  });
  it('СОХРАНЁННОЕ поле: ключ заморожен, переименование его не меняет', () => {
    expect(nextAutoKey('Номер', 'Номер', 'Новое имя', false)).toBe('Номер');
  });
});

describe('toCamelKey', () => {
  it('склеивает слова, каждое с заглавной буквы', () => {
    expect(toCamelKey('Номер документа')).toBe('НомерДокумента');
  });

  it('схлопывает несколько пробелов/пунктуацию в один разделитель', () => {
    expect(toCamelKey('Номер  документа, №1')).toBe('НомерДокумента1');
  });

  it('оставляет одно слово как есть (кроме регистра первой буквы)', () => {
    expect(toCamelKey('артикул')).toBe('Артикул');
  });

  it('пустая строка → пустой ключ', () => {
    expect(toCamelKey('')).toBe('');
  });

  it('обрезает начальные/конечные разделители', () => {
    expect(toCamelKey('  Дата  ')).toBe('Дата');
  });
});

/**
 * База сравнения ключа поля (issue #527). Здесь воспроизводится ЖИЗНЬ карточки поля: база двигается
 * только при смене набора сохранённых ключей (то есть после записи схемы), а печать в поле ключа её
 * не трогает — иначе предупреждение о дрейфе данных гаснет ровно тогда, когда оно нужно.
 */
describe('nextSavedKey (issue #527)', () => {
  it('новое поле не считается сохранённым, пока его ключа нет в схеме', () => {
    const persisted = new Set(['Печать']);
    expect(nextSavedKey(null, '', persisted)).toBeNull();
    expect(nextSavedKey(null, 'Подпись', persisted)).toBeNull();
  });

  it('после сохранения нового поля база встаёт на его ключ — предупреждения быть не должно', () => {
    // до сохранения
    let base = nextSavedKey(null, 'Подпись', new Set(['Печать']));
    expect(base).toBeNull();
    // сохранили: набор сохранённых ключей пополнился
    base = nextSavedKey(base, 'Подпись', new Set(['Печать', 'Подпись']), new Set(['Печать']));
    expect(base).toBe('Подпись');   // прежде здесь оставалась пустая строка → «ключ изменён с «»»
  });

  it('печать в поле ключа сохранённого поля базу не сдвигает', () => {
    const persisted = new Set(['Печать']);
    let base: string | null = 'Печать';
    for (const typed of ['Печат', 'Печа', 'Ш', 'Шт', 'Штамп']) {
      base = nextSavedKey(base, typed, persisted);
      expect(base).toBe('Печать');   // ← отсюда берётся `from` для переноса данных
    }
  });

  it('после сохранения переименования база переезжает на новый ключ', () => {
    const base = nextSavedKey('Печать', 'Штамп', new Set(['Штамп']), new Set(['Печать']));
    expect(base).toBe('Штамп');
  });

  it('набранный дубликат чужого сохранённого ключа базу не крадёт', () => {
    // Пользователь набрал ключ уже существующего поля. Ключ был сохранён и ДО перезагрузки схемы —
    // значит записали не нас. Переехав, карточка заморозила бы ввод посреди правки.
    const persisted = new Set(['Печать', 'Подпись']);
    expect(nextSavedKey('Печать', 'Подпись', persisted, persisted)).toBe('Печать');
  });

  it('НОВОЕ поле тоже не присваивает себе чужой сохранённый ключ', () => {
    // Самое опасное: у нового поля базы ещё нет, и без сравнения с прежним набором ключей карточка
    // считала себя сохранённой под чужим ключом. Дальше правка ключа записывала перенос ОТ чужого
    // поля, и подтверждение диалога перенесло бы его реальные данные (MigrateFieldKeyCommand).
    const persisted = new Set(['Дата', 'Печать']);
    expect(nextSavedKey(null, 'Дата', persisted, persisted)).toBeNull();
  });

  it('при монтировании (прежний набор неизвестен) сохранённый ключ опознаётся сразу', () => {
    expect(nextSavedKey(null, 'Печать', new Set(['Печать']))).toBe('Печать');
  });

  it('без набора сохранённых ключей (форма создания типа) поле всегда новое', () => {
    expect(nextSavedKey(null, 'Подпись', undefined)).toBeNull();
  });
});

/**
 * Личность поля в редакторе (issue #527). Держится на символьном ключе именно ради этих двух
 * свойств: её не видно в сохраняемой схеме и она переживает правку поля.
 */
describe('FIELD_UID', () => {
  it('в сохраняемую схему не протекает', () => {
    const fields = parseSchemaFields({ fields: [{ key: 'Печать', title: 'Печать', type: 'string' }] });
    expect(fields[0][FIELD_UID]).toBeTruthy();
    const json = schemaToJson(fields, [], {});
    expect(json).not.toContain('fieldUid');
    expect(Object.keys(JSON.parse(json).fields[0])).toEqual(
      expect.not.arrayContaining([expect.stringContaining('uid')]));
  });

  it('переживает правку поля (расширение объекта)', () => {
    const field: SchemaField = withFieldUid({ key: 'Печать', title: 'Печать', type: 'string', required: false });
    const patched = { ...field, key: 'Штамп' };
    expect(patched[FIELD_UID]).toBe(field[FIELD_UID]);
  });

  it('уже помеченному полю личность не переписывает', () => {
    const field: SchemaField = withFieldUid({ key: 'a', title: 'a', type: 'string', required: false });
    expect(withFieldUid(field)).toBe(field);
  });

  it('разным полям — разная личность', () => {
    const a: SchemaField = withFieldUid({ key: 'a', title: 'a', type: 'string', required: false });
    const b: SchemaField = withFieldUid({ key: 'b', title: 'b', type: 'string', required: false });
    expect(a[FIELD_UID]).not.toBe(b[FIELD_UID]);
  });
});
