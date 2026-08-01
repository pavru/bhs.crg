import { useState } from 'react';
import { ImageIcon, Loader2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { apiClient } from '@/shared/api/client';
import { formatBytes } from '@/shared/api/attachments';
import { apiError } from '@/shared/utils/apiError';
import { CollapsibleSection } from './CollapsibleSection';

interface Report {
  objects: number;
  images: number;
  bytes: number;
  failed: number;
  downscaled: number;
  savedBytes: number;
  dryRun: boolean;
}

async function run(dryRun: boolean): Promise<Report> {
  const { data } = await apiClient.post<Report>('/maintenance/images/migrate', null, { params: { dryRun } });
  return data;
}

/**
 * Обслуживание картинок (issues #522, #523) — единственное место, где мы СПРАШИВАЕМ.
 *
 * Уменьшение при загрузке действует только на новые картинки, поэтому уже лежащие остаются как есть:
 * без этого шага план обслуживал бы будущее, а измеренная боль сохранялась. Но переделка чужих
 * печатей — не то, что делают молча: по-человечески это звучит как «мою печать переделают без меня?».
 *
 * Поэтому: разово, у администратора, одним экраном, с числами, посчитанными ЧЕСТНО (сухой прогон
 * действительно скачивает и уменьшает, просто ничего не сохраняет), и с подтверждением. Не у каждого
 * владельца записи и не по одной. Оригиналы при этом сохраняются — переделка обратима.
 */
export function ImageMaintenanceSection() {
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

  async function apply() {
    setConfirmOpen(false);
    setBusy(true); setError('');
    try {
      const result = await run(false);
      setDone(result);
      setReport(await run(true));   // пересчёт: показываем, что осталось
    } catch (e) {
      setError(apiError(e, 'Не удалось выполнить'));
    } finally { setBusy(false); }
  }

  const nothingToDo = report !== null && report.images === 0 && report.downscaled === 0;

  return (
    <CollapsibleSection title="Обслуживание изображений" storageKey="image-maintenance" defaultOpen={false}>
      <div className="space-y-3">
        <p className="text-xs text-fg3">
          Картинки, загруженные раньше, лежат внутри записей и попадают в каждую резервную копию.
          Их можно перенести в хранилище файлов и уменьшить — оригиналы при этом сохраняются.
        </p>

        <div className="flex items-center gap-2">
          <Button variant="outlined" size="sm" onClick={() => void check()} loading={busy}
            icon={<ImageIcon size={14} />}>
            Посчитать
          </Button>
          {report && !nothingToDo && (
            <Button variant="filled" size="sm" onClick={() => setConfirmOpen(true)} disabled={busy}>
              Выполнить
            </Button>
          )}
          {busy && <Loader2 size={14} className="animate-spin text-brand" />}
        </div>

        {error && <p className="text-xs text-danger">{error}</p>}

        {done && (
          <p className="text-xs text-success">
            Готово: перенесено {done.images}, уменьшено {done.downscaled}
            {done.savedBytes > 0 && `, освобождено ${formatBytes(done.savedBytes)}`}
            {done.failed > 0 && `, не удалось ${done.failed}`}
          </p>
        )}

        {report && (
          nothingToDo
            ? <p className="text-xs text-fg3">Обслуживать нечего — все изображения уже в хранилище и укладываются в размер.</p>
            : (
              <ul className="text-xs text-fg2 space-y-1">
                {report.images > 0 && (
                  <li>
                    Перенести из записей: <b>{report.images}</b> изображений
                    в <b>{report.objects}</b> записях — освободится {formatBytes(report.bytes)}
                  </li>
                )}
                {report.downscaled > 0 && (
                  <li>Уменьшить: <b>{report.downscaled}</b> изображений — освободится {formatBytes(report.savedBytes)}</li>
                )}
                {report.failed > 0 && <li className="text-warning">Не поддаются обработке: {report.failed}</li>}
              </ul>
            )
        )}
      </div>

      <ConfirmDialog
        open={confirmOpen} onOpenChange={setConfirmOpen}
        title="Обслужить изображения?"
        description={
          report
            ? `Будет перенесено ${report.images} и уменьшено ${report.downscaled} изображений. `
              + 'Оригиналы сохраняются в хранилище, вид документов не меняется.'
            : ''
        }
        confirmLabel="Выполнить"
        onConfirm={() => apply()}
      />
    </CollapsibleSection>
  );
}
