import { describe, expect, it } from 'vitest';
import { countViolations, RULES } from './ui-ratchet-lib.mjs';

// Правило notice-plaque ([UI-73]) и подсказка badge ([UI-74]), sharedui-md3-12 PR 2. Отдельно от
// ui-ratchet.test.mjs — тот у порога FileSizeRatchet. Счёт — только своего правила.
const plaques = text => countViolations(text, { dir: 'features', file: 'features/x/X.tsx' })['notice-plaque'] ?? 0;

describe('храповик интерфейса: notice-plaque', () => {
  it('плашка: div, p или section с заливкой контейнера роли, рамкой и скруглением', () => {
    expect(plaques([
      '<div className="bg-warning-container border rounded-md px-3 py-2">Счёт изменили</div>',
      '<p className={`rounded-sm border border-error ${x} bg-error-container`}>Не сохранилось</p>',
      '<section className="bg-primary-container border-b rounded-lg">Идёт исправление</section>',
      '<div className="bg-success-container border-x-2 border-success rounded-md">Готово</div>',
    ].join(' '))).toBe(4);
    expect(RULES.find(r => r.id === 'notice-plaque').instead).toContain('Banner');
  });

  it('пометка-пилюля, подложка с прозрачностью, пунктир, наведение и Banner — не плашки', () => {
    expect(plaques([
      '<span className="bg-error-container border rounded-full text-label-small">ошибка</span>',
      '<div className="bg-error-container border rounded-sm text-label-small px-1">ошибка</div>',
      '<div className="bg-primary-container/40 border rounded-md">выбранная строка</div>',
      '<div className="bg-primary-container border-2 border-dashed rounded-md">Перетащите файл</div>',
      '<div className="hover:bg-warning-container border rounded-md">строка</div>',
      '<div className="bg-warning-container rounded-md">без рамки</div>',
      '<div className="bg-warning-container border-warning rounded-md">цвет рамки без ширины</div>',
      '<div className="bg-warning-container border-0 rounded-md">рамка снята</div>',
      '<Banner tone="warning" actions={[]}>Счёт изменили</Banner>',
    ].join(' '))).toBe(0);
  });

  it('смотрит весь src/, и shared/ui тоже', () => {
    const plaque = '<div className="bg-warning-container border rounded-md">Не перечитался</div>';
    expect(countViolations(plaque, { dir: 'shared', file: 'shared/ui/X.tsx' })['notice-plaque']).toBe(1);
  });
});

describe('храповик интерфейса: подсказка badge', () => {
  it('выход — StatusText и CountText ([UI-74]): StatusBadge больше нет', () => {
    const { instead } = RULES.find(r => r.id === 'badge');
    expect(instead).toContain('StatusText');
    expect(instead).toContain('CountText');
    expect(instead).not.toContain('StatusBadge');
  });
});
