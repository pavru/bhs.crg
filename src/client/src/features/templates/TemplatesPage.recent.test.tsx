// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { DocumentType, Template } from '@/shared/api/types';
import type { TemplateGroup } from './groupTemplates';
import { TemplatesPage } from './TemplatesPage';

const post = vi.fn((..._args: unknown[]) => Promise.resolve());
vi.mock('@/shared/api/client', () => ({ apiClient: { post: (...args: unknown[]) => post(...args) } }));

const types = [
  { id: 't-1', name: 'АОСР', kind: 'Document', isAbstract: false },
  { id: 't-2', name: 'Протокол', kind: 'Document', isAbstract: false },
] as DocumentType[];
const version = (id: string, typeId: string, extra: Partial<Template> = {}): Template => ({
  id, documentTypeId: typeId, name: `Шаблон ${typeId}`, content: '', parameters: null, comment: null, version: 1,
  isActive: true, isDefault: true, createdAt: '', updatedAt: '', ...extra,
});
const byType: Record<string, Template[]> = {
  't-1': [version('a-1', 't-1', { isActive: false, isDefault: false }), version('a-2', 't-1', { version: 2 })],
  't-2': [version('b-1', 't-2')],
};

vi.mock('@/shared/api/documentTypes', () => ({ useListDocumentTypes: () => ({ data: types }) }));
vi.mock('@/shared/api/templates', () => ({
  useListTemplates: (typeId?: string) => ({ data: typeId ? byType[typeId] : [], isLoading: false }),
  useTemplatesUsage: () => ({ data: {} }),
  useCreateTemplate: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteTemplate: () => ({ mutateAsync: vi.fn() }),
  useDuplicateTemplate: () => ({ mutateAsync: vi.fn() }),
}));
vi.mock('@/features/settings/useMaxTemplateVersions', () => ({ useMaxTemplateVersions: () => [10] }));
vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => ({ info: vi.fn(), apiError: vi.fn() }) }));
vi.mock('./EditorPanel', () => ({ EditorPanel: ({ template }: { template: Template }) => <div data-testid="editor">{template.id}</div> }));
vi.mock('./UserLibPanel', () => ({ UserLibPanel: () => <div>библиотека</div> }));
vi.mock('./SystemLibPanel', () => ({ SystemLibPanel: () => <div>системная</div> }));
vi.mock('./TypeBlocksPanel', () => ({ TypeBlocksPanel: () => <div>блоки</div> }));
// Список версий и селектор типа — кнопками: здесь проверяется, что страница отмечает, а не как они рисуются.
vi.mock('./TemplateSidebar', () => ({
  DocTypeSelector: ({ onSelect }: { onSelect: (id: string) => void }) =>
    <button type="button" onClick={() => onSelect('t-2')}>сменить тип</button>,
  TemplateSidebar: ({ groups, onSelect }: { groups: TemplateGroup[]; onSelect: (t: Template) => void }) =>
    <div>{groups.flatMap(g => g.versions).map(t =>
      <button type="button" key={t.id} onClick={() => onSelect(t)}>выбрать {t.id}</button>)}</div>,
  VersionCleanupModal: () => null,
}));

const touched = () => post.mock.calls.filter(c => c[0] === '/account/recent').map(c => (c[1] as { id: string }).id);
const shown = (at = '/templates') => render(<MemoryRouter initialEntries={[at]}><TemplatesPage /></MemoryRouter>);

beforeEach(() => { try { localStorage.clear(); } catch { /* память необязательна */ } });
afterEach(() => { cleanup(); post.mockClear(); });

/**
 * Страница шаблонов отмечает в «Недавних» только ОТКРЫТЫЙ шаблон (shell-09, Р-6; [UI-35.1]): выбор в списке
 * или версию, названную адресом входа. Авто-выбор, смена типа и библиотеки Typst не отмечают ничего — иначе
 * заход за библиотекой заполнял бы «Недавние» шаблонами, которых никто не открывал.
 */
describe('страница шаблонов: отметка «Недавних»', () => {
  it('вход с типом без шаблона — версия выбрана сама, отметки нет', () => {
    shown('/templates?type=t-1');
    expect(screen.getByTestId('editor').textContent).toBe('a-2');
    expect(touched()).toEqual([]);
  });

  it('смена типа — шаблон по умолчанию выбран сам, отметки нет', () => {
    shown('/templates?type=t-1');
    fireEvent.click(screen.getByText('сменить тип'));
    expect(screen.getByTestId('editor').textContent).toBe('b-1');
    expect(touched()).toEqual([]);
  });

  it('библиотека Typst — отметки нет, и выбор в ней не нужен', () => {
    shown('/templates?type=t-1');
    fireEvent.click(screen.getByRole('tab', { name: /Общие функции Typst/ }));
    expect(screen.getByText('библиотека')).toBeTruthy();
    expect(touched()).toEqual([]);
  });

  it('выбор в списке — отметка этой версии', () => {
    shown('/templates?type=t-1');
    fireEvent.click(screen.getByText('выбрать a-1'));
    expect(touched()).toEqual(['a-1']);
  });

  it('вход с названной версией (из «Недавних») — отметка её', () => {
    shown('/templates?type=t-1&template=a-2');
    expect(touched()).toEqual(['a-2']);
  });

  it('названная версия мертва — подмена страницей не отмечается', () => {
    shown('/templates?type=t-1&template=gone');
    expect(screen.getByTestId('editor').textContent).toBe('a-2');
    expect(touched()).toEqual([]);
  });

  it('восстановление из памяти браузера — не открытие', () => {
    try { localStorage.setItem('templates-last', JSON.stringify({ typeId: 't-1', templateId: 'a-1' })); } catch { /* */ }
    shown();
    expect(screen.getByTestId('editor').textContent).toBe('a-1');
    expect(touched()).toEqual([]);
  });
});
