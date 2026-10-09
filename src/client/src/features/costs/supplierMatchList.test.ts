import { describe, expect, it } from 'vitest';
import type { SupplierMatchItem } from '@/shared/api/supplierMatches';
import { forgetQuestion, issueNote, shownOf, supplierLabel } from './supplierMatchList';

const item = (patch: Partial<SupplierMatchItem> = {}): SupplierMatchItem => ({
  id: 'm-1', version: '7', supplierId: 's-1', supplierName: 'ООО «Свет-Опт»', supplierArchived: false,
  supplierLost: false, by: 'name', source: 'Кабель силовой 3х2,5', nomenclatureId: 'n-1',
  nomenclatureName: 'Кабель ВВГнг-LS 3х2,5', nomenclatureType: 'Номенклатура', issue: null,
  updatedAt: '2026-10-09T10:00:00Z', updatedBy: 'Снабженец', ...patch,
});

describe('слова списка соответствий', () => {
  it('удалённый поставщик назван удалённым, а не «без названия»', () => {
    expect(supplierLabel({ name: null, lost: true })).toBe('поставщик удалён');
    expect(supplierLabel({ name: '  ', lost: false })).toBe('поставщик без названия');
    expect(supplierLabel({ name: 'ООО «Свет-Опт»', lost: false })).toBe('ООО «Свет-Опт»');
  });

  it('почему соответствие не подставляется — сказано, а у действующего молчит', () => {
    expect(issueNote('archived')).toContain('в архиве');
    expect(issueNote('lost')).toContain('удалена');
    expect(issueNote(null)).toBeNull();
  });

  it('порция называет число «из», а весь список — сколько всего', () => {
    expect(shownOf(50, 1240)).toBe('Показано 50 из 1240');
    expect(shownOf(3, 3)).toBe('Всего: 3 соответствия');
    expect(shownOf(1, 1)).toBe('Всего: 1 соответствие');
  });

  it('вопрос перед «Забыть» называет поставщика, ключ, позицию и последствия', () => {
    const text = forgetQuestion(item());

    expect(text).toContain('ООО «Свет-Опт»: наименование «Кабель силовой 3х2,5» → «Кабель ВВГнг-LS 3х2,5»');
    expect(text).toContain('будет ждать выбора');
    expect(text).toContain('позиция останется');
    expect(forgetQuestion(item({ by: 'code', source: 'RZ-2W' }))).toContain('артикул «RZ-2W»');
    expect(forgetQuestion(item({ issue: 'lost', nomenclatureName: null }))).toContain('→ удалённая позиция');
  });
});
