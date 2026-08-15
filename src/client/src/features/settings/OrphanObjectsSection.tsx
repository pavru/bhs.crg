import { useState } from 'react';
import { Unlink, Loader2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { apiClient } from '@/shared/api/client';
import { apiError } from '@/shared/utils/apiError';
import { CollapsibleSection } from './CollapsibleSection';

interface Report {
  objects: number;
  qualityDocuments: number;
  materialLinks: number;
  withData: number;
  /** Сирот, на которые ещё ссылаются живые записи — их уборка не трогает. */
  referenced: number;
  total: number;
  dryRun: boolean;
}

async function run(dryRun: boolean): Promise<Report> {
  const { data } = await apiClient.post<Report>('/maintenance/orphan-objects/cleanup', null, { params: { dryRun } });
  return data;
}

/**
 * Уборка объектов, чьё место расположения больше не существует (issue #739).
 *
 * Появлялись они так: у объектов нет внешнего ключа на комплект (ось расположения полиморфна), база
 * уносила разделы за стройкой и комплекты за разделом, а объекты оставляла — прикладной каскад был
 * только у комплекта. Причина закрыта, но след в старых базах остаётся, и восстановление прежней
 * резервной копии способно привезти его снова.
 *
 * Отдельно показываем объекты С ДАННЫМИ: пустые — это лениво созданные профили уровней, терять там
 * нечего, а вот запись с содержимым стоит посмотреть глазами прежде, чем нажимать «удалить».
 */
export function OrphanObjectsSection() {
  const [report, setReport] = useState<Report | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [done, setDone] = useState<number | null>(null);

  async function check() {
    setBusy(true); setError(''); setDone(null);
    try { setReport(await run(true)); }
    catch (e) { setError(apiError(e, 'Не удалось посчитать')); }
    finally { setBusy(false); }
  }

  /**
   * Ошибку НЕ глотаем: диалог подтверждения — единый канал для отказов (см. ConfirmDialog), и при
   * reject он остаётся открытым с причиной. Проглоти мы её здесь — диалог закрылся бы так, будто
   * удаление прошло, а провал остался бы мелкой строкой в свёрнутой секции.
   */
  async function apply() {
    setBusy(true); setError('');
    try {
      const result = await run(false);
      setDone(result.total);
      setReport(await run(true)); // пересчёт дёшев — один запрос к базе, без разбора файлов
    } finally { setBusy(false); }
  }

  const nothingToDo = report !== null && report.total === 0;

  return (
    <CollapsibleSection title="Потерянные объекты" storageKey="orphan-objects" defaultOpen={false}>
      <div className="space-y-3">
        <p className="text-xs text-fg3">
          Документы и записи общих данных, чей комплект, раздел или стройка удалены. В интерфейсе они
          не видны, но остаются в базе — попадают в поиск, в резервную копию и в проверки ссылок.
        </p>

        <div className="flex items-center gap-2">
          <Button variant="outlined" size="sm" onClick={() => void check()} loading={busy}
            icon={<Unlink size={14} />}>
            Посчитать
          </Button>
          {report && !nothingToDo && (
            <Button variant="filled" size="sm" danger onClick={() => setConfirmOpen(true)} disabled={busy}>
              Удалить
            </Button>
          )}
          {busy && <Loader2 size={14} className="animate-spin text-brand" />}
        </div>

        {error && <p className="text-xs text-danger">{error}</p>}

        {done !== null && <p className="text-xs text-success">Готово: удалено объектов {done}.</p>}

        {report && (
          nothingToDo
            ? <p className="text-xs text-fg3">Потерянных объектов нет.</p>
            : (
              <ul className="text-xs text-fg2 space-y-1">
                <li>Будет удалено: <b>{report.total}</b></li>
                {report.objects > 0 && <li>Документов и записей общих данных: {report.objects}</li>}
                {report.qualityDocuments > 0 && <li>Документов качества: {report.qualityDocuments}</li>}
                {report.materialLinks > 0 && <li>Связок материалов: {report.materialLinks}</li>}
                {report.withData > 0 && (
                  <li className="text-warning">
                    С непустыми данными: {report.withData} — удаление необратимо, содержимое
                    восстановить будет неоткуда.
                  </li>
                )}
                {report.referenced > 0 && (
                  <li className="text-fg3">
                    Останутся: {report.referenced} — на них ещё ссылаются живые записи, и ссылки эти
                    работают. Удалить их значило бы своими руками сделать ссылку висячей.
                  </li>
                )}
              </ul>
            )
        )}
      </div>

      <ConfirmDialog
        open={confirmOpen} onOpenChange={setConfirmOpen}
        title="Удалить потерянные объекты?"
        description={
          report
            ? `Будет удалено объектов: ${report.total}`
              + (report.withData > 0 ? `, из них с данными: ${report.withData}.` : ' (все пустые).')
              + ' Восстановить их будет неоткуда — места, к которому они относились, уже нет.'
            : ''
        }
        requireCheckbox={report && report.withData > 0 ? 'Понимаю, что данные будут потеряны' : undefined}
        confirmLabel="Удалить"
        onConfirm={() => apply()}
      />
    </CollapsibleSection>
  );
}
