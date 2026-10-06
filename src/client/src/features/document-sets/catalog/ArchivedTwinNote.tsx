import { Archive, ArchiveRestore, Link2 } from 'lucide-react';
import { useCommonDataEntry, useSetCommonDataArchive, type ArchivedTwin } from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import { Button } from '@/shared/ui/Button';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { useToast } from '@/shared/ui/Toast';

/** Показ резолвнутой $ref-ссылки в связанном поле (issue #99): резолвит запись каталога по id → имя. */
export function BoundRefValue({ entryId }: { entryId: string }) {
  const { data: entry } = useCommonDataEntry(entryId);
  return (
    <span className="inline-flex items-center gap-1 text-brand">
      <Link2 size={12} className="shrink-0" />
      {entry ? entry.displayName : <span className="text-fg4">запись каталога…</span>}
      {/* Запись по идентификатору несёт признак сама (issue #1185): связка цела, пометка — о выборе. */}
      {entry?.archived && <ArchivedMark />}
    </span>
  );
}

/**
 * «Такая запись есть в архиве» в форме новой записи (issue #1185). Сервер отказал в создании: ключ
 * идентичности совпал с архивной записью, а действующей с таким ключом нет. Строка стоит В ФОРМЕ, а
 * не диалогом: человек видит и то, что набрал, и то, с чем это совпало.
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
  const toast = useToast();

  async function giveBack() {
    try {
      await unarchive.mutateAsync({ id: twin.archivedId, archived: false });
    } catch (e) {
      toast.apiError(e, 'Запись не возвращена из архива');
      return;
    }
    toast.success(`Запись «${twin.archivedName}» возвращена из архива — она снова в списке. Новая не создавалась.`);
    onReturned();
  }

  const pending = busy || unarchive.isPending;
  return (
    <div className="rounded-lg border border-stroke bg-base px-3 py-2 space-y-2">
      <p className="text-sm text-fg1 flex items-center gap-1.5">
        <Archive size={14} className="text-fg3 shrink-0" />
        Такая запись есть в архиве: «{twin.archivedName}».
      </p>
      <p className="text-xs text-fg3">
        Совпали поля идентичности. Вернуть ту запись — и ссылки на неё останутся целы, а в справочнике
        не появится вторая такая же.
        {!canReturn && ' Вернуть из архива может тот, кто ведёт общие данные.'}
      </p>
      <div className="flex gap-2">
        {canReturn && (
          <Button type="button" variant="tonal" size="sm" icon={<ArchiveRestore size={13} />}
            loading={unarchive.isPending} disabled={pending} onClick={() => void giveBack()}>
            Вернуть из архива
          </Button>
        )}
        <Button type="button" variant="text" size="sm" disabled={pending} onClick={onCreateAnyway}>
          Создать новую
        </Button>
      </div>
    </div>
  );
}
