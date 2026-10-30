import { describe, expect, it } from 'vitest';
import { migrateSizes } from './md3-size-migrate.mjs';

// Проход размера кнопок кодмода md3:migrate (sharedui-md3-10, [UI-68]): почему по дереву и по импорту —
// в md3-size-migrate.mjs.
const BUTTON = "import { Button, IconButton } from '@/shared/ui/Button';\nimport { Icon } from '@/shared/ui/Icon';\n";
const run = body => migrateSizes(BUTTON + body, 'src/x.tsx');
const out = body => run(body).text.slice(BUTTON.length);

describe('md3:migrate: размер кнопок', () => {
  it('снимает size у Button на любой позиции атрибута', () => {
    expect(out('<Button size="sm" variant="text">Да</Button>')).toBe('<Button variant="text">Да</Button>');
    expect(out('<Button variant="text" size="lg">Да</Button>')).toBe('<Button variant="text">Да</Button>');
    expect(out('<Button\n  size="sm"\n  variant="text"\n>Да</Button>')).toBe('<Button\n  variant="text"\n>Да</Button>');
  });

  it('видит атрибут после icon={<Icon … />}: первый > внутри значка его не прячет', () => {
    expect(out('<Button icon={<Icon icon={Add} size={18} />} size="sm">Добавить</Button>'))
      .toBe('<Button icon={<Icon icon={Add} size={18} />}>Добавить</Button>');
  });

  it('снимает size за ModalExit и PrimaryActionButton — по их импорту', () => {
    const text = "import { ModalExit } from '@/shared/ui/Modal';\nimport { PrimaryActionButton } from './PrimaryActionButton';\n"
      + '<ModalExit size="sm">Закрыть</ModalExit>\n<PrimaryActionButton size="sm" action={a} />';
    expect(migrateSizes(text, 'src/y.tsx').text).toContain('<ModalExit>Закрыть</ModalExit>\n<PrimaryActionButton action={a} />');
  });

  it('чужой Button и size у Checkbox не трогает', () => {
    const text = "import { Button } from 'other-lib';\nimport { Checkbox } from '@/shared/ui/Checkbox';\n"
      + '<Button size="sm" /><Checkbox size="sm" />';
    expect(migrateSizes(text, 'src/z.tsx').text).toBe(text);
  });

  it('знак ребёнком кнопки-иконки становится свойством icon', () => {
    expect(out('<IconButton label="Удалить" size="sm" danger onClick={f}>\n  <Icon icon={Delete} size={18} />\n</IconButton>'))
      .toBe('<IconButton label="Удалить" danger onClick={f} icon={Delete} />');
  });

  it('цвет знака — iconClassName; условный знак и ход операции — выражением', () => {
    expect(out('<IconButton label="x"><Icon icon={Warning} size={18} className="text-error" /></IconButton>'))
      .toBe('<IconButton label="x" icon={Warning} iconClassName="text-error" />');
    expect(out('<IconButton label="x">{on ? <Icon icon={A} size={18} /> : <Icon icon={B} size={18} />}</IconButton>'))
      .toBe('<IconButton label="x" icon={on ? A : B} />');
    expect(out('<IconButton label="x">{busy ? <CircularProgress size={18} /> : <Icon icon={Refresh} size={18} />}</IconButton>'))
      .toBe('<IconButton label="x" loading={busy} icon={Refresh} />');
  });

  it('ребёнок другого вида — в отчёт, без замены', () => {
    const r = run('<IconButton label="x"><span>×</span></IconButton>');
    expect(r.text.slice(BUTTON.length)).toBe('<IconButton label="x"><span>×</span></IconButton>');
    expect(r.untouched).toHaveLength(1);
  });

  it('повторный запуск ничего не меняет', () => {
    const once = out('<IconButton label="x" size="sm"><Icon icon={A} size={18} /></IconButton>');
    expect(migrateSizes(BUTTON + once, 'src/x.tsx').replaced).toHaveLength(0);
  });
});
