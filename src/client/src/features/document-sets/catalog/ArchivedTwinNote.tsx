import { Archive, Unarchive } from '@/shared/ui/icons';
import { useSetCommonDataArchive, type ArchivedTwin } from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import { Banner } from '@/shared/ui/Banner';
import { useSnackbar } from '@/shared/ui/Snackbar';

/**
 * «Такая запись есть в архиве» в форме новой записи (issue #1185). Сервер отказал в создании: ключ
 * идентичности совпал с архивной записью, а действующей с таким ключом нет. Сообщение — banner вверху
 * формы (Р1, sharedui-md3-17), а не диалог: человек видит и то, что набрал, и то, с чем это совпало; и не
 * строка у подвала — после попытки сохранить её было не видно над длинной формой.
 *
 * <p>Два выхода, и оба названы: вернуть ту запись (тогда новая не нужна, форма закрывается) или
 * создать новую осознанно. Без права вести общие данные первого выхода нет, и текст говорит, к кому
 * идти.</p>
 */
export function ArchivedTwinNote({ twin, busy, onReturned, onCreateAnyway }: {
  twin: ArchivedTwin;
  busy: boolean;
  /** Запись возвращена из архива: создавать вторую незачем. */
  onReturned: () => void;
  onCreateAnyway: () => void;
}) {
  const unarchive = useSetCommonDataArchive();
  const canReturn = useCan().permission('core.catalog.edit');
  const toast = useSnackbar();

  async function giveBack() {
    try {
      await unarchive.mutateAsync({ id: twin.archivedId, archived: false });
    } catch (e) {
      toast.apiError(e, 'Запись не возвращена из архива');
      return;
    }
    // Про «снова в списке» не говорим: двойник мог лежать на другом уровне, чем открытый список.
    toast.success(`Запись «${twin.archivedName}» возвращена из архива. Новая не создавалась.`);
    onReturned();
  }

  const pending = busy || unarchive.isPending;
  // «Создать новую» во время возврата не жмётся: Banner блокирует только ждущее действие, поэтому оно
  // скрыто, пока идёт любое из двух.
  const actions = [
    ...(canReturn ? [{ label: 'Вернуть из архива', icon: Unarchive, loading: unarchive.isPending, onClick: () => void giveBack() }] : []),
    ...(pending ? [] : [{ label: 'Создать новую', onClick: onCreateAnyway }]),
  ];
  return (
    <Banner tone="info" icon={Archive} urgent actions={actions}>
      Такая запись есть в архиве: «{twin.archivedName}». Совпали поля идентичности. Вернуть ту запись — и ссылки
      на неё останутся целы, а в справочнике не появится вторая такая же.
      {!canReturn && ' Вернуть из архива может тот, кто ведёт общие данные.'}
    </Banner>
  );
}
