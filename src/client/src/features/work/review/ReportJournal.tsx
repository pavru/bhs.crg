import { useReviewJournal } from '@/shared/api/workReview';
import { Banner } from '@/shared/ui/Banner';
import { JournalFeed } from '@/shared/ui/JournalFeed';
import { apiError } from '@/shared/utils/apiError';

const titleByKind: Record<string, string> = {
  created: 'Отчёт создан', updated: 'Отчёт изменён', deleted: 'Отчёт удалён',
  submitted: 'Отчёт подан', 'auto-submitted': 'Отчёт подан автоматически',
  'field-receipt': 'Получена подача с телефона',
  accepted: 'Отчёт принят', returned: 'Возвращён на правку', 'crew-approved': 'Бригада сверена',
  corrected: 'Отчёт исправлен после закрытия', 'accepted-changed': 'Принятый отчёт изменён',
  'manager-edited': 'Правка строк менеджером', 'same-person': 'Ввёл и принял один человек',
  'notify-none': 'Сотрудника не удалось уведомить',
};

export function ReportJournal({ id }: { id: string }) {
  const journal = useReviewJournal(id);
  return <section className="min-w-0 space-y-3" aria-label="Журнал отчёта">
    <h2 className="text-title-medium">Журнал</h2>
    {journal.isPending && <p role="status" className="text-body-small text-on-surface-variant">Загрузка журнала…</p>}
    {journal.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void journal.refetch() }]}>
      {apiError(journal.error, 'Не удалось загрузить журнал.')}
    </Banner>}
    {journal.data && <JournalFeed entries={journal.data.map(entry => ({
      sequence: entry.seq, title: Object.hasOwn(titleByKind, entry.kind) ? titleByKind[entry.kind] : entry.kind,
      author: entry.author.name, executor: entry.enteredFor ? entry.executor.name : undefined,
      serverTime: entry.serverTime, deviceTime: entry.deviceTime, note: entry.comment,
    }))} />}
  </section>;
}
