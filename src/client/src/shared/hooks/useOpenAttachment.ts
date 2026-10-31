import { useCallback } from 'react';
import { openAttachmentInNewTab } from '@/shared/api/attachments';
import { useSnackbar } from '@/shared/ui/Snackbar';

/**
 * Открыть вложение во вкладке так, чтобы отказ увидели на месте ([UI-27], issue #1494): у вкладки свой
 * текст отказа загрузки, а вкладку, которую браузер не дал открыть (`TabBlocked`), назвать, кроме
 * места, некому — без этого кнопка молча ничего не делала бы. Промис не отклоняется: ждать его можно
 * ради «открывается…» на кнопке.
 */
export function useOpenAttachment(): (blobPath: string) => Promise<void> {
  const toast = useSnackbar();
  return useCallback(
    (blobPath: string) => openAttachmentInNewTab(blobPath).catch((e: unknown) => toast.apiError(e, 'Не удалось открыть файл')),
    [toast],
  );
}
