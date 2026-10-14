import { describe, expect, it } from 'vitest';
import type { CostsOrganization } from '@/shared/api/invoices';
import type { SupplierTermsItem } from '@/shared/api/supplierTerms';
import { choosable, parseDays, removeQuestion, termsSupplier } from './supplierTerms';

const item = (supplierId: string, patch: Partial<SupplierTermsItem> = {}): SupplierTermsItem => ({
  supplierId, version: '1', supplierName: 'ООО «Кабель»', supplierArchived: false, supplierLost: false,
  deferralDays: 30, updatedAt: '2026-10-14T00:00:00Z', updatedBy: null, ...patch,
});

describe('parseDays', () => {
  it('целое число дней в пределах', () => {
    expect(parseDays(' 30 ')).toBe(30);
    expect(parseDays('0')).toBe(0);
    expect(parseDays('3650')).toBe(3650);
  });

  it('пусто — не число и не ошибка', () => {
    expect(parseDays('  ')).toBeNull();
  });

  it('дробь, минус, буквы и век — «не то»', () => {
    for (const text of ['1.5', '-1', '30 дней', '3651']) expect(parseDays(text)).toBe('bad');
  });
});

describe('choosable', () => {
  const org = (id: string, archived = false): CostsOrganization => ({ id, name: id, type: 'org', archived });

  it('в выборе нет архивных и тех, у кого условия уже есть', () => {
    expect(choosable([org('a'), org('b'), org('c', true)], [item('b')]).map(o => o.id)).toEqual(['a']);
  });
});

describe('termsSupplier и removeQuestion', () => {
  it('удалённый поставщик назван словами', () => {
    expect(termsSupplier(item('x', { supplierLost: true, supplierName: null }))).toBe('поставщик удалён');
  });

  it('вопрос говорит, что заведённые счета не меняются', () => {
    expect(removeQuestion(item('x'))).toContain('В уже заведённых счетах отсрочка и срок останутся как есть');
  });
});
