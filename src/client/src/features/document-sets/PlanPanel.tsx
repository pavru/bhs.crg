import { useMemo, useState } from 'react';
import { Plus, Trash2, Target } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { useToast } from '@/shared/ui/Toast';
import { TypePicker, type PickType } from '@/shared/ui/TypePicker';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useDocumentSetPlan, useReplaceDocumentSetPlan, type PlanRow } from '@/shared/api/plans';

/** Строка в правке: факт приходит с сервера и не редактируется. */
interface EditRow {
  documentTypeId: string;
  typeName: string;
  plannedCount: number;
  actualCount: number;
}

const EMPTY: PlanRow[] = [];

/**
 * План комплекта (issue #796): каких документов и сколько должно быть.
 *
 * План — справочный индикатор, а не запрет: он не мешает ни завести документ вне плана, ни собрать
 * комплект. Отсюда и тон экрана — «сколько осталось», а не «нарушение».
 *
 * Правится списком и сохраняется целиком: сервер принимает план заменой, поэтому «убрать строку»
 * здесь — это убрать её из списка и сохранить, а не отдельное действие с подтверждением.
 */
export function PlanPanel({ setId }: { setId: string }) {
  const { data: saved = EMPTY, isLoading } = useDocumentSetPlan(setId);

  if (isLoading) return <div className="text-sm text-fg4">Загрузка…</div>;

  // Форма пересоздаётся КЛЮЧОМ, а не эффектом «пришли данные — перезаписать состояние». Эффект,
  // синхронизирующий состояние с пропсом, — лишний проход рендера и чужие правки поверх твоих; в
  // этом проекте он уже давал цикл рендера. Подпись меняется только при РЕАЛЬНОМ изменении плана
  // или факта, поэтому фоновая перепроверка тех же данных не сбрасывает недописанную строку.
  return <PlanEditor key={`${setId}:${signature(saved)}`} setId={setId} saved={saved} />;
}

function signature(rows: PlanRow[]): string {
  return rows.map(r => `${r.documentTypeId}:${r.plannedCount}:${r.actualCount}`).sort().join('|');
}

function PlanEditor({ setId, saved }: { setId: string; saved: PlanRow[] }) {
  const { data: allTypes = [] } = useListDocumentTypes();
  const replace = useReplaceDocumentSetPlan();
  const toast = useToast();

  const [rows, setRows] = useState<EditRow[]>(() => saved.map(r => ({ ...r })));
  const [pickerOpen, setPickerOpen] = useState(false);

  const dirty = useMemo(() => {
    if (rows.length !== saved.length) return true;
    const before = new Map(saved.map(r => [r.documentTypeId, r.plannedCount]));
    return rows.some(r => before.get(r.documentTypeId) !== r.plannedCount);
  }, [rows, saved]);

  const used = new Set(rows.map(r => r.documentTypeId));
  // Абстрактные типы в план не годятся: документа такого типа не бывает, значит позиция была бы
  // неисполнимой. Уже добавленные тоже прячем — на тип приходится одна строка.
  const pickable: PickType[] = allTypes
    .filter(t => t.kind === 'Document' && !t.isAbstract && !used.has(t.id))
    .map(t => ({ id: t.id, name: t.name, code: t.code, section: t.group ?? 'Без группы' }));

  const planned = rows.reduce((acc, r) => acc + r.plannedCount, 0);
  const closed = rows.reduce((acc, r) => acc + Math.min(r.actualCount, r.plannedCount), 0);

  function addType(id: string) {
    const t = allTypes.find(x => x.id === id);
    if (!t) return;
    setRows(prev => [...prev, { documentTypeId: t.id, typeName: t.name, plannedCount: 1, actualCount: 0 }]);
    setPickerOpen(false);
  }

  async function save() {
    try {
      await replace.mutateAsync({
        setId,
        rows: rows.map(r => ({ documentTypeId: r.documentTypeId, plannedCount: r.plannedCount })),
      });
      // Тоста об успехе нет намеренно: результат виден прямо здесь — таблица и «закрыто позиций»
      // перечитываются с сервера. Тост тут дублировал бы то, на что человек и так смотрит.
    } catch (e: unknown) {
      toast.apiError(e, 'Не удалось сохранить план');
    }
  }

  return (
    <div className="mx-auto max-w-3xl space-y-4">
      <header className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-sm font-medium text-fg1">План по документам</h2>
          <p className="text-xs text-fg4 mt-0.5">
            Каких документов и сколько должно быть в комплекте. Готовность считается по выпущенным:
            черновик не закрывает позицию. Плана нет — проценты нигде не показываются.
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <Button variant="text" size="sm" icon={<Plus size={16} />} onClick={() => setPickerOpen(true)}>
            Добавить тип
          </Button>
          <Button variant="filled" size="sm" disabled={!dirty} loading={replace.isPending} onClick={save}>
            Сохранить
          </Button>
        </div>
      </header>

      {rows.length === 0 ? (
        <EmptyState icon={<Target size={22} />} title="Плана нет"
          description="Добавьте типы документов и укажите количество — тогда на комплекте, разделе и стройке появится процент готовности." />
      ) : (
        <>
          <div className="border border-stroke rounded-xl overflow-hidden">
            <div className="grid grid-cols-[1fr_7rem_7rem_2.5rem] gap-2 px-3 py-2 bg-muted text-[11px] uppercase tracking-wide text-fg4">
              <span>Тип документа</span>
              <span className="text-right">План</span>
              <span className="text-right">Выпущено</span>
              <span />
            </div>
            {rows.map((r, i) => (
              <div key={r.documentTypeId}
                className="grid grid-cols-[1fr_7rem_7rem_2.5rem] gap-2 items-center px-3 py-1.5 border-t border-stroke text-sm">
                <span className="truncate text-fg1" title={r.typeName}>{r.typeName}</span>
                <input type="number" min={1} value={r.plannedCount}
                  aria-label={`План по типу «${r.typeName}»`}
                  onChange={e => {
                    // Минимум единица: ноль — это отсутствие строки, и сервер такую отвергает.
                    const n = Math.max(1, Math.floor(Number(e.target.value) || 1));
                    setRows(prev => prev.map((x, j) => (j === i ? { ...x, plannedCount: n } : x)));
                  }}
                  className="h-8 px-2 rounded-lg border border-stroke bg-surface text-right tabular-nums
                             focus:outline-none focus:border-brand" />
                <span className={`text-right tabular-nums ${
                  r.actualCount >= r.plannedCount ? 'text-brand' : 'text-fg3'}`}>
                  {r.actualCount}
                </span>
                <button type="button" aria-label={`Убрать «${r.typeName}» из плана`}
                  onClick={() => setRows(prev => prev.filter((_, j) => j !== i))}
                  className="h-7 w-7 grid place-items-center rounded-lg text-fg4 hover:text-danger hover:bg-danger-subtle transition-colors">
                  <Trash2 size={15} />
                </button>
              </div>
            ))}
          </div>

          <p className="text-xs text-fg4">
            Закрыто позиций: <span className="tabular-nums text-fg2">{closed} из {planned}</span>.
            {' '}Документы сверх плана в счёт не идут, типы вне плана — тоже.
          </p>
        </>
      )}

      <TypePicker open={pickerOpen} onOpenChange={setPickerOpen} types={pickable}
        title="Тип документа в план" recentKey="plan" onSelect={addType} />
    </div>
  );
}
