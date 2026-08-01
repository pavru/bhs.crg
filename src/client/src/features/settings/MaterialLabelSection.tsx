import { useState } from 'react';
import { Tags, Loader2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { apiClient } from '@/shared/api/client';
import { apiError } from '@/shared/utils/apiError';
import { CollapsibleSection } from './CollapsibleSection';

interface Report {
  linksWithoutLabel: number;
  named: number;
  notFound: number;
  documentsScanned: number;
  documentsFailed: number;
  dryRun: boolean;
}

async function run(dryRun: boolean): Promise<Report> {
  const { data } = await apiClient.post<Report>('/maintenance/material-links/backfill-labels', null, { params: { dryRun } });
  return data;
}

/**
 * Восстановление человеческих имён материалов у связок с документами качества (issue #561).
 *
 * Связка запоминает имя материала с #554, но только при новой привязке — у заведённых раньше его нет,
 * и экран контроля показывает машинный ключ (`mb15-07-01m-54` — это боковая панель ВРУ). Имя
 * вычислимо: оно лежит в строках подключённых наборов данных, надо их прочитать.
 *
 * Читать приходится файлы, поэтому это действие администратора, а не миграция на старте. Сухой
 * прогон честный: файлы действительно разбираются, просто ничего не сохраняется.
 */
export function MaterialLabelSection() {
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
      // Пересчёта НЕ делаем: проход читает и разбирает файлы наборов, и повторять его ради
      // косметики значит гонять всю работу второй раз. Остаток считаем арифметикой.
      setReport({ ...result, named: 0, notFound: result.notFound, dryRun: true });
    } catch (e) {
      setError(apiError(e, 'Не удалось выполнить'));
    } finally { setBusy(false); }
  }

  const nothingToDo = report !== null && report.named === 0;

  return (
    <CollapsibleSection title="Имена материалов в связках" storageKey="material-labels" defaultOpen={false}>
      <div className="space-y-3">
        <p className="text-xs text-fg3">
          Связки, заведённые до появления имени материала, показываются машинным ключом
          (например <span className="font-mono">mb15-07-01m-54</span>). Имя можно восстановить из
          наборов данных, подключённых к документам.
        </p>

        <div className="flex items-center gap-2">
          <Button variant="outlined" size="sm" onClick={() => void check()} loading={busy}
            icon={<Tags size={14} />}>
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
          <p className="text-xs text-success">Готово: имён восстановлено {done.named}.</p>
        )}

        {report && (
          nothingToDo
            ? <p className="text-xs text-fg3">
                Восстанавливать нечего: {report.linksWithoutLabel === 0
                  ? 'у всех связок имена уже есть.'
                  : 'ни один из материалов не встречается в подключённых наборах данных.'}
              </p>
            : (
              <ul className="text-xs text-fg2 space-y-1">
                <li>Связок без имени: <b>{report.linksWithoutLabel}</b></li>
                <li>Имя найдётся: <b>{report.named}</b> (прочитано документов: {report.documentsScanned})</li>
                {report.documentsFailed > 0 && (
                  <li className="text-warning">
                    Не удалось прочитать документов: {report.documentsFailed} — их материалы в поиске
                    не участвовали.
                  </li>
                )}
                {report.notFound > 0 && (
                  <li className="text-fg3">
                    Останутся с ключом: {report.notFound} — этих материалов больше нет ни в одном
                    подключённом наборе, и придумывать им имя неоткуда.
                  </li>
                )}
              </ul>
            )
        )}
      </div>

      <ConfirmDialog
        open={confirmOpen} onOpenChange={setConfirmOpen}
        title="Восстановить имена материалов?"
        description={
          report
            ? `Будет заполнено имён: ${report.named}. Уже заданные имена не перезаписываются, `
              + 'сами связки не меняются.'
            : ''
        }
        confirmLabel="Выполнить"
        onConfirm={() => apply()}
      />
    </CollapsibleSection>
  );
}
