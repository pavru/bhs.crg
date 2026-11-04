import { ContentCut } from '@/shared/ui/icons';
import { useRecutGrouping } from '@/shared/api/datasets';
import { Banner } from '@/shared/ui/Banner';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { ruCount } from '@/shared/utils/pluralize';

/**
 * Документы набора без файла — и выход (issue #1534).
 *
 * <p>Части PDF-набора пропадают из хранилища без правки набора: восстановление из копии, снятой до
 * #1399, или копия источника, не обновлённая при прежней перерезке. Копия и восстановление называют
 * выход — это действие; прежде они посылали «сохранить разбиение», а «Сохранить» неактивна, пока
 * разбиение не правили.</p>
 *
 * <p>Полоса стоит только тогда, когда документов без файла не ноль, — то есть ровно там, где действие
 * что-то вернёт. Файл набора при этом на месте: без него редактор не открылся бы вовсе (листы
 * рисуются из файла). Пока разбиение правят, кнопки нет: сохранение вырежет документы и так, а
 * перерезка по прежнему разбиению рядом с несохранённой правкой читалась бы как её сохранение.</p>
 */
export function MissingPartsNotice({ fileId, missing, dirty }: {
  fileId: string;
  /** Сколько документов без файла — `missingParts` из чтения страниц. */
  missing: number;
  dirty: boolean;
}) {
  const recut = useRecutGrouping(fileId);
  const toast = useSnackbar();
  if (missing <= 0) return null;

  return (
    <Banner tone="warning" urgent data-testid="missing-parts" className="mb-3"
      actions={dirty ? [] : [{
        label: 'Вырезать документы заново', icon: ContentCut, loading: recut.isPending,
        onClick: () => recut.mutate(undefined, {
          onSuccess: r => r.missingParts > 0
            ? toast.error(`Вырезаны не все: без файла осталось ${ruCount(r.missingParts, 'документ', 'документа', 'документов')}.`)
            : toast.success('Документы вырезаны заново.'),
          onError: e => toast.apiError(e, 'Не удалось вырезать документы'),
        }),
      }]}>
      {ruCount(missing, 'документ', 'документа', 'документов')} без файла в хранилище: генерация выпустит
      их без вложений.{' '}
      {dirty
        ? 'Сохранение разбиения вырежет их заново.'
        : 'Вырезать их можно заново из файла набора — по текущему разбиению, оно не изменится.'}
    </Banner>
  );
}

/** Отказ сохранения разбиения — строкой под шапкой. */
export function GroupingSaveError({ error }: { error: unknown }) {
  return (
    <p className="text-body-medium text-error mb-3">
      {(error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось сохранить'}
    </p>
  );
}
