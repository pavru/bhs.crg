import { describe, expect, it } from 'vitest';
import type { NomenclatureCandidate, NomenclatureCandidates } from '@/shared/api/nomenclatureCandidates';
import type { CardLine } from '@/shared/api/nomenclatureCard';
import cases from './supplierKeyCases.json';
import { matchKeyText } from './supplierMatches';
import {
  CANDIDATES_SHOWN, candidateRows, foundRows, lineVsCard, looksLikeArticle, neighbourCandidates, neighboursOf, wordMarks,
} from './pickerCandidates';

const candidate = (id: string, patch: Partial<NomenclatureCandidate> = {}): NomenclatureCandidate => ({
  id, name: `Позиция ${id}`, type: 'Номенклатура', matchedAlias: null, why: ['большинство слов строки'],
  strong: false, matched: null, ...patch,
});

const answer = (items: NomenclatureCandidate[], patch: Partial<NomenclatureCandidates> = {}): NomenclatureCandidates => ({
  items, more: false, inArchive: 0, unreadable: 0, recalledUnusable: null, ...patch,
});

describe('ключ соответствия — как на сервере (общий набор с C#)', () => {
  it.each(cases.cases.map(c => [c.name, c] as const))('%s', (_, c) => {
    expect(matchKeyText(c.input)).toBe(c.expect);
  });
});

describe('соседние строки этого же документа', () => {
  const lines = [
    { name: 'Кабель ВВГнг 3х2,5', code: 'K-1', positionId: 'p-1', positionName: 'Кабель 3х2,5' },
    null,
    { name: 'кабель  ввгнг 3х2,5 ', code: null, positionId: 'p-2', positionName: 'Кабель другой' },
    { name: 'Провод', code: 'k-1', positionId: 'p-1', positionName: 'Кабель 3х2,5' },
  ];
  const near = neighboursOf(lines, 1, l => l);

  it('соседи — без самой строки и без строк без позиции; номер — с единицы', () => {
    expect(near.map(n => n.number)).toEqual([1, 3, 4]);
    expect(neighboursOf(lines, 0, l => l).map(n => n.number)).toEqual([3, 4]);
  });

  it('точное равенство артикула или имени; сильный — только по артикулу', () => {
    const found = neighbourCandidates({ name: 'Кабель ВВГнг 3х2,5', code: 'K-1' }, near);
    expect(found).toEqual([
      expect.objectContaining({ id: 'p-1', strong: true, why: ['выбрана в строках 1, 4'] }),
      expect.objectContaining({ id: 'p-2', strong: false, why: ['выбрана в строке 3'] }),
    ]);
    expect(neighbourCandidates({ name: 'Кабель ВВГнг 3*2.5' }, near)).toEqual([]);
    expect(neighbourCandidates({}, near)).toEqual([]);
  });
});

describe('кандидаты строками', () => {
  it('«лучшее» — у сильного от сервера; первым; без сильного Enter-цели нет', () => {
    const { rows } = candidateRows(answer([candidate('a'), candidate('b', { strong: true, why: ['совпадает артикул «X»'] })]), [], null);
    expect(rows.map(r => [r.id, r.best])).toEqual([['b', true], ['a', false]]);
    expect(candidateRows(answer([candidate('a')]), [], null).rows.some(r => r.best)).toBe(false);
  });

  it('сосед, выбравший другую позицию, делает ответ спорным — лучшего нет', () => {
    const server = answer([candidate('a', { strong: true })]);
    const other = neighbourCandidates({ name: 'Кабель' }, [{ number: 2, name: 'кабель', positionId: 'b', positionName: 'Б' }]);
    const { rows } = candidateRows(server, other, null);
    expect(rows.map(r => [r.id, r.best])).toEqual([['b', false], ['a', false]]);
  });

  it('сосед с той же позицией склеивается с серверной строкой: причины через «·»', () => {
    const same = neighbourCandidates({ code: 'K-1' }, [{ number: 2, code: 'k-1', positionId: 'a', positionName: 'А' }]);
    const { rows } = candidateRows(answer([candidate('a', { why: ['совпадает артикул «K-1»'] })]), same, null);
    expect(rows).toEqual([expect.objectContaining({ id: 'a', best: true, why: 'совпадает артикул «K-1» · выбрана в строке 2' })]);
  });

  it('уже выбранной позиции среди кандидатов нет; больше пяти — «есть ещё»', () => {
    const many = answer(['a', 'b', 'c', 'd', 'e', 'f'].map(id => candidate(id)));
    const { rows, more } = candidateRows(many, [], 'a');
    expect(rows.map(r => r.id)).toEqual(['b', 'c', 'd', 'e', 'f']);
    expect(rows).toHaveLength(CANDIDATES_SHOWN);
    expect(more).toBe(false);
    expect(candidateRows(many, [], null).more).toBe(true);
    expect(candidateRows(answer([candidate('a')], { more: true }), [], null).more).toBe(true);
  });

  it('находка, бывшая кандидатом, сохраняет причину и сравнение; «лучшего» в поиске нет', () => {
    const matched = { words: [], field: 'Артикул' };
    const { rows } = candidateRows(answer([candidate('a', { strong: true, matched, why: ['совпадает артикул «X»'] })]), [], null);
    const found = foundRows([
      { id: 'a', name: 'А', type: 'Номенклатура', matchedAlias: 'AV' },
      { id: 'z', name: 'Я', type: 'Номенклатура', matchedAlias: null },
    ], rows);
    expect(found).toEqual([
      { id: 'a', name: 'А', type: 'Номенклатура', why: 'совпадает артикул «X» · найдено по «AV»', best: false, matched },
      { id: 'z', name: 'Я', type: 'Номенклатура', why: null, best: false, matched: null },
    ]);
  });
});

describe('строка ↔ позиция', () => {
  const tagged = (key: string, text: string | null, unreadable = false): CardLine => ({
    key, title: key, text, kind: 'text', identity: false, inheritedFrom: null, unreadable, archived: false,
  });
  const source = 'Провод ПуГВ 1х1,5';
  const words = [
    { text: 'Провод', start: 0, found: true },
    { text: 'ПуГВ', start: 7, found: true },
    { text: '1х1,5', start: 12, found: false },
  ];

  it('слова — числом и подсветкой по месту в строке', () => {
    const [row] = lineVsCard(source, { name: source }, { words, field: null }, { article: null, unit: null });
    expect(row).toMatchObject({ label: 'Наименование', text: 'совпадают 2 слова из 3' });
    expect(row.marks).toEqual([
      { text: 'Провод', hit: true }, { text: ' ', hit: false }, { text: 'ПуГВ', hit: true },
      { text: ' ', hit: false }, { text: '1х1,5', hit: false },
    ]);
    const all = words.map(w => ({ ...w, found: true }));
    expect(lineVsCard(source, { name: source }, { words: all, field: null }, { article: null, unit: null })[0].text)
      .toBe('совпадают все 3 слова строки');
  });

  it('артикул: признак сервера, иначе — поле тэга; не размечено — строки нет', () => {
    const from = { name: source, code: 'K-1' };
    expect(lineVsCard(source, from, { words: [], field: 'Код' }, { article: null, unit: null }))
      .toEqual([{ key: 'code', label: 'Артикул', text: 'совпадает — поле «Код»', tone: 'ok' }]);
    expect(lineVsCard(source, from, null, { article: tagged('Арт', 'K-2'), unit: null })[0])
      .toMatchObject({ text: 'отличается: в строке K-1, в позиции K-2', tone: 'differs' });
    expect(lineVsCard(source, from, null, { article: tagged('Арт', null), unit: null })[0].text).toBe('в позиции не указан');
    expect(lineVsCard(source, from, null, { article: tagged('Арт', null, true), unit: null })[0].text).toBe('в позиции не прочитан');
    expect(lineVsCard(source, from, null, { article: null, unit: null })).toEqual([]);
  });

  it('единица: «шт.» и «ШТ» — одна; другая — жёлтым', () => {
    const from = { name: source, unit: 'ШТ' };
    expect(lineVsCard(source, from, null, { article: null, unit: tagged('Ед', 'шт.') })[0])
      .toMatchObject({ text: 'совпадает: шт.', tone: 'ok' });
    expect(lineVsCard(source, { name: source, unit: 'м' }, null, { article: null, unit: tagged('Ед', 'бухта') })[0])
      .toMatchObject({ text: 'отличается: в строке м, в позиции бухта', tone: 'differs' });
  });

  it('слова не на своих местах — подсветка словами сервера, а не по сдвинутой строке', () => {
    expect(wordMarks('другая строка', words).map(m => m.text).join('')).toBe('Провод ПуГВ 1х1,5');
  });
});

describe('запрос похож на артикул', () => {
  it.each([
    ['mcb10-1-06C', true], ['00-00293', true], ['пугв 1х1,5', false], ['кабель', false], ['123', false],
  ])('%s → %s', (query, expected) => {
    expect(looksLikeArticle(query)).toBe(expected);
  });
});
