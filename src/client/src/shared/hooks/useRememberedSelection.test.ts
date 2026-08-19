import { describe, it, expect } from 'vitest';
import { resolveRemembered } from './useRememberedSelection';

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
