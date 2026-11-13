// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { DocumentType } from '@/shared/api/types';
import { Modal } from '@/shared/ui/Modal';
import { NewTemplateForm } from './TemplatesPage';

const mutateAsync = vi.fn();
vi.mock('@/shared/api/templates', () => ({
  useCreateTemplate: () => ({ mutateAsync: (...args: unknown[]) => mutateAsync(...args), isPending: false }),
}));

const docType = { id: 'd1', name: 'АОСР', code: 'AOSR', kind: 'Document', isAbstract: false, schema: { fields: [] } } as unknown as DocumentType;

function shown() {
  const onClose = vi.fn();
  const onCreated = vi.fn();
  render(
    <Modal open onOpenChange={() => {}} title="Новый шаблон" flushBody>
      <NewTemplateForm documentTypeId="d1" docType={docType} allDocTypes={[docType]} onClose={onClose} onCreated={onCreated} />
    </Modal>);
  return { onClose, onCreated };
}

async function submit(name: string) {
  fireEvent.change(screen.getByLabelText(/Название шаблона/), { target: { value: name } });
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Создать' })); });
}

afterEach(() => { cleanup(); mutateAsync.mockReset(); });

/** Занятое название (#1924): отказ — у поля названия, окно остаётся открытым с вводом ([ID-3]). */
describe('окно «Новый шаблон»: занятое название', () => {
  it('отказ 409 показан у поля, окно открыто, ввод на месте', async () => {
    const refusal = Object.assign(new Error('Шаблон с таким названием уже есть: «Основной».'), { response: { status: 409 } });
    mutateAsync.mockRejectedValueOnce(refusal);
    const { onClose, onCreated } = shown();

    await submit('Основной');

    const field = screen.getByLabelText(/Название шаблона/) as HTMLInputElement;
    expect(field.value).toBe('Основной');
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByText('Шаблон с таким названием уже есть: «Основной».')).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
    expect(onCreated).not.toHaveBeenCalled();
  });

  it('правка названия снимает отказ', async () => {
    mutateAsync.mockRejectedValueOnce(Object.assign(new Error('Шаблон с таким названием уже есть: «Основной».'), { response: { status: 409 } }));
    shown();
    await submit('Основной');

    fireEvent.change(screen.getByLabelText(/Название шаблона/), { target: { value: 'Основной 2' } });

    expect(screen.queryByText(/уже есть/)).toBeNull();
  });

  it('другой отказ — строкой под формой, не у поля', async () => {
    mutateAsync.mockRejectedValueOnce(Object.assign(new Error('Внутренняя ошибка сервера.'), { response: { status: 500 } }));
    shown();
    await submit('Основной');

    const field = screen.getByLabelText(/Название шаблона/);
    expect(field.getAttribute('aria-invalid')).toBeNull();
    expect(screen.getByText('Внутренняя ошибка сервера.')).toBeTruthy();
  });
});
