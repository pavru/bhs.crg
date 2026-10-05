import { describe, expect, it } from 'vitest';
import type { ClosingLine, ClosingSection } from '@/shared/api/periods';
import { figure, goneLines, goneSections, localLink, unfinishedSummary, visiblyChanged, wasText } from './closing';

const line = (key: string, count: number, counted: string, amount: number | null = null): ClosingLine =>
  ({ key, text: key, count, counted, amount, note: null, link: null });

const section = (module: string, unfinished: ClosingLine[], frozen: ClosingLine[] = []): ClosingSection =>
  ({ module, title: module, dateRule: '', unfinished, frozen, amountsHidden: false });

describe('figure', () => {
  it('называет сумму, только когда она есть', () => {
    expect(figure(line('a', 3, '3 счёта', 412500))).toMatch(/^3 счёта на 412\s500,00 ₽$/);
    expect(figure(line('a', 3, '3 счёта'))).toBe('3 счёта');
  });
});

describe('unfinishedSummary', () => {
  it('незавершённого нет — null, а не «0»', () => {
    expect(unfinishedSummary([])).toBeNull();
    expect(unfinishedSummary([section('costs', [], [line('entering', 5, '5 счетов')])])).toBeNull();
  });

  it('числа разных модулей перечисляются, а не складываются', () => {
    expect(unfinishedSummary([
      section('costs', [line('unsettled', 11, '11 счетов')]),
      section('works', [line('waiting', 2, '2 отчёта')]),
    ])).toBe('11 счетов, 2 отчёта');
  });
});

describe('wasText', () => {
  const before = [section('costs', [line('unsettled', 2, '2 счёта', 300)], [line('entering', 5, '5 счетов', 900)])];

  it('сравнивать не с чем — пометки нет', () => {
    expect(wasText(null, 'costs', 'unfinished', line('unsettled', 3, '3 счёта', 500))).toBeNull();
  });

  it('строка не изменилась — пометки нет', () => {
    expect(wasText(before, 'costs', 'unfinished', line('unsettled', 2, '2 счёта', 300))).toBeNull();
  });

  it('изменилось число или сумма — названо прежнее', () => {
    expect(wasText(before, 'costs', 'unfinished', line('unsettled', 3, '3 счёта', 500))).toMatch(/^было: 2 счёта на 300,00 ₽$/);
    // Число то же, сумма другая — тоже изменение: закрывают деньги, а не количество бумаг.
    expect(wasText(before, 'costs', 'frozen', line('entering', 5, '5 счетов', 901))).toMatch(/^было: 5 счетов на 900,00 ₽$/);
  });

  it('строки раньше не было', () => {
    expect(wasText(before, 'costs', 'frozen', line('locked', 6, '6 счетов'))).toBe('не было');
    // Та же строка другой группы — другая строка.
    expect(wasText(before, 'costs', 'frozen', line('unsettled', 2, '2 счёта', 300))).toBe('не было');
  });
});

describe('goneLines', () => {
  it('называет строки, которых больше нет', () => {
    const before = [section('costs', [line('unsettled', 2, '2 счёта')], [line('entering', 5, '5 счетов')])];
    const now = section('costs', [], [line('entering', 5, '5 счетов')]);

    expect(goneLines(before, now, 'unfinished').map(l => l.key)).toEqual(['unsettled']);
    expect(goneLines(before, now, 'frozen')).toEqual([]);
    expect(goneLines(null, now, 'unfinished')).toEqual([]);
  });
});

describe('localLink', () => {
  it('пропускает только путь внутри приложения', () => {
    expect(localLink('/tables/costs.invoices/registry#filter=%7B%7D')).toBe('/tables/costs.invoices/registry#filter=%7B%7D');
    expect(localLink(null)).toBeNull();
    expect(localLink('https://example.org/x')).toBeNull();
    expect(localLink('//example.org/x')).toBeNull();
    expect(localLink('/\\example.org')).toBeNull();
    expect(localLink('javascript:alert(1)')).toBeNull();
  });

  it('не пропускает управляющие символы и пробелы — браузер их выбросит, и путь станет чужим адресом', () => {
    for (const gap of ['\t', '\n', '\r', ' ', '\u00a0', '\u0000'])
      expect(localLink(`/${gap}/example.org/x`)).toBeNull();
    expect(localLink('/')).toBeNull();
  });
});

describe('goneSections и visiblyChanged', () => {
  const before = [
    section('costs', [line('unsettled', 2, '2 счёта')], [line('entering', 5, '5 счетов')]),
    section('works', [line('waiting', 1, '1 отчёт')]),
  ];

  it('раздел, исчезнувший целиком, назван', () => {
    expect(goneSections(before, [before[0]]).map(s => s.module)).toEqual(['works']);
    expect(goneSections(before, before)).toEqual([]);
    expect(goneSections(null, before)).toEqual([]);
    expect(visiblyChanged(before, [before[0]])).toBe(true);
  });

  it('на экране то же — изменилось невидимое', () => {
    expect(visiblyChanged(before, before)).toBe(false);
  });

  it('изменившаяся или исчезнувшая строка — видимое изменение', () => {
    expect(visiblyChanged(before, [section('costs', [line('unsettled', 3, '3 счёта')], [line('entering', 5, '5 счетов')]), before[1]])).toBe(true);
    expect(visiblyChanged(before, [section('costs', [], [line('entering', 5, '5 счетов')]), before[1]])).toBe(true);
  });
});
