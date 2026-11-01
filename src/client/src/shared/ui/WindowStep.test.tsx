// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { Modal, ModalExit, ModalPrimary } from './Modal';
import { WindowStep } from './WindowStep';
import { useOwnWindow } from './modalControl';

/**
 * Шаг окна вместо окна поверх окна (issue #1384): внутри окна шаг подменяет заголовок, тело и подвал,
 * а форма окна остаётся под ним со всем набранным; снаружи окна шаг — окно сам.
 */

afterEach(cleanup);

const dialogs = () => screen.queryAllByRole('dialog');
const title = () => dialogs()[0]?.querySelector('span[title]')?.textContent;
async function escape() {
  fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
}

/** Окно с полем и кнопкой, открывающей шаг; шаг может открыть второй шаг. */
function Host({ onClose = vi.fn(), dirtyStep = false }: { onClose?: () => void; dirtyStep?: boolean }) {
  const [step, setStep] = useState(false);
  const [inner, setInner] = useState(false);
  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Строка" isDirty={false}
      footer={<button type="button">Готово</button>}>
      <input aria-label="поле" />
      <button type="button" onClick={() => setStep(true)}>Вынести</button>
      {step && (
        <WindowStep title="Вынос" onBack={() => setStep(false)} isDirty={dirtyStep || undefined}
          footer={<><ModalExit /><button type="button" onClick={() => setInner(true)}>Дальше</button></>}>
          <p>тело шага</p>
          <input aria-label="поле шага" />
          {inner && (
            <WindowStep title="Второй шаг" onBack={() => setInner(false)} footer={<ModalExit>Назад</ModalExit>}>
              <p>тело второго шага</p>
            </WindowStep>
          )}
        </WindowStep>
      )}
    </Modal>
  );
}

describe('WindowStep', () => {
  it('снаружи окна — окно сам, и «Отмена» уходит с него', () => {
    const onBack = vi.fn();
    render(<WindowStep title="Вынос" onBack={onBack} footer={<ModalExit />}><p>тело</p></WindowStep>);
    expect(dialogs()).toHaveLength(1);
    expect(screen.getByText('тело')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(onBack).toHaveBeenCalled();
  });

  it('внутри окна — шаг этого окна: второго окна нет, форма скрыта, заголовок и подвал — шага', () => {
    render(<Host />);
    fireEvent.click(screen.getByRole('button', { name: 'Вынести' }));
    expect(dialogs()).toHaveLength(1);
    expect(title()).toBe('Вынос');
    expect(screen.getByText('тело шага')).toBeTruthy();
    expect(screen.queryByRole('textbox', { name: 'поле' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Готово' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Дальше' })).toBeTruthy();
  });

  it('«Отмена» на шаге возвращает к форме с набранным, а не закрывает окно', () => {
    const onClose = vi.fn();
    render(<Host onClose={onClose} />);
    fireEvent.change(screen.getByLabelText('поле'), { target: { value: 'набрано' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вынести' }));
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(onClose).not.toHaveBeenCalled();
    expect(title()).toBe('Строка');
    expect((screen.getByRole('textbox', { name: 'поле' }) as HTMLInputElement).value).toBe('набрано');
    expect(screen.getByRole('button', { name: 'Готово' })).toBeTruthy();
  });

  it('Esc на шаге — назад к форме', async () => {
    const onClose = vi.fn();
    render(<Host onClose={onClose} />);
    fireEvent.click(screen.getByRole('button', { name: 'Вынести' }));
    await escape();
    expect(onClose).not.toHaveBeenCalled();
    expect(title()).toBe('Строка');
  });

  it('шаг на шаге: виден верхний, выход с него — к нижнему', () => {
    render(<Host />);
    fireEvent.click(screen.getByRole('button', { name: 'Вынести' }));
    fireEvent.click(screen.getByRole('button', { name: 'Дальше' }));
    expect(title()).toBe('Второй шаг');
    // Нижний шаг смонтирован и скрыт — его состояние доживёт до возврата.
    expect(screen.getByText('тело шага').closest('[hidden]')).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Назад' }));
    expect(title()).toBe('Вынос');
    expect(screen.getByText('тело шага').closest('[hidden]')).toBeNull();
  });

  it('с набранным на шаге выход спрашивает, и «Закрыть» уходит только с шага', () => {
    const onClose = vi.fn();
    render(<Host onClose={onClose} dirtyStep />);
    fireEvent.click(screen.getByRole('button', { name: 'Вынести' }));
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(screen.getByText('Закрыть без сохранения?')).toBeTruthy();
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Закрыть' }));
    expect(onClose).not.toHaveBeenCalled();
    expect(title()).toBe('Строка');
  });
});

describe('WindowStep: набранное и фокус', () => {
  it('без isDirty шаг судит по вводу сам: Esc после набора не уходит с шага, а показывает выход', async () => {
    const onClose = vi.fn();
    render(<Host onClose={onClose} />);
    fireEvent.click(screen.getByRole('button', { name: 'Вынести' }));
    fireEvent.change(screen.getByLabelText('поле шага'), { target: { value: 'набрано' } });
    await escape();
    expect(title()).toBe('Вынос');
    expect((screen.getByLabelText('поле шага') as HTMLInputElement).value).toBe('набрано');
    expect(document.activeElement?.textContent).toBe('Отмена');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('набранное в форме окна шагу не приписывается: Esc на чистом шаге уходит с него', async () => {
    render(<Host />);
    fireEvent.change(screen.getByLabelText('поле'), { target: { value: 'набрано' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вынести' }));
    await escape();
    expect(title()).toBe('Строка');
  });

  it('фокус: открыли шаг — уходит со скрытой кнопки; сняли верхний шаг — к кнопке, что его открыла', async () => {
    render(<Host />);
    const open = screen.getByRole('button', { name: 'Вынести' });
    open.focus();
    fireEvent.click(open);
    await act(async () => {});
    expect(document.activeElement?.closest('[hidden]')).toBeNull();
    expect(document.activeElement).not.toBe(open);

    const next = screen.getByRole('button', { name: 'Дальше' });
    next.focus();
    fireEvent.click(next);
    await act(async () => {});
    fireEvent.click(screen.getByRole('button', { name: 'Назад' }));
    await act(async () => {});
    expect(document.activeElement).toBe(next);

    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    await act(async () => {});
    expect(document.activeElement).toBe(open);
  });
});

describe('useOwnWindow', () => {
  function Probe() { return <span>{useOwnWindow(true) ? 'своё окно' : 'на месте'}</span>; }

  it('снаружи окна — своё окно, внутри — на месте', () => {
    render(<Probe />);
    expect(screen.getByText('своё окно')).toBeTruthy();
    cleanup();
    render(<Modal open onOpenChange={() => {}} title="Окно" nothingToLose><Probe /></Modal>);
    expect(screen.getByText('на месте')).toBeTruthy();
  });
});

describe('шаг на узком экране ([UI-75])', () => {
  const realMatchMedia = window.matchMedia;
  // Экран уже 768 ([UI-75]): «уже» отвечает да, «шире» — нет.
  beforeEach(() => {
    window.matchMedia = vi.fn().mockImplementation((q: string) => ({
      matches: q.includes('max-width'), addEventListener() {}, removeEventListener() {},
    })) as unknown as typeof window.matchMedia;
  });
  afterEach(() => { window.matchMedia = realMatchMedia; });
  const tick = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

  function WithStep() {
    const [step, setStep] = useState(false);
    return (
      <Modal open onOpenChange={() => {}} title="Строка счёта"
        footer={<><ModalExit /><ModalPrimary onClick={() => setStep(true)}>Новая позиция</ModalPrimary></>}>
        <p>Форма окна</p>
        {step && (
          <WindowStep title="Новая позиция номенклатуры" onBack={() => setStep(false)}
            footer={<><ModalExit>К поиску</ModalExit><ModalPrimary>Завести</ModalPrimary></>}>
            <p>Форма шага</p>
          </WindowStep>
        )}
      </Modal>
    );
  }

  it('у ведущего края — стрелка «Назад», она возвращает к форме окна; в шапке — главное шага', async () => {
    render(<WithStep />);
    fireEvent.click(screen.getByRole('button', { name: 'Новая позиция' }));
    await tick();
    expect(screen.getByText('Форма шага')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Завести' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Новая позиция' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Назад' }));
    await tick();
    expect(screen.queryByText('Форма шага')).toBeNull();
    expect(screen.getByRole('button', { name: 'Закрыть' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Новая позиция' })).toBeTruthy();
  });
});
