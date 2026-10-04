import { describe, expect, it } from 'vitest';
import { breakdownTotals, wholeLabel } from './tableCells';

describe('суммы расшифровки строки', () => {
  it('без сужающего отбора — только строка целиком', () => {
    expect(breakdownTotals({ narrowed: false, totals: [{ column: 'Доля', whole: 100000, named: null }] }, 'счёт'))
      .toEqual([{ label: 'Счёт целиком', value: (100000).toLocaleString('ru-RU') }]);
  });

  it('под сужающим отбором «В отборе» есть всегда — и пустое названо, а не опущено', () => {
    const rows = breakdownTotals({ narrowed: true, totals: [{ column: 'Доля', whole: 100000, named: null }] }, 'счёт');
    expect(rows.map(r => r.label)).toEqual(['Счёт целиком', 'В отборе']);
    expect(rows[1].value).toBe('—');
  });

  it('закрытая колонка итогов не даёт — сервер их не присылает', () => {
    expect(breakdownTotals({ narrowed: true, totals: [] }, 'счёт')).toEqual([]);
  });

  it('зерно таблицы называет, что сложено', () => {
    expect(wholeLabel('строка счёта')).toBe('Строка счёта целиком');
    expect(wholeLabel('')).toBe('Строка целиком');
  });
});
