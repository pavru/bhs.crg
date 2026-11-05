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

  it('область по умолчанию: shared/ui не смотрит, прочий shared — смотрит', () => {
    const row = "<button className={`state-layer ${a ? 'bg-secondary-container' : ''}`}>x</button>";
    expect(rows(row, 'shared/ui/ListItem.tsx')).toBe(0);
    expect(rows(row, 'shared/hooks/X.tsx')).toBe(1);
  });
});
