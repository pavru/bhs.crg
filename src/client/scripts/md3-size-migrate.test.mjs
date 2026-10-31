import { describe, expect, it } from 'vitest';
import { migrateSizes } from './md3-size-migrate.mjs';

// Проход размера кнопок кодмода md3:migrate (sharedui-md3-10, [UI-68]): почему по дереву и по импорту —
// в md3-size-migrate.mjs.
const BUTTON = "import { Button, IconButton } from '@/shared/ui/Button';\nimport { Icon } from '@/shared/ui/Icon';\n";
const run = body => migrateSizes(BUTTON + body, 'src/x.tsx');
// Тело без строк импорта: импорт `Icon` проход снимает, когда знак ушёл в `icon`.
const out = body => run(body).text.replace(/^import .*\n/gm, '');

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
    // Хвост пути «/Button» у стороннего пакета — тоже чужой; относительный путь к shared/ui — свой.
    const lib = "import { Button } from '@mui/material/Button';\n<Button size=\"sm\" />";
    expect(migrateSizes(lib, 'src/z.tsx').text).toBe(lib);
    const rel = "import { Button } from '../../shared/ui/Button';\n<Button size=\"sm\" />";
    expect(migrateSizes(rel, 'src/z.tsx').text).toBe("import { Button } from '../../shared/ui/Button';\n<Button />");
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
    expect(out('<IconButton label="x" disabled={busy}>{busy ? <CircularProgress size={18} /> : <Icon icon={Refresh} size={18} />}</IconButton>'))
      .toBe('<IconButton label="x" disabled={busy} loading={busy} icon={Refresh} />');
    expect(out('<IconButton label="x" disabled={!ok || (busy)}>{busy ? <CircularProgress /> : <Icon icon={Refresh} />}</IconButton>'))
      .toBe('<IconButton label="x" disabled={!ok || (busy)} loading={busy} icon={Refresh} />');
  });

  it('индикатор хода — в отчёт, если disabled кнопки на время хода её не выключает: loading выключил бы', () => {
    for (const attrs of ['', ' disabled={!ok}', ' disabled={busy && ok}', ' disabled={!busy}']) {
      const body = `<IconButton label="x"${attrs}>{busy ? <CircularProgress /> : <Icon icon={Refresh} />}</IconButton>`;
      const r = run(body);
      expect(r.text.replace(/^import .*\n/gm, '')).toBe(body);
      expect(r.untouched).toHaveLength(1);
    }
  });

  it('ребёнок другого вида — в отчёт, без замены', () => {
    const r = run('<IconButton label="x"><span>×</span></IconButton>');
    expect(r.text.replace(/^import .*\n/gm, '')).toBe('<IconButton label="x"><span>×</span></IconButton>');
    expect(r.untouched).toHaveLength(1);
  });

  it('импорт Icon без употребления снимается, с употреблением — остаётся', () => {
    const gone = migrateSizes(BUTTON + '<IconButton label="x"><Icon icon={A} size={18} /></IconButton>', 'src/x.tsx').text;
    expect(gone).not.toContain("import { Icon }");
    const kept = migrateSizes(BUTTON + '<IconButton label="x"><Icon icon={A} size={18} /></IconButton><Icon icon={B} />', 'src/x.tsx').text;
    expect(kept).toContain("import { Icon }");
    const shared = migrateSizes("import { Icon, Spinner } from './Icon';\nimport { IconButton } from './Button';\n"
      + '<IconButton label="x"><Icon icon={A} size={18} /></IconButton>', 'src/x.tsx').text;
    expect(shared).toContain("import { Spinner } from './Icon';");
  });

  it('слово Icon в комментарии или строке — не употребление: импорт снимается', () => {
    const text = migrateSizes(BUTTON + '// Icon больше не нужен\nconst s = "Icon";\n<IconButton label="x"><Icon icon={A} size={18} /></IconButton>', 'src/x.tsx').text;
    expect(text).not.toContain('import { Icon }');
  });

  it('повторный запуск ничего не меняет', () => {
    const once = out('<IconButton label="x" size="sm"><Icon icon={A} size={18} /></IconButton>');
    expect(migrateSizes(BUTTON + once, 'src/x.tsx').replaced).toHaveLength(0);
  });
});
