import { useState } from 'react';
import { parseSourceColumnNames, parseSourceColumns } from '@/shared/api/datasetHelpers';
import { apiError } from '@/shared/utils/apiError';
import {
  useSetDataSetSourceProcessing, useSaveSourceAsTemplate, type SourceProcessingPatch,
} from '@/shared/api/datasetProcessing';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { RowFilterDialog } from './RowFilterDialog';
import { filterColumns } from '@/shared/filter/rowFilterModel';
import { ComputedColumnsDialog } from './ComputedColumnsDialog';
import { SortSpecDialog } from './SortSpecDialog';
import type { DataSetSource } from '@/shared/api/types';

/** Какой диалог обработки открыт: отбор, вычисляемые колонки, сортировка или «Сохранить как шаблон». */
export type ProcessingDialogKind = 'filter' | 'transforms' | 'sort' | 'template';

/** Мини-диалог: только имя нового шаблона — извлечение и обработку сервер возьмёт у источника сам. */
function SaveAsTemplateDialog({ defaultName, isPending, onSave, onClose }: {
  defaultName: string; isPending: boolean;
  onSave: (name: string) => Promise<unknown>; onClose: () => void;
}) {
  const [name, setName] = useState(defaultName);
  // Сервер шаблон с негодным отбором не примет (issue #1137), а с устаревшей копии источника — тоже
  // (issue #1141); причину показываем здесь же.
  const [error, setError] = useState<string | null>(null);
  const canSave = !isPending && !!name.trim();
  const submit = () => {
    if (!canSave) return;
    setError(null);
    onSave(name.trim()).catch(e => setError(apiError(e, 'Не удалось сохранить шаблон')));
  };

  return (
    <Modal
      open
      onOpenChange={o => { if (!o && !isPending) onClose(); }}
      title="Сохранить как шаблон"
      footer={
        <div className="flex gap-2 justify-end">
          <Button variant="text" size="sm" onClick={onClose} disabled={isPending}>Отмена</Button>
          <Button variant="filled" size="sm" onClick={submit} disabled={!canSave} loading={isPending}>
            {isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      }>
      <p className="text-xs mb-3 text-fg3">
        Row-selector/колонки (Extraction) и текущие Filter/Transformation/Sort источника
        станут переиспользуемым шаблоном — копия, не живая ссылка.
      </p>
      <TextField label="Название шаблона" value={name} onChange={e => setName(e.target.value)} autoFocus
        onKeyDown={e => { if (e.key === 'Enter') submit(); }} />
      {error && <p role="alert" className="mt-2 text-xs text-danger">{error}</p>}
    </Modal>
  );
}

/**
 * Диалоги обработки источника — все от одного СНИМКА (`base`): копии источника, какой она была на
 * странице в момент открытия (issue #1141). Из снимка берётся всё, что диалог показывает, — начальное
 * значение, предлагаемые колонки — и версия обработки, которую он назовёт серверу при сохранении.
 *
 * Не из живого `src`: страница перечитывает источники и под открытым диалогом (возврат на вкладку,
 * чужая мутация, отказ 409). Начальное значение диалог берёт один раз, при открытии, а версия и
 * колонки подтянулись бы свежие — и сервер получил бы черновик, собранный по старым данным, с версией,
 * говорящей «я видел новые». Сверка прошла бы, чужая правка была бы затёрта.
 *
 * Отказ сервера («источник тем временем изменили») диалог показывает у себя и не закрывается
 * (`useDialogSave`); повторное «Сохранить» из него же получит тот же отказ — снимок прежний.
 */
export function SourceProcessingDialogs({ kind, base, onClose }: {
  kind: ProcessingDialogKind; base: DataSetSource; onClose: () => void;
}) {
  const setProcessing = useSetDataSetSourceProcessing();
  const saveTemplate = useSaveSourceAsTemplate();

  const computedAliases = (base.computedColumns ?? []).map(c => c.alias).filter(Boolean);
  const sourceColumns = parseSourceColumnNames(base.cachedSchema);

  // Каждый диалог шлёт ТОЛЬКО свою часть (issue #1139): остальные сервер не трогает. Обещание отдаём
  // диалогу: он ждёт ответ и показывает отказ у себя (отбор и версию сервер проверяет).
  const save = (patch: SourceProcessingPatch) =>
    setProcessing.mutateAsync({ id: base.id, ifMatch: base.processingVersion, ...patch });

  switch (kind) {
    case 'filter':
      return (
        <RowFilterDialog columns={filterColumns(parseSourceColumns(base.cachedSchema), computedAliases)}
          initial={base.rowFilter} onSave={f => save({ rowFilter: f })} onClose={onClose} />
      );
    // Диалогу отдаём только колонки САМОГО источника, без вычисляемых псевдонимов (issue #539):
    // фишка несёт номер позиции, а позиции вычисляемых колонок меняются по ходу — при вычислении
    // N-й предыдущие уже есть, а следующих ещё нет, и номер врал бы. Сослаться на предыдущую
    // вычисляемую колонку по-прежнему можно, просто вручную.
    case 'transforms':
      return (
        <ComputedColumnsDialog initial={base.computedColumns} sourceColumns={sourceColumns}
          onSave={c => save({ computedColumns: c })} onClose={onClose} />
      );
    case 'sort':
      return (
        <SortSpecDialog columns={[...new Set([...sourceColumns, ...computedAliases])]} initial={base.sortSpec}
          onSave={s => save({ sortSpec: s })} onClose={onClose} />
      );
    case 'template':
      return (
        <SaveAsTemplateDialog defaultName={base.name} isPending={saveTemplate.isPending}
          onSave={name => saveTemplate
            .mutateAsync({ sourceId: base.id, name, ifMatch: base.processingVersion }).then(onClose)}
          onClose={onClose} />
      );
  }
}
