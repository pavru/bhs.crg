// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { Modal, ModalAction, ModalExit, ModalNote, ModalPrimary } from './Modal';
import { ConfirmDialog } from './ConfirmDialog';

/**
 * Действия окна с ролью и окно во весь экран (sharedui-md3-13, [UI-75]): на столе — подвал, как был; на
 * экране уже 768 и у `fullScreen` — шапка с крестиком, главным действием и «⋮», подвала нет.
 */

afterEach(cleanup);

const realMatchMedia = window.matchMedia;

/** Ширина экрана для `useMedia`: «уже 768» отвечает `narrow`, «шире» — обратное. */
function screenWidth(narrow: boolean) {
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: q.includes('max-width') ? narrow : !narrow, addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
}

afterEach(() => { window.matchMedia = realMatchMedia; });

function Window({ busy = false, onSave = vi.fn(), onDelete = vi.fn(), fullScreen = false }: {
  busy?: boolean; onSave?: () => void; onDelete?: () => void; fullScreen?: boolean;
}) {
  return (
    <Modal open onOpenChange={() => {}} title="Позиция" busy={busy} fullScreen={fullScreen}
      footer={<>
        <ModalNote>Изменение войдёт в счёт</ModalNote>
        <ModalAction danger lead onClick={onDelete}>Удалить</ModalAction>
        <ModalExit />
        <ModalPrimary onClick={onSave}>Сохранить</ModalPrimary>
      </>}>
      <input aria-label="Имя" />
    </Modal>
  );
}

describe('на столе: подвал из ролей', () => {
  beforeEach(() => screenWidth(false));

  it('кнопки — в объявленном порядке, пояснение и ведущее действие помечены у ведущего края', () => {
    render(<Window />);
    const footer = screen.getByText('Изменение войдёт в счёт').parentElement!.parentElement!;
    const names = within(footer).getAllByRole('button').map(b => b.textContent);
    expect(names).toEqual(['Удалить', 'Отмена', 'Сохранить']);
    expect(screen.getByText('Изменение войдёт в счёт').hasAttribute('data-lead')).toBe(true);
    expect(screen.getByRole('button', { name: 'Удалить' }).hasAttribute('data-lead')).toBe(true);
    expect(screen.queryByRole('button', { name: 'Закрыть' })).toBeNull();
  });

  it('busy гасит все кнопки с ролью: выход — без нажатия, остальные — недоступны', () => {
    render(<Window busy />);
    expect(screen.getByRole('button', { name: 'Отмена' }).getAttribute('aria-disabled')).toBe('true');
    expect((screen.getByRole('button', { name: 'Удалить' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('во весь экран: шапка вместо подвала', () => {
  beforeEach(() => screenWidth(true));

  it('на узком экране: крестик, заголовок, главное в шапке; «Отмены» и подвала нет', () => {
    const onSave = vi.fn();
    render(<Window onSave={onSave} />);
    expect(screen.getByRole('button', { name: 'Закрыть' }).hasAttribute('data-modal-exit')).toBe(true);
    expect(screen.queryByRole('button', { name: 'Отмена' })).toBeNull();
    expect(screen.getByText('Изменение войдёт в счёт').tagName).toBe('P');
    const save = screen.getByRole('button', { name: 'Сохранить' });
    // Главное — кнопкой text в шапке: подвал скрыт, и найденная кнопка — не из него.
    expect(save.closest('[hidden]')).toBeNull();
    fireEvent.click(save);
    expect(onSave).toHaveBeenCalled();
  });

  it('прочее — пунктом «⋮», «Удалить» тона ошибки', async () => {
    const onDelete = vi.fn();
    render(<Window onDelete={onDelete} />);
    expect(screen.queryByRole('button', { name: 'Удалить' })).toBeNull();
    const more = screen.getByRole('button', { name: 'Другие действия' });
    await act(async () => { more.focus(); fireEvent.keyDown(more, { key: 'Enter' }); });
    const item = screen.getByRole('menuitem', { name: 'Удалить' });
    expect(item.outerHTML).toContain('text-error');
    await act(async () => { fireEvent.click(item); });
    expect(onDelete).toHaveBeenCalled();
  });

  it('busy: главное в шапке недоступно, пункт «⋮» тоже', async () => {
    render(<Window busy />);
    expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(true);
    const more = screen.getByRole('button', { name: 'Другие действия' });
    await act(async () => { more.focus(); fireEvent.keyDown(more, { key: 'Enter' }); });
    expect(screen.getByRole('menuitem', { name: 'Удалить' }).hasAttribute('data-disabled')).toBe(true);
  });

  it('крестик при правках спрашивает, как «Отмена» и Esc', () => {
    const onOpenChange = vi.fn();
    render(
      <Modal open onOpenChange={onOpenChange} title="Позиция" isDirty footer={<ModalPrimary>Сохранить</ModalPrimary>}>
        <input aria-label="Имя" />
      </Modal>);
    fireEvent.change(screen.getByLabelText('Имя'), { target: { value: 'кабель' } });
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    expect(screen.getByText('Закрыть без сохранения?')).toBeTruthy();
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it('fullScreen — во весь экран и на столе', () => {
    screenWidth(false);
    render(<Window fullScreen />);
    expect(screen.getByRole('button', { name: 'Закрыть' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Отмена' })).toBeNull();
    expect(screen.getByRole('dialog').className).toContain('inset-0');
  });
});

describe('подтверждение на узком экране', () => {
  beforeEach(() => screenWidth(true));

  it('остаётся basic dialog: не шире 384 и с отступами 24, а не во весь экран', () => {
    render(<ConfirmDialog open onOpenChange={() => {}} title="Удалить позицию?" confirmLabel="Удалить" onConfirm={() => {}} />);
    const dialog = screen.getByRole('dialog');
    expect(dialog.className).toContain('max-w-sm');
    expect(dialog.className).toContain('inset-x-6');
    expect(dialog.className).not.toContain('inset-0');
  });
});
