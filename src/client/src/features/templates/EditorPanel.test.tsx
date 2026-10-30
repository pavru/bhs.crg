// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { DocumentType, Template } from '@/shared/api/types';

/** «Сохранить» в редакторе шаблона — split-кнопка ([UI-83]). Монако и сеть здесь ни при чём — заглушки. */

const saveContent = vi.fn();
vi.mock('@/shared/api/templates', () => ({
  useSaveTemplateContent: () => ({ mutateAsync: saveContent, isPending: false }),
  useCreateTemplateVersion: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useSetTemplateDefault: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));
vi.mock('@/shared/ui/CodeEditor', () => ({ default: () => <div data-testid="editor" /> }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ resolvedTheme: 'light' }) }));
vi.mock('@/shared/ui/typstUserLibCompletion', () => ({ useUserLibCompletion: () => {} }));
vi.mock('@/shared/ui/typstAssetCompletion', () => ({ useAssetCompletion: () => {} }));
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => ({ info: vi.fn(), success: vi.fn(), error: vi.fn() }) }));
vi.mock('./TemplateParamsPanel', () => ({ TemplateParamsPanel: () => null }));
vi.mock('./TemplateAssetsPanel', () => ({ TemplateAssetsPanel: () => null }));

const { EditorPanel } = await import('./EditorPanel');

afterEach(() => { cleanup(); saveContent.mockReset(); });

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
