import { describe, it, expect } from 'vitest';
import { linkAnomaly, scopeBreakdown, scopeBreakdownText, widerThanSet } from './linkScopes';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';
import type { CatalogScope } from '@/shared/api/types';

let seq = 0;
function link(scope: CatalogScope, materialKey = `m${++seq}`): MaterialQualityLink {
  return {
    id: `l${++seq}`, scope, scopeId: null, materialKey, materialLabel: null,
    qualityDocumentId: 'q1', qualityDocumentName: 'Сертификат', qualityDocumentType: null,
    createdAt: '', updatedAt: '',
  } as MaterialQualityLink;
}

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
    expect(linkAnomaly(links[0], links)).toBeNull();
  });

  it('однородный список из общесистемных — тоже не аномалия', () => {
    // Живой случай #587: все 113 связок на System. Это могло быть и осознанным решением; аномалия —
    // когда связка выбивается из остальных, а не когда весь список одинаков.
    const links = [link('System'), link('System')];
    expect(linkAnomaly(links[0], links)).toBeNull();
  });

  it('связка шире остальных помечается', () => {
    const wide = link('System');
    const links = [link('Set'), link('Set'), wide];
    expect(linkAnomaly(wide, links)).toContain('шире остальных');
    expect(linkAnomaly(links[0], links)).toBeNull();
  });

  it('два уровня на один материал: победителя называем прямо', () => {
    const narrow = link('Set', 'кабель | ');
    const wide = link('System', 'кабель | ');
    const links = [narrow, wide];
    expect(linkAnomaly(narrow, links)).toContain('подставится ЭТА');
    expect(linkAnomaly(wide, links)).toContain('«Комплект»');
  });

  it('конфликт уровней важнее, чем «шире остальных»', () => {
    // У широкой связки верны обе претензии, но неразличимый дубль — та, о которой надо сказать:
    // она объясняет, почему в PDF попадает не то, что человек видит первым.
    const narrow = link('Set', 'кабель | ');
    const wide = link('System', 'кабель | ');
    const links = [narrow, wide, link('Set')];
    expect(linkAnomaly(wide, links)).toContain('на разных уровнях');
  });
});
