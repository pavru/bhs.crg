import { describe, expect, it } from 'vitest';
import { countViolations as countAll } from './ui-ratchet-lib.mjs';

// Правило split-button ([UI-83]): своя половина split-кнопки. Отдельно от ui-ratchet.test.mjs — тот у
// порога FileSizeRatchet. Счёт — только этого правила: фикстуры не обязаны быть чистыми для прочих.
const countViolations = text => Object.fromEntries(Object.entries(countAll(text)).filter(([rule]) => rule === 'split-button'));

describe('храповик интерфейса: split-button', () => {
  /** Своя половина split-кнопки ([UI-83]): одностороннее скругление у кнопки, а не у плашки. */
  it('самодельная split-кнопка — кнопка с односторонним скруглением', () => {
    expect(countViolations([
      '<button className="h-8 rounded-l-full bg-primary">Сохранить</button>',
      '<IconButton label="Ещё" className="rounded-l-none bg-primary"><Icon icon={Down} size={18} /></IconButton>',
      '<Button className={`${x} active:rounded-tr-xs`}>Да</Button>',
      '<a href="/x" className="rounded-e-full">Ссылка</a>',
    ].join(' '))).toEqual({ 'split-button': 4 });
  });

  it('кнопка под другим именем импорта и обёртка кнопки — тоже половина', () => {
    expect(countViolations([
      "import { Button as B } from '@/shared/ui/Button';",
      "import { IconButton as Ib } from '../../shared/ui/Button';",
      '<B className="rounded-r-none">Сохранить</B>',
      '<Ib label="Ещё" className="rounded-l-xs" />',
      '<PrimaryActionButton action={a} className="rounded-r-none" />',
    ].join('\n'))).toEqual({ 'split-button': 3 });
  });

  it('чужой компонент с односторонним скруглением — не кнопка', () => {
    expect(countViolations([
      "import { Button as B } from 'some-lib';",
      '<B className="rounded-r-none">Да</B>',
      '<Card className="rounded-l-xs">a</Card>',
    ].join('\n'))).toEqual({});
  });

  it('скругление плашки, панели и целой кнопки — не split', () => {
    expect(countViolations([
      '<div className="rounded-r-md border-l-2">условие</div>',
      '<span className="rounded-l-xs">a</span>',
      '<Button className="rounded-full">Да</Button>',
      '<button className="rounded-lg">Нет</button>',
    ].join(' '))).toEqual({});
  });
});

// Правила sharedui-md3-10 ([UI-68]): размер кнопки не задаётся ни свойством, ни классом, а переменную
// размера кнопки-иконки ставит только клетка таблицы.
const only = (rule, text, opts) => Object.fromEntries(Object.entries(countAll(text, opts)).filter(([r]) => r === rule));
const BUTTON = "import { Button, IconButton } from '@/shared/ui/Button';\n";

describe('храповик интерфейса: button-size', () => {
  it('высота, ширина и размер в className кнопки — нарушение, с вариантом тоже', () => {
    expect(only('button-size', BUTTON + [
      '<IconButton label="Назад" className="min-h-11 min-w-11" icon={A} />',
      '<Button className="w-full">Добавить</Button>',
      '<IconButton label="x" className="max-md:size-8" icon={A} />',
    ].join('\n'))).toEqual({ 'button-size': 3 });
  });

  it('сжатие и обрезка размера не меняют: min-w-0, max-w-*; разметка и чужие компоненты — не кнопки', () => {
    expect(only('button-size', BUTTON + [
      '<Button className="min-w-0 max-w-full">Длинная подпись</Button>',
      '<button className="h-8">своя</button>',
      '<Card className="h-8" />',
    ].join('\n'))).toEqual({});
  });
});

describe('храповик интерфейса: dense-scope и icon-button в shared/', () => {
  it('переменная размера кнопки-иконки — только в местах XS, buttonClasses.ts и Button.tsx', () => {
    const text = 'const c = "[--md-comp-icon-button-size:32px]";';
    expect(only('dense-scope', text, { dir: 'features', file: 'features/costs/X.tsx' })).toEqual({ 'dense-scope': 1 });
    expect(only('dense-scope', text, { dir: 'shared', file: 'shared/ui/dataTable.ts' })).toEqual({});
    expect(only('dense-scope', text, { dir: 'shared', file: 'shared/ui/Button.tsx' })).toEqual({});
  });

  it('класс места ICON_BUTTON_XS — только в названных местах', () => {
    const text = "import { ICON_BUTTON_XS } from '@/shared/ui/buttonClasses';";
    expect(only('dense-scope', text, { dir: 'features', file: 'features/costs/X.tsx' })).toEqual({ 'dense-scope': 1 });
    expect(only('dense-scope', text, { dir: 'shared', file: 'shared/ui/DateInput.tsx' })).toEqual({});
    expect(only('dense-scope', text, { dir: 'shared', file: 'shared/ui/OutlinedField.tsx' })).toEqual({});
    expect(only('dense-scope', text, { dir: 'features', file: 'features/document-sets/editor/SourceBindingTrigger.tsx' })).toEqual({});
  });

  it('своя кнопка-иконка в shared/ui — нарушение; крестик чипа и сам Button.tsx — нет', () => {
    const text = '<button aria-label="Ещё" className="p-1"><Icon icon={MoreVert} size={18} /></button>';
    expect(only('icon-button', text, { dir: 'shared', file: 'shared/ui/RowActionsMenu.tsx' })).toEqual({ 'icon-button': 1 });
    expect(only('icon-button', text, { dir: 'shared', file: 'shared/ui/Chip.tsx' })).toEqual({});
    expect(only('icon-button', text, { dir: 'shared', file: 'shared/ui/Button.tsx' })).toEqual({});
  });
});
