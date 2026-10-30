// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useEffect, useRef } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { DocumentType, Template } from '@/shared/api/types';

/**
 * «Сохранить» в редакторе шаблона — split-кнопка ([UI-83]); «Реквизит» — меню Radix ([UI-51]). Монако и
 * сеть здесь ни при чём — заглушки: вместо редактора поле ввода, которое умеет фокус и записывает вставки.
 */

const saveContent = vi.fn();
vi.mock('@/shared/api/templates', () => ({
  useSaveTemplateContent: () => ({ mutateAsync: saveContent, isPending: false }),
  useCreateTemplateVersion: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useSetTemplateDefault: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));
const executeEdits = vi.fn();
vi.mock('@/shared/ui/CodeEditor', () => ({
  default: function FakeEditor({ onMount }: { onMount: (editor: unknown) => void }) {
    const area = useRef<HTMLTextAreaElement>(null);
    useEffect(() => {
      onMount({
        getSelection: () => ({ startLineNumber: 1 }),
        getModel: () => null,
        executeEdits,
        focus: () => area.current?.focus(),
        hasTextFocus: () => document.activeElement === area.current,
      });
    }, [onMount]);
    return <textarea data-testid="editor" ref={area} />;
  },
}));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ resolvedTheme: 'light' }) }));
vi.mock('@/shared/ui/typstUserLibCompletion', () => ({ useUserLibCompletion: () => {} }));
vi.mock('@/shared/ui/typstAssetCompletion', () => ({ useAssetCompletion: () => {} }));
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => ({ info: vi.fn(), success: vi.fn(), error: vi.fn() }) }));
vi.mock('./TemplateParamsPanel', () => ({ TemplateParamsPanel: () => null }));
vi.mock('./TemplateAssetsPanel', () => ({ TemplateAssetsPanel: () => null }));

const { EditorPanel } = await import('./EditorPanel');

afterEach(() => { cleanup(); saveContent.mockReset(); executeEdits.mockReset(); });

const template = (isActive: boolean): Template => ({
  id: 't1', documentTypeId: 'd1', name: 'АОСР', content: '= АОСР', parameters: null, comment: null,
  version: 2, isActive, isDefault: true, createdAt: '2026-10-30T00:00:00Z', updatedAt: '2026-10-30T00:00:00Z',
});
const docType = { id: 'd1', name: 'АОСР', schema: '{"fields":[]}' } as unknown as DocumentType;

const renderPanel = (isActive: boolean) =>
  render(<EditorPanel template={template(isActive)} docType={docType} allDocTypes={[docType]} onSaved={vi.fn()} />);

const MENU = 'Ещё варианты сохранения';
// Пункт меню выполняется, когда Radix вернул фокус на стрелку, а это он делает тиком после закрытия меню.
const tick = () => new Promise(r => setTimeout(r, 0));
async function openMenu() {
  const arrow = screen.getByRole('button', { name: MENU });
  await act(async () => { arrow.focus(); fireEvent.keyDown(arrow, { key: 'Enter' }); });
  return arrow;
}

describe('EditorPanel: «Сохранить»', () => {
  it('пункт меню стрелки открывает окно новой версии', async () => {
    renderPanel(true);
    await openMenu();
    const item = screen.getByRole('menuitem', { name: /Сохранить как новую версию…/ });
    expect(item.textContent).toContain('Создаст v3');
    await act(async () => { fireEvent.keyDown(item, { key: 'Enter' }); }); await act(tick);
    expect(screen.getByRole('button', { name: 'Создать v3' })).toBeTruthy();
  });

  it('окно новой версии закрылось — фокус на стрелке, а не в body', async () => {
    renderPanel(true);
    const arrow = await openMenu();
    await act(async () => { fireEvent.keyDown(screen.getByRole('menuitem'), { key: 'Enter' }); }); await act(tick);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); }); await act(tick);
    expect(screen.queryByRole('button', { name: 'Создать v3' })).toBeNull();
    expect(document.activeElement).toBe(arrow);
  });

  it('у исторической версии — одна кнопка, стрелки нет', () => {
    renderPanel(false);
    expect(screen.getByRole('button', { name: /Сохранить как новую/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: MENU })).toBeNull();
  });

  it('Ctrl+S при открытом меню — браузерное «Сохранить страницу» погашено, сохранения нет', async () => {
    renderPanel(true);
    await openMenu();
    const target = document.activeElement ?? document.body;
    const notCancelled = fireEvent.keyDown(target, { code: 'KeyS', key: 's', ctrlKey: true });
    expect(notCancelled).toBe(false);
    expect(saveContent).not.toHaveBeenCalled();
  });
});

describe('EditorPanel: «Реквизит»', () => {
  // Составное поле «object» раскрывается полями типа «Объект»: в меню оно — заголовок группы.
  const objectType = {
    id: 'd2', name: 'Объект', schema: { fields: [{ key: 'name', title: 'Наименование', type: 'string' }] },
  } as unknown as DocumentType;
  const withFields = {
    id: 'd1', name: 'АОСР',
    schema: { fields: [
      { key: 'number', title: 'Номер', type: 'string' },
      { key: 'object', title: 'Объект', type: 'complex', typeId: 'd2' },
    ] },
  } as unknown as DocumentType;
  const renderWithFields = () => render(
    <EditorPanel template={template(true)} docType={withFields} allDocTypes={[withFields, objectType]} onSaved={vi.fn()} />);

  async function openPicker() {
    const trigger = screen.getByRole('button', { name: /Реквизит/ });
    await act(async () => { trigger.focus(); fireEvent.keyDown(trigger, { key: 'Enter' }); });
    return trigger;
  }

  it('составное поле — заголовок группы, а не пункт: стрелка его перешагивает', async () => {
    renderWithFields();
    await openPicker();
    const items = screen.getAllByRole('menuitem');
    expect(items.map(i => i.querySelector('code')?.textContent)).toEqual(['number', 'object.name']);
    expect(document.activeElement).toBe(items[0]);
    // Стрелку Radix отрабатывает тиком (setTimeout в RovingFocus).
    await act(async () => { fireEvent.keyDown(items[0], { key: 'ArrowDown' }); }); await act(tick);
    expect(document.activeElement).toBe(items[1]);
  });

  it('Enter вставляет #get("<путь>") на место курсора, фокус — в редакторе', async () => {
    renderWithFields();
    await openPicker();
    const item = screen.getAllByRole('menuitem')[1];
    await act(async () => { fireEvent.keyDown(item, { key: 'Enter' }); }); await act(tick);
    expect(screen.queryByRole('menu')).toBeNull();
    expect(executeEdits).toHaveBeenCalledWith('insert-requisite', [expect.objectContaining({ text: '#get("object.name")' })]);
    expect(document.activeElement).toBe(screen.getByTestId('editor'));
  });

  it('Esc закрывает меню без вставки, фокус — на «Реквизит»', async () => {
    renderWithFields();
    const trigger = await openPicker();
    await act(async () => { fireEvent.keyDown(screen.getAllByRole('menuitem')[0], { key: 'Escape' }); }); await act(tick);
    expect(screen.queryByRole('menu')).toBeNull();
    expect(executeEdits).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(trigger);
  });
});
