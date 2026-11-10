import { describe, expect, it } from 'vitest';
import type { ConjunctureItem, ConjunctureOffer, EstimateAttachment, VolumeLine } from '@/shared/api/planningConjuncture';
import {
  attachmentSummary, attachmentTitle, justificationWords, kaLines, mergeAttachments, noPricesWords, offerWords,
  partialWords, selectionWords, vorLines,
} from './attachmentRows';

function attachment(over: Partial<EstimateAttachment> = {}): EstimateAttachment {
  return {
    id: 'a1', kind: 'ka', estimateId: 'e1', fileName: '250803-КАЦ-01.gsfx', number: null, name: null, programVersion: null,
    schemaVersion: null, creationDate: null, currentPriceDate: null, parsed: 52, inFile: 52, chapters: 2, suppliers: 3,
    importedAt: '2026-11-08T10:00:00Z', importedBy: 'Иванова А.', version: '1', ...over,
  };
}

function offer(order: number, price: number | null, over: Partial<ConjunctureOffer> = {}): ConjunctureOffer {
  return {
    order, number: String(order), caption: 'Кабель', units: 'м', resourceCode: null, supplier: `Поставщик ${order}`,
    supplierInn: `770000001${order}`, vat: 20, date: null, selected: false,
    ...(price === undefined ? {} : { cost: { price, priceWithVat: price === null ? null : price * 1.2 } }), ...over,
  };
}

function item(over: Partial<ConjunctureItem> = {}): ConjunctureItem {
  return {
    id: 'i1', order: 1, chapterIndex: 1, chapterCaption: 'Кабельная продукция', number: '1', caption: 'Кабель ВВГнг 5×16',
    units: 'м', inactive: false, selection: 'min', selectedOffer: 1,
    offers: [offer(1, 100, { selected: true }), offer(2, 110), offer(3, 120)], cost: { selectedPrice: 100 }, ...over,
  };
}

const withoutCost = (i: ConjunctureItem): ConjunctureItem => ({
  ...i, cost: undefined, offers: i.offers.map(o => ({ ...o, cost: undefined })),
});

describe('строка списка приложений', () => {
  it('заголовок — номер и название, иначе имя файла', () => {
    expect(attachmentTitle(attachment({ number: 'КА-02', name: 'Сводный конъюнктурный анализ' }))).toBe('КА-02 · Сводный конъюнктурный анализ');
    expect(attachmentTitle(attachment({ name: '  ' }))).toBe('250803-КАЦ-01.gsfx');
  });

  it('сводка КА — позиции, поставщики и дата цен; ВОР — работы в главах', () => {
    expect(attachmentSummary(attachment({ currentPriceDate: '2026-03-25' }))).toBe('52 позиции · 3 поставщика · цены на 25.03.2026');
    expect(attachmentSummary(attachment({ suppliers: 1 }))).toBe('52 позиции · 1 поставщик');
    expect(attachmentSummary(attachment({ kind: 'vor', parsed: 48, chapters: 7, suppliers: null }))).toBe('48 работ в 7 главах');
    expect(attachmentSummary(attachment({ kind: 'vor', parsed: 1, chapters: 1, suppliers: null }))).toBe('1 работа в 1 главе');
  });

  it('частично — только когда разобрано не всё', () => {
    expect(partialWords(attachment({ parsed: 84, inFile: 86 }))).toBe('частично 84 из 86');
    expect(partialWords(attachment())).toBeNull();
  });

  it('КА и ВОР одним списком, новые сверху', () => {
    const merged = mergeAttachments(
      [attachment({ id: 'old', importedAt: '2026-11-01T00:00:00Z' })],
      [attachment({ id: 'new', kind: 'vor', importedAt: '2026-11-08T00:00:00Z' })],
    );
    expect(merged.map(a => a.id)).toEqual(['new', 'old']);
  });
});

describe('способ выбора словом', () => {
  it('минимальная, единственное, цен нет, не учитывается', () => {
    expect(selectionWords(item())).toBe('минимальная');
    expect(selectionWords(item({ offers: [offer(1, 100, { selected: true })] }))).toBe('единственное');
    expect(selectionWords(item({ selection: 'none', selectedOffer: null }))).toBe('цен нет');
    expect(selectionWords(item({ inactive: true }))).toBe('не учитывается');
  });

  it('выбрано в файле не минимальное — сказано, когда цены видны', () => {
    const chosen = item({
      selection: 'chosen', selectedOffer: 2, cost: { selectedPrice: 110 },
      offers: [offer(1, 100), offer(2, 110, { selected: true }), offer(3, 120)],
    });
    expect(selectionWords(chosen)).toBe('выбрано в файле, не минимальная');
    expect(offerWords(chosen, chosen.offers[0])).toBe('минимальная');
    expect(offerWords(chosen, chosen.offers[1])).toBe('выбрано');
    // Без права на цены «не минимальная» не вычислить — сказано только то, что известно.
    expect(selectionWords(withoutCost(chosen))).toBe('выбрано в файле');
    expect(offerWords(withoutCost(chosen), withoutCost(chosen).offers[0])).toBeNull();
  });

  it('без права на цены выбранное минимальным так и названо', () => {
    const min = withoutCost(item());
    expect(offerWords(min, min.offers[0])).toBe('выбрано · минимальная');
    expect(offerWords(min, min.offers[1])).toBeNull();
  });
});

describe('строки сетки КА', () => {
  it('глава, позиция и её предложения строками; одно предложение — одна строка', () => {
    const single = item({ id: 'i2', number: '2', offers: [offer(1, 50, { selected: true })] });
    const lines = kaLines([item(), single], '');
    expect(lines.map(l => l.kind)).toEqual(['chapter', 'item', 'offer', 'offer', 'offer', 'item']);
    expect(lines[5]).toMatchObject({ kind: 'item', single: true });
  });

  it('без цен у всех предложений — одной строкой с поставщиками', () => {
    const none = item({ selection: 'none', selectedOffer: null, cost: { selectedPrice: null },
      offers: [offer(1, null), offer(2, null), offer(3, null)] });
    expect(kaLines([none], '').map(l => l.kind)).toEqual(['chapter', 'item', 'no-prices']);
    expect(noPricesWords(none)).toBe('3 предложения без цен: Поставщик 1, Поставщик 2, Поставщик 3');
  });

  it('поиск находит позицию по поставщику и ИНН, глава — только у найденного', () => {
    const other = item({ id: 'i9', chapterIndex: 2, chapterCaption: '', number: '9', caption: 'Лоток',
      offers: [offer(1, 10, { selected: true, supplier: 'АО «ТД Провод»', supplierInn: '7700000033' })] });
    expect(kaLines([item(), other], 'тд провод').map(l => l.key)).toEqual(['chapter:2', 'i9']);
    expect(kaLines([item(), other], '7700000033')).toHaveLength(2);
    expect(kaLines([other], '')[0]).toMatchObject({ caption: 'Глава 2' });
  });
});

describe('строки сетки ВОР', () => {
  const line = (over: Partial<VolumeLine>): VolumeLine => ({
    id: 'l1', order: 1, chapterIndex: 3, chapterCaption: 'Силовое электрооборудование', number: '12', caption: 'Прокладка кабеля',
    code: null, units: 'м', quantity: 12.24, formula: null, drawings: null, note: null, subordinate: false, parentNumber: null, ...over,
  });

  it('по главам и с поиском по шифру и чертежам', () => {
    const lines = [line({}), line({ id: 'l2', number: '12.1', subordinate: true, parentNumber: '12' }),
      line({ id: 'l3', chapterIndex: 4, number: '13', drawings: 'ЭОМ-2 л. 4–6' })];
    expect(vorLines(lines, '').map(l => l.kind)).toEqual(['chapter', 'line', 'line', 'chapter', 'line']);
    expect(vorLines(lines, 'эом-2').map(l => l.key)).toEqual(['chapter:4', 'l3']);
  });

  it('обоснование одной строкой', () => {
    expect(justificationWords({ code: '250803-ЭОМ лист 7', drawings: null, note: 'с запасом 2 %' }))
      .toBe('шифр 250803-ЭОМ лист 7 · примечание: с запасом 2 %');
    expect(justificationWords({ code: null, drawings: null, note: null })).toBeNull();
  });
});
