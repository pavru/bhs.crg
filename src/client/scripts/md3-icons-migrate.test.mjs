import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { lucideStub, migrateIcons, toScale } from './md3-icons-migrate.mjs';
import iconMap from './md3-icon-map.json' with { type: 'json' };
import manifest from '../src/shared/ui/icons/manifest.json' with { type: 'json' };

// Кодмод значков ([UI-60], [UI-61], sharedui-md3-03): фикстуры — файлы в src/, которых на диске нет;
// lucide-react — заглушка из таблицы, поэтому тест не зависит от того, стоит ли пакет.
const srcRoot = join(import.meta.dirname, '..', 'src');
const path = file => join(srcRoot, file).replaceAll('\\', '/');

/** Перевод фикстур `{ путь от src/: текст }` → `{ путь: { text, notes, changes } }` и имена вне таблицы. */
function run(fixtures) {
  const texts = new Map(Object.entries(fixtures).map(([f, t]) => [path(f), t]));
  const result = migrateIcons([...texts.keys()], { read: p => texts.get(p.replaceAll('\\', '/')) });
  return {
    unknown: result.unknown,
    files: Object.fromEntries(result.files.map(f => [f.file, f])),
  };
}

describe('значки: таблица lucide → Symbols', () => {
  it('каждый символ таблицы — в манифесте набора, с тем же экспортом и заливкой', () => {
    const entries = Object.values(iconMap.icons).flatMap(e => [e, ...Object.values(e.files ?? {})]).filter(e => e.symbol);
    for (const e of entries) {
      expect(manifest.icons[e.symbol], e.symbol).toBeDefined();
      expect(!!manifest.icons[e.symbol].fill, `${e.symbol}: fill`).toBe(!!e.fill);
    }
  });

  it('шкала: 9…18 → 18, 20 → 20, 22 и 24 → 24, 28…36 → 40, 40 и 48 остаются', () => {
    expect([9, 13, 18, 20, 22, 24, 28, 36, 40, 48].map(toScale)).toEqual([18, 18, 18, 20, 24, 24, 40, 40, 40, 48]);
  });

  it('заглушка lucide-react называет каждое имя таблицы, кроме типа', () => {
    expect(lucideStub()).toContain('export declare const Trash2: LucideIcon;');
    expect(lucideStub()).not.toContain('const LucideIcon');
  });
});

describe('значки: кодмод', () => {
  it('импорт с as, JSX, размер на ступень, strokeWidth долой', () => {
    const { files } = run({ 'features/a.tsx': [
      "import { Trash2 as Bin, X } from 'lucide-react';",
      'export const A = () => <div><Bin size={14} strokeWidth={2.5} className="text-error" /><X size={28} /><X /></div>;',
    ].join('\n') });
    expect(files['features/a.tsx'].text).toBe([
      "import { Icon } from '@/shared/ui/Icon';",
      "import { Close, Delete } from '@/shared/ui/icons';",
      'export const A = () => <div><Icon icon={Delete} size={18} className="text-error" /><Icon icon={Close} size={40} /><Icon icon={Close} /></div>;',
    ].join('\n'));
  });

  it('значение в карте и тип LucideIcon → IconDef; typeof значка — тоже IconDef', () => {
    const { files } = run({ 'shared/ui/nav.ts': [
      "import { Database, Printer, type LucideIcon } from 'lucide-react';",
      'export const items: { icon: LucideIcon }[] = [{ icon: Database }, { icon: Printer }];',
      'export type Def = { icon: typeof Printer };',
    ].join('\n') });
    expect(files['shared/ui/nav.ts'].text).toBe([
      "import { Database, Print, type IconDef } from './icons';",
      'export const items: { icon: IconDef }[] = [{ icon: Database }, { icon: Print }];',
      'export type Def = { icon: IconDef };',
    ].join('\n'));
  });

  it('спиннер в кнопке — CircularProgress без animate-spin; вращение по условию — в отчёт', () => {
    const { files } = run({ 'features/b.tsx': [
      "import { Loader2, RefreshCw } from 'lucide-react';",
      'export const B = ({ busy }: { busy: boolean }) => <button type="button">',
      '  <Loader2 size={14} className="animate-spin motion-reduce:animate-none text-primary" aria-hidden />',
      "  <RefreshCw size={15} className={busy ? 'animate-spin' : ''} />",
      '</button>;',
    ].join('\n') });
    const b = files['features/b.tsx'];
    expect(b.text).toContain('<CircularProgress size={18} className="text-primary" />');
    expect(b.text).toContain("import { CircularProgress } from '@/shared/ui/CircularProgress';");
    expect(b.notes.map(n => `${n.line} ${n.note}`)).toEqual([expect.stringMatching(/^4 вращение значка по условию/)]);
  });

  it('значок в переменной — по типу: проп и <it.icon />; переменная — со строчной, имя Icon — компоненту', () => {
    const { files } = run({ 'shared/ui/Pill.tsx': [
      "import type { LucideIcon } from 'lucide-react';",
      'export function Pill({ icon: Icon, items }: { icon: LucideIcon; items: { icon: LucideIcon }[] }) {',
      '  return <>{items.map(it => <it.icon key="k" size={16} />)}<Icon size={12} /></>;',
      '}',
    ].join('\n') });
    expect(files['shared/ui/Pill.tsx'].text).toBe([
      "import { Icon } from './Icon';",
      "import type { IconDef } from './icons';",
      'export function Pill({ icon, items }: { icon: IconDef; items: { icon: IconDef }[] }) {',
      '  return <>{items.map(it => <Icon icon={it.icon} key="k" size={18} />)}<Icon icon={icon} size={18} /></>;',
      '}',
    ].join('\n'));
  });

  it('имя, занятое в файле, — импорт с псевдонимом', () => {
    const { files } = run({ 'features/c.tsx': [
      "import { Table } from '@/shared/ui/Table';",
      "import { Table2 } from 'lucide-react';",
      'export const C = () => <Table><Table2 size={13} /></Table>;',
    ].join('\n') });
    expect(files['features/c.tsx'].text).toContain("import { Table as TableIcon } from '@/shared/ui/icons';");
    expect(files['features/c.tsx'].text).toContain('<Table><Icon icon={TableIcon} size={18} /></Table>');
  });

  it('размер классом — в size; значок в низком контейнере — в отчёт', () => {
    const { files } = run({ 'features/d.tsx': [
      "import { Check } from 'lucide-react';",
      'export const D = () => <span className="flex h-3"><Check className="w-3 h-3 text-primary" /></span>;',
    ].join('\n') });
    expect(files['features/d.tsx'].text).toContain('<Icon icon={Check} size={18} className="text-primary" />');
    expect(files['features/d.tsx'].notes[0].note).toMatch(/значок 18 px в контейнере h-3 \(12 px\)/);
  });

  it('другой значок по месту — из files таблицы', () => {
    const file = 'features/reconciliations/ObservationReviewDialog.tsx';
    const { files } = run({ [file]: "import { RotateCcw } from 'lucide-react';\nexport const E = () => <RotateCcw size={14} />;" });
    expect(files[file].text).toContain('<Icon icon={Undo} size={18} />');
  });

  it('значок вне таблицы — отказ с перечнем мест', () => {
    const { unknown } = run({ 'features/f.tsx': "import { Pizza } from 'lucide-react';\nexport const F = () => <Pizza />;" });
    expect(unknown).toEqual([{ name: 'Pizza', where: ['features/f.tsx'] }]);
  });

  it('второй прогон — замен 0', () => {
    const once = run({ 'features/g.tsx': "import { Trash2 } from 'lucide-react';\nexport const G = () => <Trash2 size={14} />;" });
    const twice = run({ 'features/g.tsx': once.files['features/g.tsx'].text });
    expect(twice.files).toEqual({});
  });
});
