import { describe, expect, it } from 'vitest';
import { countViolations, RULES } from './ui-ratchet-lib.mjs';

// Правило list-row ([UI-84], sharedui-md3-18). Отдельно от ui-ratchet.test.mjs — тот у порога FileSizeRatchet.
// Счёт — только своего правила.
const rows = (text, file = 'features/x/X.tsx') =>
  countViolations(text, { dir: file.split('/')[0], file })['list-row'] ?? 0;

describe('храповик интерфейса: list-row', () => {
  it('строка руками: слой состояния и заливка выбранного в ветке условия', () => {
    expect(rows([
      // typeRow типов документов — тернарный оператор в шаблонной строке
      "<button className={`state-layer w-full flex items-center px-3 h-11 rounded-full ${active ? 'bg-primary-container text-on-primary-container' : 'text-on-surface-variant'}`}>Тип</button>",
      // строка счёта — заливка наведения и && вместо тернарного
      "<div className={`flex hover:bg-surface-container ${open && 'bg-secondary-container'}`}>Счёт</div>",
      // div дерева шаблонов
      "<div className={`state-layer flex items-center group/ver ${sel ? 'bg-primary-container' : ''}`}>v3</div>",
      // выбор вариантом aria-current — условие уже в классе
      '<a className="state-layer flex aria-current:bg-secondary-container">Роль</a>',
      '<li className="state-layer data-[selected=true]:bg-secondary-container">Профиль</li>',
      // ?? — тоже условие
      "<div className={`state-layer ${selectedCls ?? 'bg-secondary-container'}`}>Набор</div>",
    ].join(' '))).toBe(6);
    expect(RULES.find(r => r.id === 'list-row').instead).toContain('ListItem');
  });

  it('общие компоненты, открытое меню, наведение без условия, пометка и заливка без слоя — не строки', () => {
    expect(rows([
      '<ListItem headline="Тип" selected onClick={f} />',
      '<NavItem icon={Description} label="Документы" active onClick={f} />',
      // открытое меню — не выбор
      '<button className="state-layer data-[state=open]:bg-secondary-container">⋮</button>',
      // BaseInstanceChip: подложка наведения с прозрачностью без условия
      "<button className={`state-layer hover:bg-primary-container/70 ${x ? 'text-primary' : ''}`}>чип</button>",
      // заливка без условия
      '<div className="state-layer bg-secondary-container">всегда</div>',
      // пометка — span, а не строка
      "<span className={`state-layer ${on ? 'bg-primary-container' : ''}`}>пометка</span>",
      // условная заливка без слоя состояния — плашка, а не строка
      "<div className={`p-2 ${on ? 'bg-secondary-container' : ''}`}>плашка</div>",
      // слой строки общего компонента
      "<div className={`state-layer-row ${sel ? 'bg-secondary-container' : ''}`}>ListItem изнутри</div>",
    ].join(' '))).toBe(0);
  });

  it('вариант выбора руками (sharedui-md3-21): role="option" атрибутом и литералом, один счёт на элемент', () => {
    // PickerOption: и role, и заливка выбранного в const той же функции — один счёт
    expect(rows(`function PickerOption({ active, best }) {
      const tone = best ? 'bg-primary-container' : active ? 'bg-surface-container' : 'state-layer';
      return <div role="option" aria-selected={active} className={\`px-2 \${tone}\`}>x</div>;
    }`)).toBe(1);
    expect(rows('<div role="option" className="px-3 py-1.5">Стройка</div>')).toBe(1);
    expect(rows("<li role={grouped ? 'option' : undefined}>x</li>")).toBe(1);
    expect(RULES.find(r => r.id === 'list-row').instead).toContain('ListOption');
  });

  it('заливка в const той же функции — считается и без role', () => {
    expect(rows(`function Row({ active }) {
      const tone = active ? 'bg-secondary-container' : 'state-layer';
      return <button className={\`w-full \${tone}\`}>x</button>;
    }`)).toBe(1);
    // const из другой функции — названный пропуск
    expect(rows(`const tone = a ? 'bg-secondary-container' : 'state-layer';
      function Row() { return <button className={\`w-full \${tone}\`}>x</button>; }`)).toBe(0);
  });

  it('вариант из shared/ui и role другого значения — не считаются', () => {
    expect(rows([
      '<ListOption id="o1" headline="Кабель" active onChoose={f} />',
      '<div role="listbox">список</div>',
      '<div role="optionGroup">x</div>',
    ].join(' '))).toBe(0);
  });

  it('область по умолчанию: shared/ui не смотрит, прочий shared — смотрит', () => {
    const row = "<button className={`state-layer ${a ? 'bg-secondary-container' : ''}`}>x</button>";
    expect(rows(row, 'shared/ui/ListItem.tsx')).toBe(0);
    expect(rows(row, 'shared/hooks/X.tsx')).toBe(1);
  });
});
