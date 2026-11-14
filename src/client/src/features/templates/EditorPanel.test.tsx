// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useEffect, useRef } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { DocumentType, TemplateSummary } from '@/shared/api/types';
import type { NewVersionPlan } from './groupTemplates';

/**
 * «Сохранить» в редакторе шаблона — split-кнопка ([UI-83]); «Реквизит» — меню Radix ([UI-51]). Монако и
 * сеть здесь ни при чём — заглушки: вместо редактора поле ввода, которое умеет фокус и записывает вставки.
 */

const saveContent = vi.fn();
// Текст версии — своим запросом (issue #1972): по id из «кэша» contents; contentState — когда
// нужен сам ответ запроса (загрузка, отказ).
const DEFAULT_CONTENTS = { t1: '= АОСР' };
let contents: Record<string, string> = { ...DEFAULT_CONTENTS };
let contentState: { data?: string; isError?: boolean; error?: unknown } | null = null;
vi.mock('@/shared/api/templates', () => ({
  useTemplateContent: (id: string) => contentState ?? { data: contents[id] },
  useSaveTemplateContent: () => ({ mutateAsync: saveContent, isPending: false }),
  useCreateTemplateVersion: () => ({ mutateAsync: (...args: unknown[]) => createVersion(...args), isPending: false }),
  useSetTemplateDefault: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));
const invalidateQueries = vi.fn();
const createVersion = vi.fn();
vi.mock('@tanstack/react-query', async (orig) => ({
  ...(await orig<typeof import('@tanstack/react-query')>()),
  useQueryClient: () => ({ invalidateQueries }),
}));
const executeEdits = vi.fn();
vi.mock('@/shared/ui/CodeEditor', () => ({
  default: function FakeEditor({ onMount, value, onChange }:
    { onMount: (editor: unknown) => void; value: string; onChange: (v: string | undefined) => void }) {
    const area = useRef<HTMLTextAreaElement>(null);
    useEffect(() => {
      onMount({
        getSelection: () => ({ startLineNumber: 1 }),
        getModel: () => null,
        getValue: () => area.current?.value ?? '',
        executeEdits,
        focus: () => area.current?.focus(),
        hasTextFocus: () => document.activeElement === area.current,
      });
    }, [onMount]);
    return <textarea data-testid="editor" ref={area} value={value} onChange={e => onChange(e.target.value)} />;
  },
}));
vi.mock('@/shared/ui/typstUserLibCompletion', () => ({ useUserLibCompletion: () => {} }));
vi.mock('@/shared/ui/typstAssetCompletion', () => ({ useAssetCompletion: () => {} }));
vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => ({ info: vi.fn(), success: vi.fn(), error: vi.fn() }) }));
vi.mock('./TemplateParamsPanel', () => ({ TemplateParamsPanel: () => null }));
vi.mock('./TemplateAssetsPanel', () => ({ TemplateAssetsPanel: () => null }));

const { EditorPanel } = await import('./EditorPanel');

afterEach(() => { contentState = null; contents = { ...DEFAULT_CONTENTS }; cleanup(); saveContent.mockReset(); executeEdits.mockReset(); invalidateQueries.mockReset(); createVersion.mockReset(); });

const template = (isActive: boolean): TemplateSummary => ({
  id: 't1', documentTypeId: 'd1', name: 'АОСР', parameters: null, comment: null,
  version: 2, isActive, isDefault: true, createdAt: '2026-10-30T00:00:00Z', updatedAt: '2026-10-30T00:00:00Z',
});
const docType = { id: 'd1', name: 'АОСР', schema: '{"fields":[]}' } as unknown as DocumentType;

const NO_RETIRE: NewVersionPlan = { next: 3, retires: [], takesDefault: false, pinnedToRetired: 0, noPinDocsMove: true };
const renderPanel = (isActive: boolean, plan: NewVersionPlan = NO_RETIRE) =>
  render(<EditorPanel template={template(isActive)} docType={docType} allDocTypes={[docType]} onSaved={vi.fn()} versionPlan={plan} />);

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
    <EditorPanel template={template(true)} docType={withFields} allDocTypes={[withFields, objectType]} onSaved={vi.fn()} versionPlan={NO_RETIRE} />);

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

  it('Esc закрывает меню без вставки, фокус — на «Реквизит», и его подсказка не открывается сама', async () => {
    // В jsdom программный фокус не :focus-visible, а в браузере после Esc — да: без этой подмены
    // подсказка не открылась бы и без отметки фокуса слоя, и тест молчал бы.
    const matches = Element.prototype.matches;
    const spy = vi.spyOn(Element.prototype, 'matches')
      .mockImplementation(function (this: Element, sel: string) { return sel === ':focus-visible' || matches.call(this, sel); });
    try {
      renderWithFields();
      const trigger = await openPicker();
      await act(async () => { fireEvent.keyDown(screen.getAllByRole('menuitem')[0], { key: 'Escape' }); }); await act(tick);
      expect(screen.queryByRole('menu')).toBeNull();
      expect(executeEdits).not.toHaveBeenCalled();
      expect(document.activeElement).toBe(trigger);
      expect(screen.queryByRole('tooltip')).toBeNull();
    } finally { spy.mockRestore(); }
  });
});

describe('окно новой версии от исторической (issue #1920)', () => {
  it('называет действующую версию, которая уйдёт в историю, и закреплённые за ней документы', async () => {
    const v4 = { ...template(true), id: 't4', version: 4 };
    renderPanel(false, { next: 5, retires: [v4], takesDefault: true, pinnedToRetired: 2, noPinDocsMove: true });

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /Сохранить как новую/ })); });

    expect(screen.getByRole('button', { name: 'Создать v5' })).toBeTruthy();
    const text = screen.getByText(/уйдёт в историю/).textContent ?? '';
    expect(text).toContain('Действующая v4');
    expect(text).toContain('«по умолчанию»');
    expect(text).toContain('без выбранного шаблона перейдут на v5');
    // Закреплённые за гаснущей следуют за шаблоном (#1923, вариант А), а не «выбирают заново».
    expect(text).toContain('2 документа перейдут на новую версию и вернутся в черновик');
    expect(text).not.toContain('выбрать шаблон заново');
    // План берётся из списка страницы — при открытии окна он перечитывается.
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['templates', 'd1'] });
  });

  it('у действующей версии — прежний текст', async () => {
    renderPanel(false);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /Сохранить как новую/ })); });
    expect(screen.getByText(/Текущая версия сохранится в истории/)).toBeTruthy();
  });

  it('отказ «шаблон тем временем изменили» — в окне, окно открыто, план перечитан (ревью PR #1958)', async () => {
    createVersion.mockRejectedValueOnce(Object.assign(new Error('Request failed'), {
      response: { status: 409, data: { error: 'Шаблон тем временем изменили — откройте его заново.' } },
    }));
    renderPanel(false);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /Сохранить как новую/ })); });
    invalidateQueries.mockClear();

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Создать v3' })); });

    const dialog = screen.getByRole('dialog');
    expect(dialog.textContent).toContain('Шаблон тем временем изменили — откройте его заново.');
    expect(screen.getByRole('button', { name: 'Создать v3' })).toBeTruthy();
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['templates', 'd1'] });
  });

  it('за действующей закреплено три документа — окно говорит, что они перейдут (#1923)', async () => {
    renderPanel(false, { ...NO_RETIRE, pinnedToRetired: 3 });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /Сохранить как новую/ })); });
    expect(screen.getByText(/Текущая версия сохранится в истории/).textContent)
      .toContain('3 документа перейдут на новую версию и вернутся в черновик');
  });

  it('один документ — глагол в единственном числе', async () => {
    renderPanel(false, { ...NO_RETIRE, pinnedToRetired: 1 });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /Сохранить как новую/ })); });
    expect(screen.getByText(/Текущая версия сохранится в истории/).textContent)
      .toContain('1 документ перейдёт на новую версию и вернётся в черновик');
  });
});

describe('EditorPanel: текст версии своим запросом (issue #1972)', () => {
  it('пока текст не получен — редактора нет: Ctrl+S не записал бы пустой шаблон', () => {
    contentState = {};
    renderPanel(true);
    expect(screen.queryByTestId('editor')).toBeNull();
    expect(screen.getByRole('progressbar', { name: 'Загрузка текста шаблона' })).toBeTruthy();
  });

  it('версии нет (404) — отказ назван словами, а не кодом ответа; редактора нет', () => {
    contentState = { isError: true, error: Object.assign(new Error('Request failed with status code 404'), { response: { status: 404, data: '' } }) };
    renderPanel(true);
    expect(screen.queryByTestId('editor')).toBeNull();
    expect(screen.queryByRole('progressbar')).toBeNull();
    expect(screen.getByText('Этой версии больше нет — её удалили. Выберите другую.')).toBeTruthy();
  });

  it('иной отказ — причина сервера после общего «не удалось»', () => {
    contentState = { isError: true, error: Object.assign(new Error('Request failed'), { response: { status: 500, data: { error: 'База недоступна' } } }) };
    renderPanel(true);
    expect(screen.getByText('Не удалось загрузить текст шаблона: База недоступна')).toBeTruthy();
  });

  it('текст получен — редактор открыт, правок нет', () => {
    renderPanel(true);
    expect(screen.getByTestId('editor')).toBeTruthy();
    expect(screen.queryByText('Есть несохранённые правки')).toBeNull();
  });

  const editor = () => screen.getByTestId('editor') as HTMLTextAreaElement;
  const props = { docType, allDocTypes: [docType], onSaved: vi.fn(), versionPlan: NO_RETIRE };
  const dirtyMark = () => screen.queryByLabelText('есть правки');

  it('после Ctrl+S на месте другая версия из кэша показывает СВОЙ текст (ревью #1973)', async () => {
    contents = { t1: 'A', t2: 'B' };
    const a = template(true);
    const b = { ...template(true), id: 't2', name: 'Другой' };
    const { rerender } = render(<EditorPanel template={a} {...props} />);
    fireEvent.change(editor(), { target: { value: 'A2' } });
    saveContent.mockImplementationOnce(async () => {
      contents = { ...contents, t1: 'A2' };   // ответ записи кладёт текст в кэш
      return { template: { ...a, content: 'A2' }, resetDocuments: 0 };
    });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); });
    rerender(<EditorPanel template={a} {...props} />);
    expect(editor().value).toBe('A2');
    expect(dirtyMark()).toBeNull();

    rerender(<EditorPanel template={b} {...props} />);
    expect(editor().value).toBe('B');
    expect(dirtyMark()).toBeNull();
  });

  it('текст перечитан с сервера, своих правок нет — редактор следует за ним, а не «правки»', () => {
    contents = { t1: 'старый' };
    const { rerender } = render(<EditorPanel template={template(true)} {...props} />);
    contents = { t1: 'новый от соседа' };
    rerender(<EditorPanel template={template(true)} {...props} />);
    expect(editor().value).toBe('новый от соседа');
    expect(dirtyMark()).toBeNull();
  });

  it('текст перечитан, а свои правки есть — правки остаются', () => {
    contents = { t1: 'старый' };
    const { rerender } = render(<EditorPanel template={template(true)} {...props} />);
    fireEvent.change(editor(), { target: { value: 'моя правка' } });
    contents = { t1: 'новый от соседа' };
    rerender(<EditorPanel template={template(true)} {...props} />);
    expect(editor().value).toBe('моя правка');
    expect(dirtyMark()).toBeTruthy();
  });
});
