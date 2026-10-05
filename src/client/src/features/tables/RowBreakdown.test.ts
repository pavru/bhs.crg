import { describe, expect, it } from 'vitest';
import { breakdownTotals, wholeLabel } from './tableCells';

describe('суммы расшифровки строки', () => {
  it('без сужающего отбора — только строка целиком', () => {
    expect(breakdownTotals({ narrowed: false, totals: [{ column: 'Доля', whole: 100000, named: null }] }, 'счёт'))
      .toEqual([{ key: 'Доля:whole', label: 'Счёт целиком', value: (100000).toLocaleString('ru-RU') }]);
  });

  it('под сужающим отбором «В отборе» есть всегда — и пустое названо, а не опущено', () => {
    const rows = breakdownTotals({ narrowed: true, totals: [{ column: 'Доля', whole: 100000, named: null }] }, 'счёт');
    expect(rows.map(r => r.label)).toEqual(['Счёт целиком', 'В отборе']);
    expect(rows[1].value).toBe('—');
  });

  it('закрытая колонка итогов не даёт — сервер их не присылает', () => {
    expect(breakdownTotals({ narrowed: true, totals: [] }, 'счёт')).toEqual([]);
  });

  it('две суммы называют каждая свою колонку — и ключи у строк разные', () => {
    const rows = breakdownTotals({
      narrowed: true,
      columns: [
        { key: 'Доля', label: 'Доля', kind: 'number', unavailable: null, reason: null },
        { key: 'НДС', label: 'В том числе НДС', kind: 'number', unavailable: null, reason: null },
      ],
      totals: [{ column: 'Доля', whole: 100, named: 40 }, { column: 'НДС', whole: 20, named: 8 }],
    }, 'счёт');
    expect(rows.map(r => r.label)).toEqual([
      'Счёт целиком · Доля', 'В отборе · Доля', 'Счёт целиком · В том числе НДС', 'В отборе · В том числе НДС',
    ]);
    expect(new Set(rows.map(r => r.key)).size).toBe(4);
  });

  it('зерно таблицы называет, что сложено', () => {
    expect(wholeLabel('строка счёта')).toBe('Строка счёта целиком');
    expect(wholeLabel('')).toBe('Строка целиком');
  });
});
