// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { openAttachmentInNewTab } from '@/shared/api/attachments';
import { TabBlocked } from '@/shared/ui/viewer/openInNewTab';
import { useOpenAttachment } from './useOpenAttachment';

/** Отказ открытия вложения видно на месте ([UI-27], issue #1494): иначе кнопка молча ничего не делает. */

const apiError = vi.fn();
vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => ({ apiError }) }));
vi.mock('@/shared/api/attachments', () => ({ openAttachmentInNewTab: vi.fn() }));

afterEach(() => { vi.clearAllMocks(); });

describe('useOpenAttachment', () => {
  it('вкладку не дали открыть — место говорит, что разрешить, и промис не отклоняется', async () => {
    vi.mocked(openAttachmentInNewTab).mockRejectedValue(new TabBlocked());
    const { result } = renderHook(() => useOpenAttachment());
    await expect(result.current('scans/a.pdf')).resolves.toBeUndefined();
    expect(apiError).toHaveBeenCalledWith(expect.any(TabBlocked), 'Не удалось открыть файл');
    expect(apiError.mock.calls[0][0].message).toMatch(/всплывающие окна/);
  });

  it('загрузка не удалась — тоже тост, а не тишина', async () => {
    vi.mocked(openAttachmentInNewTab).mockRejectedValue(new Error('Файл не найден'));
    const { result } = renderHook(() => useOpenAttachment());
    await result.current('scans/gone.pdf');
    expect(apiError).toHaveBeenCalledTimes(1);
  });

  it('открылось — тоста нет', async () => {
    vi.mocked(openAttachmentInNewTab).mockResolvedValue();
    const { result } = renderHook(() => useOpenAttachment());
    await result.current('scans/a.pdf');
    expect(apiError).not.toHaveBeenCalled();
  });
});
