import { FileCode, Folder, LogIn, Plus, AlertCircle, AlertTriangle } from 'lucide-react';
import { NavSection } from '@/shared/ui/ListDetailShell';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { buildRows, ENTRYPOINT } from './userLibTree';
import type { UserLibFile, UserLibCheck } from '@/shared/api/typstUserLib';

/**
 * Список файлов библиотеки (issue #473).
 *
 * ПЛОСКИЙ список с отступом, а не сворачиваемое дерево: смысл разрезания одного файла на много —
 * видеть инвентарь целиком, и свёрнутые папки воссоздали бы ровно ту проблему, которую мы решаем.
 *
 * Точка входа — отдельной секцией, а не бейджем на общей строке: особость позицией объясняет себя до
 * первого касания, а украшение на обычной строке porождает вопрос «почему у неё нет „Удалить“».
 * Пунктов удаления/переименования у неё просто НЕТ, а не задизейблены.
 */
export function UserLibFileList({
  files, selected, dirty, check, onSelect, onCreate, onRename, onDelete,
}: {
  files: UserLibFile[];
  selected: string;
  /** Пути с несохранёнными правками — точка-маркер, как на вкладках редактора. */
  dirty: Set<string>;
  check: UserLibCheck | null;
  onSelect: (path: string) => void;
  onCreate: () => void;
  onRename: (path: string) => void;
  onDelete: (path: string) => void;
}) {
  const rows = buildRows(files);
  const errorsBy = new Map<string, number>();
  const warningsBy = new Map<string, number>();
  for (const e of check?.errors ?? []) errorsBy.set(e.path, (errorsBy.get(e.path) ?? 0) + 1);
  for (const w of check?.warnings ?? []) warningsBy.set(w.path, (warningsBy.get(w.path) ?? 0) + 1);

  return (
    <div className="flex-1 min-h-0 overflow-y-auto pb-2">
      <NavSection label="Точка входа" />
      <FileRow
        label={ENTRYPOINT} depth={0} icon={<LogIn size={13} />}
        active={selected === ENTRYPOINT} dirty={dirty.has(ENTRYPOINT)}
        errors={errorsBy.get(ENTRYPOINT) ?? 0} warnings={warningsBy.get(ENTRYPOINT) ?? 0}
        title="Подключает всё, что ниже"
        onClick={() => onSelect(ENTRYPOINT)}
      />

      <div className="flex items-center justify-between pr-2">
        <NavSection label="Файлы библиотеки" />
        <button type="button" onClick={onCreate} title="Создать файл"
          className="h-6 w-6 inline-flex items-center justify-center rounded text-fg3 hover:text-fg1 hover:bg-muted transition-colors">
          <Plus size={14} />
        </button>
      </div>

      {files.length === 0 ? (
        <p className="px-3 py-2 text-xs text-fg4">
          Пока всё в одном файле. Создайте файл, чтобы вынести часть функций.
        </p>
      ) : rows.map(row => row.kind === 'folder' ? (
        <div key={`d:${row.path}`} className="flex items-center gap-1.5 py-1 text-xs text-fg4"
          style={{ paddingLeft: `${0.75 + row.depth}rem` }}>
          <Folder size={12} className="shrink-0" />{row.label}
        </div>
      ) : (
        <FileRow
          key={row.path} label={row.label} depth={row.depth} icon={<FileCode size={13} />}
          active={selected === row.path} dirty={dirty.has(row.path)}
          errors={errorsBy.get(row.path) ?? 0} warnings={warningsBy.get(row.path) ?? 0}
          title={row.path}
          onClick={() => onSelect(row.path)}
          actions={
            <RowActionsMenu actions={[
              { key: 'rename', label: 'Изменить путь', onSelect: () => onRename(row.path) },
              { key: 'delete', label: 'Удалить', onSelect: () => onDelete(row.path), danger: true },
            ]} />
          }
        />
      ))}
    </div>
  );
}

function FileRow({ label, depth, icon, active, dirty, errors, warnings, title, onClick, actions }: {
  label: string; depth: number; icon: React.ReactNode; active: boolean; dirty: boolean;
  errors: number; warnings: number; title?: string; onClick: () => void; actions?: React.ReactNode;
}) {
  return (
    <div className={`group flex items-center gap-1 pr-1.5 ${active ? 'bg-brand-subtle' : 'hover:bg-muted'}`}>
      <button type="button" onClick={onClick} title={title} aria-current={active ? 'true' : undefined}
        className="flex-1 min-w-0 flex items-center gap-1.5 py-1.5 text-left text-sm"
        style={{ paddingLeft: `${0.75 + depth}rem` }}>
        <span className={`shrink-0 ${active ? 'text-brand' : 'text-fg3'}`}>{icon}</span>
        <span className={`truncate ${active ? 'text-fg1 font-medium' : 'text-fg2'}`}>{label}</span>
        {/* Ошибка может прилететь из файла, который сейчас закрыт — бейдж на его строке
            единственное место, где её видно. */}
        {errors > 0 && (
          <span title={`Ошибок: ${errors}`} className="shrink-0 text-danger"><AlertCircle size={12} /></span>
        )}
        {errors === 0 && warnings > 0 && (
          <span title={`Замечаний: ${warnings}`} className="shrink-0 text-warning"><AlertTriangle size={12} /></span>
        )}
        {dirty && <span title="Не сохранено" className="shrink-0 h-1.5 w-1.5 rounded-full bg-fg3" />}
      </button>
      {actions && <span className="shrink-0 opacity-0 group-hover:opacity-100 focus-within:opacity-100">{actions}</span>}
    </div>
  );
}
