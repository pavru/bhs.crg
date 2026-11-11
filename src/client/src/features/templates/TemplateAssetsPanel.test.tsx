// @vitest-environment jsdom
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { TemplateAssetDto } from '@/shared/api/templateAssets';

const assets: TemplateAssetDto[] = [
  { id: '1', scope: 'System', scopeId: null, kind: 'Image', name: 'Печать (скан)', fileName: 'p.png',
    mimeType: 'image/png', fontFamilyName: null, createdAt: '', updatedAt: '' },
  { id: '2', scope: 'System', scopeId: null, kind: 'Image', name: 'схема|1', fileName: 's.png',
    mimeType: 'image/png', fontFamilyName: null, createdAt: '', updatedAt: '' },
];
const mutation = { mutateAsync: vi.fn(), isPending: false };

vi.mock('@/shared/api/templateAssets', () => ({
  useListTemplateAssets: () => ({ data: assets }),
  useUploadTemplateAsset: () => mutation,
  useReplaceTemplateAsset: () => mutation,
  useDeleteTemplateAsset: () => mutation,
}));

const { TemplateAssetsPanel } = await import('./TemplateAssetsPanel');

// Ассет с именем вне правила — только из прежних данных или копии (change core-template-asset-names, D4):
// панель называет его и не даёт ссылки, которую генерация отклонит.
describe('TemplateAssetsPanel', () => {
  it('помечает картинку с негодным именем и не даёт на неё ссылки', () => {
    render(<TemplateAssetsPanel scope="System" scopeId={null} title="Ассеты" />);
    fireEvent.click(screen.getByText(/Ассеты/));

    expect(screen.getByText('/assets/Печать (скан).png')).toBeTruthy();
    expect(screen.getByText(/недопустимое имя, генерация откажет/)).toBeTruthy();
    expect(screen.queryByText('/assets/схема|1.png')).toBeNull();
  });
});
