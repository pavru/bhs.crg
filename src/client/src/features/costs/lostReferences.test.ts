import { describe, expect, it } from 'vitest';
import type { AllocationPartView, InvoiceLineView, InvoiceView } from '@/shared/api/invoices';
import { issueOf, lostSummary } from './lostReferences';
import { allocationStatus } from './allocation';
import { LOADING_PLACE, UNREAD_PLACE, missingSection, placeName } from './places';

const part = (over: Partial<AllocationPartView>): AllocationPartView => ({
  id: 'p', ordinal: 1, constructionId: 's', constructionName: null, sectionId: null, sectionName: null,
  articleId: null, articleName: null, targetLost: false, targetIssue: null, quantity: 1, amount: 1,
  rounding: 0, discrepancy: 0, mismatched: false, ...over,
});

const line = (ordinal: number, lost: boolean, parts: AllocationPartView[] = []) => ({
  ordinal, nomenclatureLost: lost,
  allocation: { mode: 'quantity', parts, unallocatedQuantity: 0, unallocatedAmount: 0, balanced: true },
}) as unknown as InvoiceLineView;

const invoice = (over: Partial<InvoiceView>): InvoiceView => ({
  lines: [],
  references: { supplier: 'present', payer: null, documentType: 'present' },
  allocation: { document: { parts: [] } },
  ...over,
}) as unknown as InvoiceView;

describe('lostSummary', () => {
  it('у счёта без потерь сводки нет', () => {
    expect(lostSummary(invoice({ lines: [line(1, false, [part({})])] }))).toBeNull();
  });

  it('называет, где потери, и считает МЕСТА, а не ссылки: о каскаде удаления клиент не гадает', () => {
    const summary = lostSummary(invoice({
      references: { supplier: 'lost', payer: 'present', documentType: 'present' },
      lines: [
        line(1, false),
        line(3, true),
        line(5, false, [
          part({ targetLost: true, targetIssue: 'construction-lost', sectionId: 'x' }),
          part({ targetLost: true, targetIssue: 'article-lost', constructionId: null, articleId: 'a' }),
        ]),
        line(7, true),
      ],
    }));

    expect(summary).toEqual({
      count: 5,
      places: 'поставщик; позиция в строках 3, 7; разноска строки 5',
      others: [], unchecked: [],
    });
  });

  it('раздел другой стройки — не потеря: назван отдельно и в число не входит', () => {
    const summary = lostSummary(invoice({
      lines: [line(2, false, [part({ targetLost: true, targetIssue: 'section-foreign', sectionId: 'x' })])],
    }));

    expect(summary).toEqual({ count: 0, places: '', others: ['раздел другой стройки — разноска строки 2'], unchecked: [] });
  });

  it('запись другого вида — не потеря: позиция и статья названы отдельно', () => {
    const moved = { ...line(4, true, [part({ targetLost: true, targetIssue: 'article-moved', constructionId: null, articleId: 'a' })]),
      nomenclatureIssue: 'moved' } as InvoiceLineView;

    expect(lostSummary(invoice({ lines: [moved] }))).toEqual({
      count: 0, places: '',
      others: ['позиция другого вида — в строке 4', 'статья другого вида — разноска строки 4'],
      unchecked: [],
    });
  });

  it('разноска счёта целиком (счёт без строк) видна сводке так же, как разноска строк', () => {
    const whole = (issue: AllocationPartView['targetIssue']) => invoice({
      allocation: { document: { parts: [part({ targetLost: true, targetIssue: issue, constructionId: null, articleId: 'a' })] } },
    } as unknown as Partial<InvoiceView>);

    expect(lostSummary(whole('article-moved'))).toEqual({
      count: 0, places: '', others: ['статья другого вида — разноска счёта'], unchecked: [],
    });
    expect(lostSummary(whole('article-lost'))).toMatchObject({ count: 1, places: 'разноска счёта' });
  });

  it('непрочитанный справочник — «не проверено», а не «запись на месте»', () => {
    const summary = lostSummary(invoice({
      lines: [line(1, false, [part({ targetLost: true, targetIssue: 'article-unread', constructionId: null, articleId: 'a' })])],
    }));

    expect(summary).toEqual({ count: 0, places: '', others: [], unchecked: ['статьи не прочитаны — разноска строки 1'] });
  });

  it('без состояния ссылок от сервера шапку потерянной не объявляет', () => {
    expect(lostSummary(invoice({ references: undefined }))).toBeNull();
  });
});

describe('пометка части разноски', () => {
  it('старый сервер не говорит, что потеряно, — потеря всё равно названа', () => {
    expect(issueOf(part({ targetLost: true, targetIssue: undefined }))).toBe('construction-lost');
    expect(issueOf(part({ targetLost: true, targetIssue: undefined, articleId: 'a' }))).toBe('article-lost');
  });

  it('слово — по тому, что потеряно, а не всегда «стройка удалена»', () => {
    const status = (issue: AllocationPartView['targetIssue']) => allocationStatus(
      { mode: 'quantity', parts: [part({ targetLost: true, targetIssue: issue })], unallocatedQuantity: 0, unallocatedAmount: 0, balanced: true },
      null).text;

    expect(status('section-lost')).toBe('раздел удалён');
    expect(status('section-foreign')).toBe('раздел другой стройки');
    expect(status('construction-lost')).toBe('стройка удалена');
  });
});

describe('placeName', () => {
  it('пока справочник мест не прочитан, стройку удалённой не называет', () => {
    const loading = { sites: undefined, articles: undefined };
    expect(placeName({ construction: 's', section: null, article: null }, loading)).toBe(LOADING_PLACE);
    expect(placeName({ construction: null, section: null, article: 'a' }, loading)).toBe(LOADING_PLACE);
    expect(placeName({ construction: 's', section: null, article: null }, { sites: [], articles: [] })).toBe('стройка удалена');
  });

  it('раздел, которого нет у стройки, но есть у другой, удалённым не называет', () => {
    const places = {
      sites: [{ id: 'a', name: 'А', sections: [] }, { id: 'b', name: 'Б', sections: [{ id: 'x', name: 'раздел' }] }],
      articles: [],
    };
    expect(missingSection('x', places)).toBe('раздел другой стройки');
    expect(missingSection('нет', places)).toBe('раздел удалён');
    expect(placeName({ construction: 'a', section: 'x', article: null }, places)).toBe('А / раздел другой стройки');
  });

  it('справочник не пришёл — это отказ, а не вечная загрузка', () => {
    const failed = { sites: undefined, articles: undefined, unread: true };
    expect(placeName({ construction: 's', section: null, article: null }, failed)).toBe(UNREAD_PLACE);
  });
});
