import { describe, it, expect } from 'vitest';
import { linkAnomaly, scopeBreakdown, scopeBreakdownText, widerThanSet } from './linkScopes';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';
import type { CatalogScope } from '@/shared/api/types';

let seq = 0;
function link(
  scope: CatalogScope,
  materialKey = `m${++seq}`,
  { docId = 'q1', docName = 'Сертификат', scopeId = null as string | null } = {},
): MaterialQualityLink {
  return {
    id: `l${++seq}`, scope, scopeId, materialKey, materialLabel: null,
    qualityDocumentId: docId, qualityDocumentName: docName, qualityDocumentType: null,
    createdAt: '', updatedAt: '',
  } as MaterialQualityLink;
}

/** Один и тот же список и как «связки документа», и как «вся библиотека» — обычный случай. */
const same = (links: MaterialQualityLink[]) => ({ inDocument: links, all: links });

describe('состав связок по уровням', () => {
  it('считает и упорядочивает от узкого к широкому', () => {
    const links = [link('System'), link('Set'), link('Set'), link('Construction')];
    expect(scopeBreakdown(links)).toEqual([
      { scope: 'Set', count: 2 },
      { scope: 'Construction', count: 1 },
      { scope: 'System', count: 1 },
    ]);
    expect(scopeBreakdownText(links)).toBe('Комплект 2, Стройка 1, Система 1');
  });

  it('шире комплекта — всё, кроме уровня «Комплект»', () => {
    const links = [link('Set'), link('Section'), link('System')];
    expect(widerThanSet(links).map(l => l.scope)).toEqual(['Section', 'System']);
  });

  it('пустой список не даёт ни состава, ни текста', () => {
    expect(scopeBreakdown([])).toEqual([]);
    expect(scopeBreakdownText([])).toBe('');
  });
});

describe('аномалии связки', () => {
  it('однородный список аномалий не даёт', () => {
    const links = [link('Set'), link('Set')];
    expect(linkAnomaly(links[0], same(links))).toBeNull();
  });

  it('однородный список из общесистемных — тоже не аномалия', () => {
    // Живой случай #587: все 113 связок на System. Это могло быть и осознанным решением; аномалия —
    // когда связка выбивается из остальных, а не когда весь список одинаков.
    const links = [link('System'), link('System')];
    expect(linkAnomaly(links[0], same(links))).toBeNull();
  });

  it('связка шире остальных помечается', () => {
    const wide = link('System');
    const links = [link('Set'), link('Set'), wide];
    expect(linkAnomaly(wide, same(links))).toContain('шире остальных');
    expect(linkAnomaly(links[0], same(links))).toBeNull();
  });

  it('за материал спорят два документа: победителя называем по имени', () => {
    const mine = link('System', 'кабель | ', { docId: 'A', docName: 'Сертификат A' });
    const rival = link('Set', 'кабель | ', { docId: 'B', docName: 'Декларация B', scopeId: 's1' });
    const all = [mine, rival];
    expect(linkAnomaly(mine, { inDocument: [mine], all })).toContain('«Декларация B»');
    expect(linkAnomaly(rival, { inDocument: [rival], all })).toContain('подставится ЭТА');
  });

  it('спор внутри ОДНОГО документа аномалией не считается', () => {
    // Кто бы из двух связок ни победил, в PDF попадёт этот же документ — предупреждать не о чем.
    const links = [
      link('Set', 'кабель | ', { docId: 'A' }),
      link('System', 'кабель | ', { docId: 'A' }),
    ];
    expect(linkAnomaly(links[0], same(links))).toBeNull();
    // У широкой связки остаётся своя, отдельная претензия — она шире соседей по документу; про
    // спор документов не говорится ничего, потому что документ один.
    expect(linkAnomaly(links[1], same(links))).not.toContain('другому документу');
  });

  it('один ключ в двух РАЗНЫХ комплектах спором не считается', () => {
    // Уникальность в базе — по тройке (область, объект области, ключ), поэтому один материал
    // законно живёт в двух комплектах. Резолвер читает связки только своей цепочки: они не
    // встречаются никогда, и треугольник здесь был бы ложной тревогой.
    const a = link('Set', 'кабель | ', { docId: 'A', docName: 'Сертификат A', scopeId: 'setA' });
    const b = link('Set', 'кабель | ', { docId: 'B', docName: 'Декларация B', scopeId: 'setB' });
    const all = [a, b];
    expect(linkAnomaly(a, { inDocument: [a], all })).toBeNull();
    expect(linkAnomaly(b, { inDocument: [b], all })).toBeNull();
  });

  it('пара «комплект — раздел» молчит: содержит ли раздел этот комплект, экран не знает', () => {
    const set = link('Set', 'кабель | ', { docId: 'A', scopeId: 'setA' });
    const section = link('Section', 'кабель | ', { docId: 'B', scopeId: 'sec1' });
    const all = [set, section];
    expect(linkAnomaly(set, { inDocument: [set], all })).toBeNull();
    expect(linkAnomaly(section, { inDocument: [section], all })).toBeNull();
  });

  it('спор важнее, чем «шире остальных»', () => {
    const wide = link('System', 'кабель | ', { docId: 'A' });
    const rival = link('Set', 'кабель | ', { docId: 'B', docName: 'Декларация B', scopeId: 's1' });
    const inDocument = [wide, link('Set', 'другое', { docId: 'A' })];
    expect(linkAnomaly(wide, { inDocument, all: [...inDocument, rival] })).toContain('Декларация B');
  });
});
