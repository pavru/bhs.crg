import { describe, it, expect } from 'vitest';
import { ROW_UID, rowKey, rowUid, withRowUid, withRowUids } from './rowIdentity';

/**
 * Личность строки списка (issue #517). Держится на символьном ключе ради трёх свойств, каждое из
 * которых здесь и закреплено: невидима для сериализации, переживает правку строки и не меняется
 * при повторном проставлении.
 */
describe('rowIdentity', () => {
  it('в сериализацию не протекает', () => {
    const row = withRowUid({ code: 'П', label: 'Проект' });
    expect(rowUid(row)).toBeTruthy();
    expect(JSON.stringify(row)).toBe('{"code":"П","label":"Проект"}');
    expect(JSON.stringify([row])).not.toContain('rowUid');
  });

  it('переживает правку строки (расширение объекта)', () => {
    const row = withRowUid({ code: 'П', label: 'Проект' });
    // Именно так редакторы правят строку — и именно поэтому WeakMap по объекту не годился бы:
    // каждое нажатие клавиши даёт НОВЫЙ объект.
    const edited = { ...row, label: 'Проектная' };
    expect(rowUid(edited)).toBe(rowUid(row));
  });

  it('уже помеченной строке личность не переписывает', () => {
    const row = withRowUid({ code: 'П' });
    expect(withRowUid(row)).toBe(row);
    expect(withRowUids([row])[0]).toBe(row);
  });

  it('разным строкам — разные личности', () => {
    const [a, b] = withRowUids([{ code: 'П' }, { code: 'Р' }]);
    expect(rowUid(a)).not.toBe(rowUid(b));
  });

  it('перестановка личности не меняет — на этом и держится ключ React', () => {
    const rows = withRowUids([{ code: 'П' }, { code: 'Р' }, { code: 'И' }]);
    const moved = [rows[1], rows[0], rows[2]];
    expect(moved.map(r => rowUid(r))).toEqual([rowUid(rows[1]), rowUid(rows[0]), rowUid(rows[2])]);
    // ключ едет вместе со строкой, а не остаётся за позицией
    expect(rowKey(moved[0], 0)).toBe(rowUid(rows[1]));
  });

  it('строка без личности ключуется позицией — старое поведение сохраняется', () => {
    expect(rowKey({ code: 'П' }, 3)).toBe(3);
    expect(rowUid({ code: 'П' })).toBeUndefined();
  });

  it('личность лежит именно под ROW_UID', () => {
    const row = withRowUid({ code: 'П' });
    expect((row as { [ROW_UID]?: string })[ROW_UID]).toBe(rowUid(row));
  });
});
