// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { resetHistoryLayers } from '@/shared/ui/historyLayers';
import '@/shared/ui/NavigationGuard';
import { InvoiceStatusIndicator } from './InvoiceStatusIndicator';
import { InvoiceStatusBanner, InvoiceStatusSheet } from './InvoiceStatusSheet';
import { goTo, lineAnchor, READINESS_ATTR } from './invoiceReadiness';
import { partOfTarget } from './invoiceParts';
import type { InvoiceStatus, StatusAction, StatusItem } from './invoiceStatus';

/**
 * Индикатор, перечень и banner (costs-invoice-screen-11, задачи 2.1–2.4, 3.1, ТЗ COST-6.4). Чем ломается:
 * диктор слышит каждый пункт; лист не возвращает фокус; banner стоит при «Проверьте» или их два.
 */

const realMatchMedia = window.matchMedia;
beforeEach(() => {
  resetHistoryLayers();
  window.history.replaceState({ idx: 0, key: 'base' }, '');
  // Широкий экран: лист — боковой.
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: q.includes('min-width'), addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
});
afterEach(() => { cleanup(); window.matchMedia = realMatchMedia; });

const item = (key: string, level: StatusItem['level'], text: string, actions: StatusAction[] = []): StatusItem =>
  ({ key, level, text, actions });

const status = (mood: InvoiceStatus['mood'], items: StatusItem[], phrase: string): InvoiceStatus => ({
  mood, tone: mood === 'blocking' ? 'error' : mood === 'info' ? 'neutral' : 'warning',
  count: items.filter(i => i.level !== 'info').length, phrase, running: false, items,
});

const GATE = status('gate', [
  item('gap:a', 'gate', 'Строки не разнесены по стройкам', [{ kind: 'goto', label: 'К разноске', place: { place: 'allocation' } }]),
  item('dup', 'check', 'Похоже на дубликат: СЧ-104', [{ kind: 'open', label: 'Открыть СЧ-104', invoiceId: 'd1' }]),
  item('hidden', 'info', 'Этого счёта нет в списке слева'),
], '2 замечания');

describe('индикатор (2.1)', () => {
  it('смена пунктов при том же уровне не объявляется, смена уровня — объявляется', () => {
    const live = () => screen.getByTestId('invoice-status-live').textContent;
    const { rerender } = render(<InvoiceStatusIndicator status={GATE} open={false} onOpen={() => {}} />);
    expect(live()).toBe('');

    rerender(<InvoiceStatusIndicator status={{ ...GATE, count: 1, phrase: '1 замечание', items: GATE.items.slice(1) }} open={false} onOpen={() => {}} />);
    expect(live()).toBe('');

    rerender(<InvoiceStatusIndicator status={status('ready', [], 'Готов к «Разобран»')} open={false} onOpen={() => {}} />);
    expect(live()).toBe('Готов к «Разобран»');
  });

  it('переход в «Мешает работе» молчит — его объявляет срочный banner', () => {
    const { rerender } = render(<InvoiceStatusIndicator status={GATE} open={false} onOpen={() => {}} />);
    rerender(<InvoiceStatusIndicator status={status('blocking', [item('gone', 'blocking', 'Счёт удалён')], 'Счёт удалён')} open={false} onOpen={() => {}} />);
    expect(screen.getByTestId('invoice-status-live').textContent).toBe('');
  });

  it('фраза — текстом; кнопка называет, что откроет', () => {
    const onOpen = vi.fn();
    render(<InvoiceStatusIndicator status={GATE} open={false} onOpen={onOpen} />);
    const button = screen.getByRole('button', { name: '2 замечания — открыть перечень' });
    expect(button.textContent).toContain('2 замечания');
    fireEvent.click(button);
    expect(onOpen).toHaveBeenCalled();
  });
});

function Host({ onAction }: { onAction: (a: StatusAction) => void }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <InvoiceStatusIndicator status={GATE} open={open} onOpen={() => setOpen(true)} />
      <InvoiceStatusSheet open={open} onClose={() => setOpen(false)} status={GATE} saving={false} savedAt="07.11.2026 12:04" onAction={onAction} />
    </>
  );
}

describe('перечень (2.2)', () => {
  it('подзаголовок «по сохранённому», группы по уровню, пункт — одно действие', () => {
    render(<Host onAction={() => {}} />);
    fireEvent.click(screen.getByTestId('invoice-status'));
    const sheet = screen.getByRole('dialog', { name: 'Состояние счёта' });
    expect(within(sheet).getByTestId('invoice-status-subtitle').textContent).toBe('2 замечания · по сохранённому · 07.11.2026 12:04');
    expect(within(sheet).getAllByRole('heading', { level: 3 }).map(h => h.textContent))
      .toEqual(['Мешает «Разобран» и оплате', 'Проверьте', 'К сведению · в число не идёт']);
    expect(within(within(sheet).getByTestId('status-item-dup').parentElement!).getAllByRole('button').map(b => b.textContent))
      .toEqual(['Открыть СЧ-104']);
  });

  it('пока запись в пути — «Сохраняю…»', () => {
    render(<InvoiceStatusSheet open onClose={() => {}} status={GATE} saving savedAt="12:04" onAction={() => {}} />);
    expect(screen.getByTestId('invoice-status-subtitle').textContent).toBe('2 замечания · Сохраняю…');
  });

  it('действие закрывает лист и уходит форме — после закрытия: фокус к индикатору возвращается раньше', async () => {
    const onAction = vi.fn();
    render(<Host onAction={onAction} />);
    fireEvent.click(screen.getByTestId('invoice-status'));
    fireEvent.click(screen.getByRole('button', { name: 'К разноске' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(onAction).not.toHaveBeenCalled();
    await act(() => new Promise(r => setTimeout(r, 50)));
    expect(onAction).toHaveBeenCalledWith(expect.objectContaining({ kind: 'goto', place: { place: 'allocation' } }));
  });

  it('метки: справа «Всё верно», «Показать на скане» — сама строка (D-11.4, вариант А)', async () => {
    const onAction = vi.fn();
    const marks = status('check', [item('marks', 'check', '2 значения со скана не проверены: «Номер», «Сумма к оплате»', [
      { kind: 'confirm-marks', label: 'Всё верно (2)' }, { kind: 'show-scan', label: 'Показать на скане' }])], '1 замечание');
    // Действие исполняется после снятия листа — лист обязан закрыться.
    function Sheet() {
      const [open, setOpen] = useState(true);
      return <InvoiceStatusSheet open={open} onClose={() => setOpen(false)} status={marks} saving={false} savedAt="12:04" onAction={onAction} />;
    }
    render(<Sheet />);
    const row = screen.getByTestId('status-item-marks');
    expect(within(row.parentElement!).getAllByRole('button').map(b => b.textContent)).toContain('Всё верно (2)');
    expect(row.getAttribute('aria-description')).toBe('Показать на скане');
    fireEvent.click(row);
    await act(() => new Promise(r => setTimeout(r, 50)));
    expect(onAction).toHaveBeenCalledWith(expect.objectContaining({ kind: 'show-scan' }));
  });

  it('Esc закрывает лист и возвращает фокус на индикатор', async () => {
    render(<Host onAction={() => {}} />);
    const opener = screen.getByTestId('invoice-status');
    opener.focus();
    fireEvent.click(opener);
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await act(() => new Promise(r => setTimeout(r, 30)));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });
});

describe('переход к месту (2.3)', () => {
  it('«К строкам» — часть «Строки», фокус в клетке строки 4', () => {
    const place = { place: 'lines' as const, line: 4 };
    expect(partOfTarget(place)).toBe('lines');
    document.body.innerHTML = `<div ${READINESS_ATTR}="lines">${[1, 2, 3, 4, 5].map(n =>
      `<div ${READINESS_ATTR}="${lineAnchor(n)}"><input aria-label="позиция ${n}" /></div>`).join('')}</div>`;
    Element.prototype.scrollIntoView = vi.fn();
    expect(goTo(place)).toBe(true);
    expect(document.activeElement?.getAttribute('aria-label')).toBe('позиция 4');
    document.body.innerHTML = '';
  });

  it('«Показать» замечания распознавания — часть «Кто и сколько»', () => {
    expect(partOfTarget({ place: 'part', part: 'who' })).toBe('who');
  });
});

describe('banner (2.4, 3.1)', () => {
  it('несколько «Мешает работе» — один banner, худший пункт, «и ещё N — в перечне», не больше двух действий', () => {
    const onAction = vi.fn();
    render(<InvoiceStatusBanner onAction={onAction} status={status('blocking', [
      item('recognition-failed', 'blocking', 'Файл не распознан: документ не прочитан', [
        { kind: 'recognize', label: 'Распознать ещё раз' }, { kind: 'delete', label: 'Не тот файл — удалить черновик…', reason: 'wrong-file' },
        { kind: 'retry-view', label: 'лишнее' },
      ]),
      item('lost', 'blocking', 'Удалённые записи справочников — в 1 месте: поставщик'),
      item('dup', 'check', 'Похоже на дубликат'),
    ], 'Не распознан')} />);

    expect(screen.getAllByTestId('invoice-status-banner')).toHaveLength(1);
    expect(screen.getAllByRole('alert')).toHaveLength(1);
    const banner = screen.getByTestId('invoice-status-banner');
    expect(banner.textContent).toContain('И ещё 1 — в перечне.');
    expect(banner.textContent).not.toContain('Удалённые записи');
    expect(within(banner).getAllByRole('button').map(b => b.textContent)).toEqual(['Распознать ещё раз', 'Не тот файл — удалить черновик…']);
    fireEvent.click(within(banner).getByRole('button', { name: 'Распознать ещё раз' }));
    expect(onAction).toHaveBeenCalledWith(expect.objectContaining({ kind: 'recognize' }));
  });

  it('только «Проверьте» — banner нет', () => {
    render(<InvoiceStatusBanner onAction={() => {}} status={status('check', [item('dup', 'check', 'Похоже на дубликат')], '1 замечание')} />);
    expect(screen.queryByTestId('invoice-status-banner')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
