// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSnackbar } from './Snackbar';
import type { SnackbarApi } from './Snackbar';
import { SnackbarProvider } from './SnackbarProvider';
import { SNACKBAR_EXIT_MS } from './snackbarQueue';

const bugReport = vi.hoisted(() => vi.fn());
vi.mock('./bugReportBus', () => ({ openBugReport: bugReport }));

let api: SnackbarApi;
function Grab() {
  const snackbar = useSnackbar();
  useEffect(() => { api = snackbar; });
  return null;
}

function mount() {
  render(<SnackbarProvider><Grab /></SnackbarProvider>);
}

/** Видимые уведомления — пункты поля Radix; объявление для диктора — отдельный узел, его не считаем. */
const shown = () => within(screen.getByRole('region')).queryAllByRole('listitem');
const texts = () => shown().map(li => li.textContent ?? '');

/** Время проходит: отсчёт показа Radix, затем выход и сдвиг очереди. */
function wait(ms: number) { act(() => { vi.advanceTimersByTime(ms); }); }

beforeEach(() => { vi.useFakeTimers(); mount(); });
afterEach(() => { cleanup(); vi.useRealTimers(); bugReport.mockReset(); });

describe('SnackbarProvider [UI-71]', () => {
  it('два подряд — виден один, второй после закрытия первого', () => {
    act(() => { api.success('Сохранено'); api.success('Отправлено'); });
    expect(texts()).toEqual([expect.stringContaining('Сохранено')]);
    wait(4000);
    wait(SNACKBAR_EXIT_MS);
    expect(texts()).toEqual([expect.stringContaining('Отправлено')]);
  });

  it('успех при видимой ошибке ждёт её срока', () => {
    act(() => { api.error('Сбой'); api.success('Сохранено'); });
    wait(4000 + SNACKBAR_EXIT_MS);
    expect(texts()).toEqual([expect.stringContaining('Сбой')]);
    wait(4000);
    wait(SNACKBAR_EXIT_MS);
    expect(texts()).toEqual([expect.stringContaining('Сохранено')]);
  });

  it('ошибка обгоняет ожидающий успех', () => {
    act(() => { api.success('Сохранено'); api.success('Отправлено'); api.error('Сбой'); });
    wait(4000);
    wait(SNACKBAR_EXIT_MS);
    expect(texts()).toEqual([expect.stringContaining('Сбой')]);
  });

  it('ожидающий успех старше 15 с не показывается, ошибка — показывается', () => {
    act(() => {
      api.error('Сбой 1', { duration: 20_000 });
      api.success('Сохранено');
      api.error('Сбой 2');
    });
    wait(20_000);
    wait(SNACKBAR_EXIT_MS);
    expect(texts()).toEqual([expect.stringContaining('Сбой 2')]);
    wait(8000);
    wait(SNACKBAR_EXIT_MS);
    expect(shown()).toEqual([]);
  });

  it('двойной вызов — одно уведомление', () => {
    act(() => { api.success('Сохранено'); api.success('Сохранено'); });
    wait(4000);
    wait(SNACKBAR_EXIT_MS);
    expect(shown()).toEqual([]);
  });

  it('у отказа без traceId нет «Сообщить», есть «Закрыть»; у успеха нет и «Закрыть»', () => {
    act(() => { api.error('Укажите название'); });
    const [li] = shown();
    expect(within(li).queryByRole('button', { name: 'Сообщить' })).toBeNull();
    fireEvent.click(within(li).getByRole('button', { name: 'Закрыть' }));
    wait(SNACKBAR_EXIT_MS);
    act(() => { api.success('Сохранено'); });
    expect(within(shown()[0]).queryByRole('button', { name: 'Закрыть' })).toBeNull();
  });

  it('у ошибки сервера — «Сообщить», при своём действии — только оно', () => {
    act(() => { api.error('Сбой сервера', { traceId: 't-1' }); });
    fireEvent.click(within(shown()[0]).getByRole('button', { name: 'Сообщить' }));
    expect(bugReport).toHaveBeenCalledWith({ origin: 'toast', received: 'Сбой сервера' });
    // Действие закрывает уведомление — как и у Radix: дело сделано.
    wait(SNACKBAR_EXIT_MS);
    expect(shown()).toEqual([]);

    const retry = vi.fn();
    act(() => { api.error('Не сохранено', { traceId: 't-2', action: { label: 'Повторить', onClick: retry } }); });
    const li = shown()[0];
    expect(within(li).queryByRole('button', { name: 'Сообщить' })).toBeNull();
    fireEvent.click(within(li).getByRole('button', { name: 'Повторить' }));
    expect(retry).toHaveBeenCalledOnce();
  });

  it('вид MD3: inverse-surface, форма 4, уровень 3, без знака', () => {
    act(() => { api.error('Сбой'); });
    const li = shown()[0];
    expect(li.className).toContain('bg-inverse-surface');
    expect(li.className).toContain('rounded-xs');
    expect(li.className).toContain('shadow-elevation-3');
    expect(li.className).not.toMatch(/error-container|border/);
    expect(li.querySelectorAll('svg')).toHaveLength(1); // только знак «Закрыть»
  });
});
