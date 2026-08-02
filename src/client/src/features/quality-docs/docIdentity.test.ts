import { describe, it, expect } from 'vitest';
import { ambiguousDocNames, isAmbiguous } from './docIdentity';
import type { QualityDocument } from '@/shared/api/qualityDocs';

/**
 * Опознание документа качества в списках (issue #588). Живой случай: два сертификата назывались
 * «EKF — автоматические выключатели», а внутри были разные номера, органы и области продукции —
 * человек выбирал вслепую.
 */
const doc = (id: string, displayName: string) => ({ id, displayName } as QualityDocument);

describe('одноимённые документы', () => {
  it('находит имя, встречающееся дважды', () => {
    const docs = [
      doc('41d18642', 'EKF — автоматические выключатели'),
      doc('94e33abc', 'EKF — автоматические выключатели'),
      doc('c0ffee', 'Кабели силовые РЭМЗ'),
    ];
    const ambiguous = ambiguousDocNames(docs);
    expect(isAmbiguous(docs[0], ambiguous)).toBe(true);
    expect(isAmbiguous(docs[1], ambiguous)).toBe(true);
    expect(isAmbiguous(docs[2], ambiguous)).toBe(false);
  });

  /** Сравнение как у серверной проверки уникальности — иначе UI и запрет разошлись бы. */
  it('регистр и краевые пробелы не создают разных имён', () => {
    const docs = [doc('a', ' EKF — автоматические выключатели'), doc('b', 'ekf — Автоматические Выключатели')];
    expect(ambiguousDocNames(docs).size).toBe(1);
    expect(isAmbiguous(docs[0], ambiguousDocNames(docs))).toBe(true);
  });

  it('уникальное имя неоднозначным не считается', () => {
    expect(ambiguousDocNames([doc('a', 'Один'), doc('b', 'Другой')]).size).toBe(0);
  });
});
