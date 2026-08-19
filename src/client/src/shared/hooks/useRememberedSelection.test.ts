import { describe, it, expect } from 'vitest';
import { resolveRemembered, parseStored } from './useRememberedSelection';

const KEYS = ['mode', 'type'] as const;

describe('resolveRemembered', () => {
  it('без адреса и памяти отдаёт пустые значения — прежнее поведение страницы', () => {
    expect(resolveRemembered(KEYS, {}, null)).toEqual({ mode: '', type: '' });
  });

  it('память отдаётся целиком, когда адрес пуст', () => {
    expect(resolveRemembered(KEYS, {}, { mode: 'enum', type: 'e1' }))
      .toEqual({ mode: 'enum', type: 'e1' });
  });

  it('адрес перекрывает память полностью, а не по ключам', () => {
    // Пришли по ссылке `?mode=primitive`: тип из прошлой сессии подмешивать нельзя — он относился
    // к другому режиму, и получилась бы пара, которой пользователь никогда не выбирал.
    expect(resolveRemembered(KEYS, { mode: 'primitive' }, { mode: 'enum', type: 'e1' }))
      .toEqual({ mode: 'primitive', type: '' });
  });

  it('пустые значения в адресе за источник не считаются', () => {
    expect(resolveRemembered(KEYS, { mode: '', type: '' }, { mode: 'enum', type: 'e1' }))
      .toEqual({ mode: 'enum', type: 'e1' });
  });

  it('ключи, которых нет ни там ни там, становятся пустыми', () => {
    expect(resolveRemembered(KEYS, { type: 't1' }, null)).toEqual({ mode: '', type: 't1' });
  });
});

describe('parseStored', () => {
  it('понимает голый id — формат первой реализации (#778)', () => {
    // JSON он не является: разбирать его как JSON бесполезно, ветка совместимости на этом
    // и была недостижимой — вчерашний выбор молча терялся.
    expect(parseStored('3fa85f64-5717-4562-b3fc-2c963f66afa6', KEYS))
      .toEqual({ mode: '3fa85f64-5717-4562-b3fc-2c963f66afa6' });
  });

  it('понимает объект', () => {
    expect(parseStored('{"mode":"enum","type":"e1"}', KEYS)).toEqual({ mode: 'enum', type: 'e1' });
  });

  it('пустое и битое значение — как отсутствие памяти', () => {
    expect(parseStored(null, KEYS)).toBeNull();
    expect(parseStored('', KEYS)).toBeNull();
    expect(parseStored('{битый', KEYS)).toBeNull();
  });
});
