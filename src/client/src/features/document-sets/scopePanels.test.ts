// @vitest-environment jsdom
import { describe, it, expect } from 'vitest';
import { constructionPanels } from '@/shared/ui/constructionPanels';
import { panelViews } from './scopePanels';

/**
 * Реестр панелей (`constructionPanels.ts`) описывает панель, а рисует её карта `panelViews` (plan-02).
 * Запись без содержимого открывалась бы пустой карточкой, содержимое без записи — недостижимо.
 */
describe('содержимое панелей карточки стройки', () => {
  it('у каждой панели реестра есть содержимое, и лишнего нет', () => {
    expect(Object.keys(panelViews).sort()).toEqual(constructionPanels.map(p => p.id).sort());
  });
});
