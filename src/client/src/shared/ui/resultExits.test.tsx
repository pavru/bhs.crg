// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ConfirmDialog } from './ConfirmDialog';
import { ErrorBoundary } from './ErrorBoundary';
import { Modal } from './Modal';

/**
 * Результат читают (issue #1237): у окна, которое что-то сообщило, выход один и назван — промах мимо
 * окна и Esc, нажатый по привычке, его не закрывают.
 */

afterEach(cleanup);

const tick = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

/** Radix отвечает на нажатие мимо только по следующему `click` — поэтому событий два. */
async function missAndEscape() {
  await tick();
  fireEvent.pointerDown(document.body);
  fireEvent.click(document.body);
  fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
  await tick();
}

const closed = (fn: ReturnType<typeof vi.fn>) => fn.mock.calls.some(c => c[0] === false);

describe('подтверждение с отказом', () => {
  function shown(onConfirm: () => Promise<unknown>, blocked?: string) {
    const onOpenChange = vi.fn();
    render(<ConfirmDialog open onOpenChange={onOpenChange} title="Удалить запись?"
      confirmLabel="Удалить запись" blocked={blocked} onConfirm={onConfirm} />);
    return onOpenChange;
  }

  const refuse = () => Promise.reject({ response: { status: 409, data: { error: 'Запись используется в двух документах.' } } });

  it('вопрос закрывается и промахом, и Esc — это «Отмена»', async () => {
    const byMiss = shown(() => Promise.resolve());
    await tick();
    fireEvent.pointerDown(document.body);
    fireEvent.click(document.body);
    expect(closed(byMiss)).toBe(true);
    cleanup();

    const byEscape = shown(() => Promise.resolve());
    await tick();
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
    expect(closed(byEscape)).toBe(true);
  });

  it('после отказа сервера причину не закрывают ни промах, ни Esc — только «Понятно»', async () => {
    const onOpenChange = shown(refuse);
    fireEvent.click(screen.getByRole('button', { name: 'Удалить запись' }));
    await tick();
    expect(screen.getByText('Запись используется в двух документах.')).toBeTruthy();

    // Выход под рукой и у клавиатуры: кнопка, на которой стоял фокус, с отказом исчезла.
    const exit = screen.getByRole('button', { name: 'Понятно' });
    expect(document.activeElement).toBe(exit);

    await missAndEscape();
    expect(onOpenChange).not.toHaveBeenCalled();
    fireEvent.click(exit);
    expect(closed(onOpenChange)).toBe(true);
  });

  // Тишина неотличима от зависшего окна: правило то же, что у `Modal` в состоянии «результат».
  it('отказ на промах и Esc отвечает: кольцо вокруг выхода, по Esc — ещё и фокус', async () => {
    shown(refuse);
    fireEvent.click(screen.getByRole('button', { name: 'Удалить запись' }));
    await tick();
    const exit = screen.getByRole('button', { name: 'Понятно' });
    // Человек щёлкнул в текст причины — фокус с выхода ушёл.
    exit.blur();

    fireEvent.pointerDown(document.body);
    fireEvent.click(document.body);
    expect(exit.hasAttribute('data-modal-hint')).toBe(true);
    expect(document.activeElement).not.toBe(exit);

    exit.removeAttribute('data-modal-hint');
    fireEvent.keyDown(document.body, { key: 'Escape' });
    expect(exit.hasAttribute('data-modal-hint')).toBe(true);
    expect(document.activeElement).toBe(exit);
  });

  it('отказ, известный заранее, держится так же', async () => {
    const onOpenChange = shown(() => Promise.resolve(), 'Период закрыт.');
    await missAndEscape();
    expect(onOpenChange).not.toHaveBeenCalled();
  });
});

describe('панель ошибки в окне без шапки', () => {
  function Broken(): never { throw new Error('поле схемы без типа'); }

  it('«Закрыть» выводит из упавшего редактора, не дожидаясь клавиши', () => {
    // React пишет пойманное в консоль дважды — в отчёте теста это шум, а не сведения.
    const silenced = vi.spyOn(console, 'error').mockImplementation(() => {});
    const leave = vi.fn();
    // Как редактор кода типа: во весь экран и без шапки — крестика у окна нет.
    render(
      <Modal open onOpenChange={() => {}} title="Документ" fullScreen headerless flushBody>
        <ErrorBoundary variant="inline" allowReload title="Не удалось открыть документ" onClose={leave}>
          <Broken />
        </ErrorBoundary>
      </Modal>,
    );
    silenced.mockRestore();

    expect(screen.getByText('Не удалось открыть документ')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    expect(leave).toHaveBeenCalledTimes(1);
  });

  // Упавший редактор признак правок уже не снимет: окно остаётся «с правками», и Esc его не
  // закрывает. Без метки выхода он не находил бы и кнопку — окно молчало бы.
  it('в окне с несохранёнными правками Esc находит «Закрыть», а она спрашивает о потере', async () => {
    const silenced = vi.spyOn(console, 'error').mockImplementation(() => {});
    const onOpenChange = vi.fn();
    render(
      <Modal open onOpenChange={onOpenChange} title="Документ" fullScreen headerless flushBody isDirty>
        {requestClose => (
          <ErrorBoundary variant="inline" title="Не удалось открыть документ" onClose={requestClose}>
            <Broken />
          </ErrorBoundary>
        )}
      </Modal>,
    );
    silenced.mockRestore();
    await tick();

    const exit = screen.getByRole('button', { name: 'Закрыть' });
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
    await tick();
    expect(onOpenChange).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(exit);

    fireEvent.click(exit);
    expect(screen.getByText('Закрыть без сохранения?')).toBeTruthy();
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it('без выхода кнопки нет — границе страницы закрывать нечего', () => {
    const silenced = vi.spyOn(console, 'error').mockImplementation(() => {});
    render(<ErrorBoundary variant="page"><Broken /></ErrorBoundary>);
    silenced.mockRestore();
    expect(screen.queryByRole('button', { name: 'Закрыть' })).toBeNull();
  });
});
