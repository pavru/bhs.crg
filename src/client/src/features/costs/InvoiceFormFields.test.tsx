// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import type { CostsOrganization, InvoiceView } from '@/shared/api/invoices';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { BlockFields } from './InvoiceFormFields';
import { BLOCKS, K } from './invoiceFields';

vi.mock('@/shared/api/client', () => ({
  apiClient: { post: vi.fn(), get: vi.fn(async () => ({ data: null })), put: vi.fn(), delete: vi.fn() }, traceIdOf: () => undefined,
}));

/**
 * Поводы записи реквизитов черновика (costs-invoice-screen-12-autosave, D-12.2, ТЗ COST-6.5): набор в поле записи
 * не даёт, уход с поля — даёт один раз, выбор организации — сразу.
 */

afterEach(cleanup);

const view = {
  id: 'i1', version: 'v1', documentTypeId: 't', unconfirmed: [], lines: [],
  requisites: { Состояние: 'Черновик', [K.number]: '104' }, references: {},
  readiness: { required: [], gaps: [] }, payment: { lockedBy: null, paid: false },
} as unknown as InvoiceView;
const organizations: CostsOrganization[] = [
  { id: 'o1', name: 'ООО «Кабель-Торг»' } as CostsOrganization,
  { id: 'o2', name: 'ООО «Наша компания»' } as CostsOrganization,
];

function Form({ onLeave, pick, locked = false, block = BLOCKS[0] }: {
  onLeave: () => void; pick: (key: string, next: unknown) => void; locked?: boolean; block?: (typeof BLOCKS)[number];
}) {
  const [edits, setEdits] = useState<Record<string, unknown>>({});
  const value = (key: string) => (key in edits ? edits[key] : view.requisites[key]);
  const set = (key: string, next: unknown) => setEdits(prev => ({ ...prev, [key]: next }));
  return (
    <BlockFields block={block} view={view} edits={edits} organizations={organizations} organizationsUnread={false}
      value={value} set={set} pick={(key, next) => { set(key, next); pick(key, next); }} onLeave={onLeave} locked={locked} />
  );
}

function mount(props: { locked?: boolean; block?: (typeof BLOCKS)[number] } = {}) {
  const onLeave = vi.fn();
  const pick = vi.fn();
  render(<QueryClientProvider client={new QueryClient()}><SnackbarProvider><Form onLeave={onLeave} pick={pick} {...props} /></SnackbarProvider></QueryClientProvider>);
  return { onLeave, pick };
}

/**
 * Вид чтения разобранного (D-12.4, Э15): до «Изменить» форма отдаёт полям замок — каждое поле читается как
 * `TextField readOnly`, выбора и правки нет, поводов записи тоже.
 */
describe('разобранный до «Изменить» — поля для чтения', () => {
  it.each(BLOCKS.map(block => [block.title, block] as const))('«%s»: ни одного поля для ввода', (_, block) => {
    const { onLeave, pick } = mount({ locked: true, block });
    expect(screen.queryAllByRole('combobox')).toHaveLength(0);
    const boxes = screen.queryAllByRole('textbox');
    expect(boxes.length).toBeGreaterThan(0);
    for (const box of boxes) expect((box as HTMLInputElement).readOnly).toBe(true);
    boxes[0].focus();
    fireEvent.blur(boxes[0], { relatedTarget: boxes[1] ?? null });
    expect(onLeave).not.toHaveBeenCalled();
    expect(pick).not.toHaveBeenCalled();
  });
});

describe('реквизиты черновика пишутся по уходу с поля', () => {
  it('набор в поле записи не даёт; уход на соседнее поле — один повод', () => {
    const { onLeave } = mount();
    const number = screen.getByRole('textbox', { name: /Номер/ });
    number.focus();
    fireEvent.change(number, { target: { value: '1045' } });
    fireEvent.change(number, { target: { value: '10456' } });
    expect(onLeave).not.toHaveBeenCalled();
    fireEvent.blur(number, { relatedTarget: screen.getByRole('textbox', { name: /Сумма к оплате/ }) });
    expect(onLeave).toHaveBeenCalledOnce();
  });

  it('переход внутри одного поля (сегменты даты) поводом не считается', () => {
    const { onLeave } = mount();
    // Поле даты — несколько сегментов в одной обёртке поля.
    const wrapper = document.querySelector(`[data-readiness="field:${K.date}"]`)!;
    const segments = Array.from(wrapper.querySelectorAll<HTMLElement>('[tabindex="0"], input'));
    expect(segments.length).toBeGreaterThan(1);
    fireEvent.blur(segments[0], { relatedTarget: segments[1] });
    expect(onLeave).not.toHaveBeenCalled();
  });

  it('набор даты по сегментам — правка, а не запись: каждая полная дата посреди набора не пишется (ревью PR #1837)', () => {
    const { pick } = mount();
    const wrapper = document.querySelector<HTMLElement>(`[data-readiness="field:${K.date}"]`)!;
    // Сегменты — по роли, а не по маске: порядок и подсказки зависят от языка.
    const seg = (part: string) => wrapper.querySelector<HTMLInputElement>(`input[aria-label="${part}"]`)!;
    for (const [ph, v] of [['day', '05'], ['month', '10'], ['year', '2026'], ['day', '12'], ['day', '']] as const) {
      fireEvent.focus(seg(ph));
      fireEvent.change(seg(ph), { target: { value: v } });
    }
    expect(pick).not.toHaveBeenCalled();
  });

  it('выбор организации — повод сразу, не дожидаясь ухода с поля', () => {
    const { pick } = mount();
    const supplier = screen.getAllByRole('combobox')[0];
    fireEvent.keyDown(supplier, { key: 'ArrowDown' });
    fireEvent.click(screen.getByRole('option', { name: 'ООО «Кабель-Торг»' }));
    expect(pick).toHaveBeenCalledWith(K.supplier, expect.objectContaining({ entryId: 'o1' }));
  });
});
