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
