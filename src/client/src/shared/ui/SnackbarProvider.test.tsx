// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SNACKBAR_UNDO_MS, useSnackbar, type SnackbarApi } from './Snackbar';
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

  describe('отклик закрытия с причиной (Э5)', () => {
    const undo = (onClose: (r: string) => void, onClick = () => {}) =>
      ({ duration: SNACKBAR_UNDO_MS, action: { label: 'Вернуть', onClick }, onClose });

    it('истёк срок — timeout, один раз', () => {
      const onClose = vi.fn();
      act(() => { api.success('Черновик удалён', undo(onClose)); });
      wait(SNACKBAR_UNDO_MS - 1);
      expect(onClose).not.toHaveBeenCalled();
      wait(1);
      wait(SNACKBAR_EXIT_MS);
      expect(onClose.mock.calls).toEqual([['timeout']]);
      expect(shown()).toEqual([]);
    });

    it('нажато действие — action, после самого действия', () => {
      const order: string[] = [];
      act(() => { api.success('Черновик удалён', undo(r => order.push(r), () => order.push('вернуть'))); });
      fireEvent.click(within(shown()[0]).getByRole('button', { name: 'Вернуть' }));
      wait(SNACKBAR_EXIT_MS);
      wait(SNACKBAR_UNDO_MS);
      expect(order).toEqual(['вернуть', 'action']);
    });

    it('«Сообщить» — тоже action', () => {
      const onClose = vi.fn();
      act(() => { api.error('Сбой сервера', { traceId: 't-1', onClose }); });
      fireEvent.click(within(shown()[0]).getByRole('button', { name: 'Сообщить' }));
      wait(SNACKBAR_EXIT_MS);
      expect(onClose.mock.calls).toEqual([['action']]);
    });

    it('«Закрыть» и Esc — dismissed', () => {
      const byButton = vi.fn();
      act(() => { api.error('Сбой', { onClose: byButton }); });
      fireEvent.click(within(shown()[0]).getByRole('button', { name: 'Закрыть' }));
      wait(SNACKBAR_EXIT_MS);
      expect(byButton.mock.calls).toEqual([['dismissed']]);

      const byEsc = vi.fn();
      act(() => { api.success('Черновик удалён', undo(byEsc)); });
      fireEvent.keyDown(shown()[0], { key: 'Escape' });
      wait(SNACKBAR_EXIT_MS);
      wait(SNACKBAR_UNDO_MS);
      expect(byEsc.mock.calls).toEqual([['dismissed']]);
    });

    it('смахнули — dismissed', () => {
      const onClose = vi.fn();
      const back = vi.fn();
      act(() => { api.success('Черновик удалён', undo(onClose, back)); });
      const li = shown()[0];
      Object.assign(li, { setPointerCapture: () => {}, hasPointerCapture: () => false, releasePointerCapture: () => {} });
      fireEvent.pointerDown(li, { button: 0, clientX: 0, clientY: 0, pointerId: 1 });
      fireEvent.pointerMove(li, { clientX: 10, clientY: 0, pointerId: 1 });
      fireEvent.pointerMove(li, { clientX: 120, clientY: 0, pointerId: 1 });
      fireEvent.pointerUp(li, { clientX: 120, clientY: 0, pointerId: 1 });
      // Смахивание, начатое на «Вернуть», кончается щелчком по ней — Radix его гасит, и мы тоже.
      fireEvent.click(within(li).getByRole('button', { name: 'Вернуть' }));
      wait(SNACKBAR_EXIT_MS);
      expect(onClose.mock.calls).toEqual([['dismissed']]);
      expect(back).not.toHaveBeenCalled();
    });

    it('упавшее действие не закрывает, и срок приходит timeout, а не action', () => {
      const onClose = vi.fn();
      act(() => { api.success('Черновик удалён', undo(onClose, () => { throw new Error('сбой'); })); });
      // Сбой обработчика из fireEvent не бросается: jsdom отдаёт его событием `error` окна.
      const caught: unknown[] = [];
      const swallow = (e: ErrorEvent) => { e.preventDefault(); caught.push(e.error); };
      window.addEventListener('error', swallow);
      fireEvent.click(within(shown()[0]).getByRole('button', { name: 'Вернуть' }));
      window.removeEventListener('error', swallow);
      expect(caught).toEqual([expect.objectContaining({ message: 'сбой' })]);
      wait(SNACKBAR_EXIT_MS);
      expect(texts()).toEqual([expect.stringContaining('Черновик удалён')]);
      expect(onClose).not.toHaveBeenCalled();
      wait(SNACKBAR_UNDO_MS);
      wait(SNACKBAR_EXIT_MS);
      expect(onClose.mock.calls).toEqual([['timeout']]);
    });

    it('с откликом без своего срока держится 8 с, а не 4', () => {
      const onClose = vi.fn();
      act(() => { api.success('Строка 7 удалена', { action: { label: 'Вернуть', onClick: () => {} }, onClose }); });
      wait(4000 + SNACKBAR_EXIT_MS);
      expect(texts()).toEqual([expect.stringContaining('Строка 7 удалена')]);
      wait(SNACKBAR_UNDO_MS - 4000 - SNACKBAR_EXIT_MS);
      wait(SNACKBAR_EXIT_MS);
      expect(onClose.mock.calls).toEqual([['timeout']]);
    });

    it('с откликом не стареет в очереди: прождав за ошибкой 20 с, показывается', () => {
      const onClose = vi.fn();
      act(() => {
        api.error('Сбой', { duration: 20_000 });
        api.success('Черновик удалён', undo(onClose));
      });
      wait(20_000);
      wait(SNACKBAR_EXIT_MS);
      expect(texts()).toEqual([expect.stringContaining('Черновик удалён')]);
      expect(onClose).not.toHaveBeenCalled();
    });

    it('наведение откладывает timeout', () => {
      const onClose = vi.fn();
      act(() => { api.success('Черновик удалён', undo(onClose)); });
      fireEvent.pointerMove(screen.getByRole('region'));
      wait(SNACKBAR_UNDO_MS * 2);
      expect(onClose).not.toHaveBeenCalled();
      fireEvent.pointerLeave(screen.getByRole('region'));
      wait(SNACKBAR_UNDO_MS);
      wait(SNACKBAR_EXIT_MS);
      expect(onClose.mock.calls).toEqual([['timeout']]);
    });

    it('вытесненное переполнением — dropped, не показавшись', () => {
      const onClose = vi.fn();
      act(() => {
        api.error('Сбой 0');
        api.success('Черновик удалён', undo(onClose));
        for (let i = 1; i <= 10; i++) api.error(`Сбой ${i}`);
      });
      expect(onClose.mock.calls).toEqual([['dropped']]);
      expect(texts()).toEqual([expect.stringContaining('Сбой 0')]);
    });

    it('сбой одного отклика dropped не теряет остальных и не роняет провайдер', () => {
      const report = vi.fn();
      vi.stubGlobal('reportError', report);
      const second = vi.fn();
      act(() => {
        api.error('Сбой 0');
        for (let i = 1; i <= 10; i++) api.error(`Сбой ${i}`);
        api.success('а', undo(() => { throw new Error('отклик упал'); }));
        api.success('б', undo(second));
      });
      vi.unstubAllGlobals();
      expect(report).toHaveBeenCalledWith(expect.objectContaining({ message: 'отклик упал' }));
      expect(second.mock.calls).toEqual([['dropped']]);
      expect(texts()).toEqual([expect.stringContaining('Сбой 0')]);
    });

    it('два одинаковых с откликом — оба показываются, каждый со своим откликом', () => {
      const first = vi.fn();
      const second = vi.fn();
      act(() => {
        api.success('Строка 7 удалена', undo(first));
        api.success('Строка 7 удалена', undo(second));
      });
      wait(SNACKBAR_UNDO_MS);
      wait(SNACKBAR_EXIT_MS);
      expect(first.mock.calls).toEqual([['timeout']]);
      expect(texts()).toEqual([expect.stringContaining('Строка 7 удалена')]);
      fireEvent.click(within(shown()[0]).getByRole('button', { name: 'Вернуть' }));
      wait(SNACKBAR_EXIT_MS);
      expect(second.mock.calls).toEqual([['action']]);
    });
  });

  // Radix ставит срок на паузу, пока указатель или фокус в поле, и снимает её по уходу — но слушает уход,
  // только пока в поле есть уведомление. Нажатое действие закрывает уведомление под указателем: уход
  // приходит, когда слушать уже некому, и следующее уведомление без поправки не закрывалось сроком вовсе —
  // а с ним не уходило и отложенное удаление черновика (costs-invoice-screen-13-undo).
  it('после нажатого действия следующее уведомление закрывается сроком', () => {
    const onClose = vi.fn();
    act(() => { api.success('Черновик удалён', { action: { label: 'Вернуть', onClick: () => {} }, onClose: () => {} }); });
    const button = within(shown()[0]).getByRole('button', { name: 'Вернуть' });
    fireEvent.pointerMove(button);
    fireEvent.focus(button);
    fireEvent.click(button);
    wait(SNACKBAR_EXIT_MS);
    act(() => { api.success('Черновик удалён', { action: { label: 'Вернуть', onClick: () => {} }, onClose }); });
    wait(SNACKBAR_UNDO_MS);
    wait(SNACKBAR_EXIT_MS);
    expect(onClose.mock.calls).toEqual([['timeout']]);
    expect(texts()).toEqual([]);
  });

  // Поле пересоздаётся под каждое уведомление, но фокус, стоявший в нём, туда же и возвращается: человек с
  // клавиатуры (F8, Tab, Enter на «Вернуть») не должен оказаться на <body> (ревью PR #1808).
  it('фокус в поле уведомлений переживает смену уведомления', () => {
    act(() => { api.success('Черновик удалён', { action: { label: 'Вернуть', onClick: () => {} }, onClose: () => {} }); });
    const button = within(shown()[0]).getByRole('button', { name: 'Вернуть' });
    act(() => { button.focus(); });
    fireEvent.click(button);
    wait(SNACKBAR_EXIT_MS);
    expect(document.activeElement).not.toBe(document.body);
    expect(screen.getByRole('region').contains(document.activeElement)).toBe(true);
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
