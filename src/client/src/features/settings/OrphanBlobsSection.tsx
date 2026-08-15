import { useState } from 'react';
import { Trash2, Loader2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { apiClient } from '@/shared/api/client';
import { apiError } from '@/shared/utils/apiError';
import { formatBytes } from '@/shared/api/attachments';
import { CollapsibleSection } from './CollapsibleSection';

interface Report {
  registered: number;
  referenced: number;
  orphans: number;
  /** Без ссылок, но моложе порога — их сборщик не трогает: файл может прямо сейчас прикрепляться. */
  tooYoung: number;
  /** Сколько кандидатов берёт один прогон: остальные подберёт следующий. */
  batch: number;
  bytes: number;
  /** Из партии уже нет в хранилище — уйдёт только запись реестра. */
  missing: number;
  sample: string[];
  deleted: number;
  failed: number;
  remaining: number;
  /** Хранилище не ответило — числа недостоверны, уборка не делалась. */
  storageUnreachable: boolean;
  minAgeHours: number;
  dryRun: boolean;
}

async function run(dryRun: boolean): Promise<Report> {
  const { data } = await apiClient.post<Report>('/maintenance/orphan-blobs/cleanup', null, { params: { dryRun } });
  return data;
}

/**
 * Уборка объектов хранилища, на которые больше никто не ссылается (issue #741).
 *
 * Удаление документа или комплекта убирало записи из базы, но файлы в хранилище оставляло — притом
 * что диалог удаления обещает «и их сгенерированные PDF». Чинить это в каждой точке удаления
 * значило бы закрыть один выход из многих, поэтому здесь сборщик: что бы и как бы ни удалили,
 * объект без ссылок будет найден.
 */
export function OrphanBlobsSection() {
  const [report, setReport] = useState<Report | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [done, setDone] = useState<Report | null>(null);

  async function check() {
    setBusy(true); setError(''); setDone(null);
    try { setReport(await run(true)); }
    catch (e) { setError(apiError(e, 'Не удалось посчитать')); }
    finally { setBusy(false); }
  }

  /**
   * Ошибку НЕ глотаем: диалог подтверждения — единый канал для отказов (см. ConfirmDialog), и при
   * reject он остаётся открытым с причиной.
   *
   * Автоматического пересчёта здесь нет намеренно. У соседней уборки объектов он стоит и стоит
   * дёшево — там один запрос к базе. Здесь пересчёт это ПОЛНЫЙ скан всех JSONB-колонок плюс запрос
   * размера на каждого оставшегося кандидата, то есть удвоение самой дорогой части операции сразу
   * после необратимого действия. Ответ на удаление и так говорит, сколько удалено и сколько
   * осталось; кому нужны свежие числа — «Посчитать» рядом.
   */
  async function apply() {
    setBusy(true); setError('');
    try {
      const result = await run(false);
      setDone(result);
      setReport(result);
    } finally { setBusy(false); }
  }

  // Считаем по «осталось»: после уборки orphans хранит то, что было НАЙДЕНО, а не то, что ещё есть.
  const nothingToDo = report !== null && report.remaining === 0;
  const blocked = report?.storageUnreachable === true;

  return (
    <CollapsibleSection title="Осиротевшие файлы хранилища" storageKey="orphan-blobs" defaultOpen={false}>
      <div className="space-y-3">
        <p className="text-xs text-fg3">
          Сгенерированные PDF, сканы и вложения, на которые больше не ссылается ни одна запись:
          удаление документа или комплекта убирает записи из базы, но файлы остаются занимать место.
          Считаются только объекты, созданные приложением.
        </p>

        <div className="flex items-center gap-2">
          <Button variant="outlined" size="sm" onClick={() => void check()} loading={busy}
            icon={<Trash2 size={14} />}>
            Посчитать
          </Button>
          {report && !nothingToDo && !blocked && (
            <Button variant="filled" size="sm" danger onClick={() => setConfirmOpen(true)} disabled={busy}>
              Удалить
            </Button>
          )}
          {busy && <Loader2 size={14} className="animate-spin text-brand" />}
        </div>

        {error && <p className="text-xs text-danger">{error}</p>}

        {done && (
          <p className="text-xs text-success">
            Готово: удалено файлов {done.deleted}, освобождено {formatBytes(done.bytes)}.
          </p>
        )}

        {blocked && (
          <p className="text-xs text-danger">
            Хранилище не отвечает — ничего не удалено. Числа ниже недостоверны: «нет размера» и «нет
            связи» с этой стороны выглядят одинаково, а снять записи о живых файлах значило бы
            сделать их недоступными навсегда. Повторите, когда хранилище ответит.
          </p>
        )}

        {report && (
          <ul className="text-xs text-fg2 space-y-1">
            <li>Всего числится за приложением: {report.registered}, из них используются: {report.referenced}</li>
            {nothingToDo && <li className="text-fg3">Осиротевших файлов нет.</li>}
            {!nothingToDo && (done
              ? <li>Осталось кандидатов: <b>{report.remaining}</b> — нажмите «Удалить» ещё раз</li>
              : <li>Будет удалено: <b>{report.batch}</b>, освободится {formatBytes(report.bytes)}</li>)}
            {report.failed > 0 && (
              <li className="text-warning">
                Не удалось удалить: {report.failed} — подберёт следующий прогон, подробности в журнале.
              </li>
            )}
            {!done && report.remaining > report.batch && (
              <li className="text-fg3">
                Всего кандидатов: {report.remaining} — за один прогон берётся не больше {report.batch},
                чтобы уборка не переросла таймаут прокси. Прогонов понадобится несколько.
              </li>
            )}
            {report.missing > 0 && (
              <li className="text-fg3">
                Из них {report.missing} в хранилище уже нет — уйдёт только запись о них.
              </li>
            )}
            {report.tooYoung > 0 && (
              <li className="text-fg3">
                Пропущено как слишком свежие: {report.tooYoung} — моложе {report.minAgeHours} ч.
                Файл попадает в хранилище раньше, чем ссылка на него сохраняется в документ, и
                только что загруженное вложение от сироты неотличимо.
              </li>
            )}
            {!done && report.sample.length > 0 && (
              <li className="text-fg3">
                Например: {report.sample.join(', ')}
                {report.batch > report.sample.length && ' …'}
              </li>
            )}
          </ul>
        )}
      </div>

      <ConfirmDialog
        open={confirmOpen} onOpenChange={setConfirmOpen}
        title="Удалить осиротевшие файлы?"
        description={
          report
            ? `Будет удалено файлов: ${report.batch}, освободится ${formatBytes(report.bytes)}.`
              + ' Файлы удаляются из хранилища окончательно — восстановить их можно будет только из'
              + ' резервной копии, если они в неё попадали.'
            : ''
        }
        requireCheckbox="Понимаю, что файлы будут удалены безвозвратно"
        confirmLabel="Удалить"
        onConfirm={() => apply()}
      />
    </CollapsibleSection>
  );
}
