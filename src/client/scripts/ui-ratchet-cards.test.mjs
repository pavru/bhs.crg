import { describe, expect, it } from 'vitest';
import { countViolations, RULES } from './ui-ratchet-lib.mjs';

// Правило hand-card ([UI-77], sharedui-md3-13 PR 2б). Отдельно от ui-ratchet.test.mjs — тот у порога
// FileSizeRatchet. Счёт — только своего правила.
const cards = (text, file = 'features/x/X.tsx') =>
  countViolations(text, { dir: file.split('/')[0], file })['hand-card'] ?? 0;

describe('храповик интерфейса: hand-card', () => {
  it('карточка: блочный тег с фоном поверхности, рамкой и скруглением на одном элементе', () => {
    expect(cards([
      '<div className="rounded-md border border-outline-variant bg-surface p-4">Шаг 1</div>',
      '<section className={`bg-surface-container-low ${x} border rounded-lg`}>Итоги</section>',
      '<article className="bg-surface-container border-2 rounded-xl">Смета</article>',
      '<p className="bg-surface-container-highest border-b rounded-sm">Сводка</p>',
      '<dl className="bg-surface border rounded-md">Реквизиты</dl>',
    ].join(' '))).toBe(5);
    expect(RULES.find(r => r.id === 'hand-card').instead).toContain('Card');
  });

  it('поле, кнопка, пометка, слой Radix, Card и неполная карточка — не карточки', () => {
    expect(cards([
      '<input className="bg-surface border border-outline rounded-sm h-10" />',
      '<textarea className="bg-surface border rounded-sm" />',
      '<button className="bg-surface-container border rounded-full">Чип</button>',
      '<span className="bg-surface border rounded-full">пометка</span>',
      '<Popover.Content className="bg-surface-container border rounded-sm">слой</Popover.Content>',
      '<Card variant="outlined" title="Шаг 1">тело</Card>',
      '<div className="bg-surface rounded-md">без рамки</div>',
      '<div className="bg-surface border">без скругления</div>',
      '<div className="border rounded-md">без фона</div>',
      '<div className="hover:bg-surface-container border rounded-md">наведение</div>',
      '<div className="bg-surface/80 border rounded-md">подложка</div>',
      '<div className="bg-surface-variant border rounded-md">не контейнер</div>',
      '<div className="bg-surface border-outline rounded-md">цвет рамки без ширины</div>',
      '<div className="bg-surface border-2 border-dashed rounded-md">Перетащите файл</div>',
    ].join(' '))).toBe(0);
  });

  it('смотрит только features/: shared/ui рисует сами поля и слои', () => {
    const card = '<div className="rounded-md border bg-surface">Шаг 1</div>';
    expect(cards(card, 'features/costs/X.tsx')).toBe(1);
    expect(cards(card, 'shared/ui/X.tsx')).toBe(0);
    expect(cards(card, 'shared/filter/X.tsx')).toBe(0);
  });
});
