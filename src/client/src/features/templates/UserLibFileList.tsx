import { Icon } from '@/shared/ui/Icon';
import { Add, CodeBlocks, ErrorIcon, Folder, Login, Warning } from '@/shared/ui/icons';
import { NavSection } from '@/shared/ui/ListDetailShell';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { buildRows, ENTRYPOINT } from './userLibTree';
import type { UserLibFile, UserLibError, UserLibWarning } from '@/shared/api/typstUserLib';
import { Tooltip } from '@/shared/ui/Tooltip';

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
  files, selected, dirty, errors, warnings, onSelect, onCreate, onRename, onDelete,
}: {
  files: UserLibFile[];
  selected: string;
  /** Пути с несохранёнными правками — точка-маркер, как на вкладках редактора. */
  dirty: Set<string>;
  /** Ошибки и замечания порознь, а не одним `check`: до первого сохранения замечания приходят из
   *  чтения, а ошибок мы не знаем вовсе — подставлять вместо них `ok: true` значило бы утверждать,
   *  что библиотека собирается, ничего не компилируя (issue #496). */
  errors: UserLibError[];
  warnings: UserLibWarning[];
  onSelect: (path: string) => void;
  /** Создать файл; аргумент — папка, в которой создаём (пусто = корень дерева). */
  onCreate: (folder?: string) => void;
  onRename: (path: string) => void;
  onDelete: (path: string) => void;
}) {
  const rows = buildRows(files);
  const errorsBy = new Map<string, number>();
  const warningsBy = new Map<string, number>();
  for (const e of errors) errorsBy.set(e.path, (errorsBy.get(e.path) ?? 0) + 1);
  for (const w of warnings) warningsBy.set(w.path, (warningsBy.get(w.path) ?? 0) + 1);

  return (
    <div className="flex-1 min-h-0 overflow-y-auto pb-2">
      <NavSection label="Точка входа" />
      <FileRow
        label={ENTRYPOINT} depth={0} icon={<Icon icon={Login} size={18} />}
        active={selected === ENTRYPOINT} dirty={dirty.has(ENTRYPOINT)}
        errors={errorsBy.get(ENTRYPOINT) ?? 0} warnings={warningsBy.get(ENTRYPOINT) ?? 0}
        title="Подключает всё, что ниже"
        onClick={() => onSelect(ENTRYPOINT)}
      />

      <div className="flex items-center justify-between pr-2">
        <NavSection label="Файлы библиотеки" />
        <Tooltip content="Создать файл" name>
          <button type="button" onClick={() => onCreate()}
            className="h-6 w-6 inline-flex items-center justify-center rounded text-on-surface-variant hover:text-on-surface state-layer">
            <Icon icon={Add} size={18} />
          </button>
        </Tooltip>
      </div>

      {files.length === 0 ? (
        <p className="px-3 py-2 text-xs text-on-surface-variant">
          Пока всё в одном файле. Создайте файл, чтобы вынести часть функций.
        </p>
      ) : rows.map(row => row.kind === 'folder' ? (
        /* «+» на папке — то же правило, что у заголовка секции, уровнем ниже: «создать в этом
           контейнере». Кебаб был бы хуже — единственный пункт под безликим глифом и лишний клик.
           Это ускоритель, а не единственный путь: создание через шапку секции остаётся. */
        <div key={`d:${row.path}`} className="group/dir flex items-center gap-1.5 py-1 pr-2 text-xs text-on-surface-variant"
          style={{ paddingLeft: `${0.75 + row.depth}rem` }}>
          <Icon icon={Folder} size={18} className="shrink-0" />
          <span className="truncate flex-1">{row.label}</span>
          <Tooltip content={`Создать файл в «${row.path}»`} name>
            <button type="button" onClick={() => onCreate(row.path)}
              className="h-5 w-5 shrink-0 inline-flex items-center justify-center rounded text-on-surface-variant
                         hover:text-on-surface state-layer opacity-0 group-hover/dir:opacity-100
                         focus-visible:opacity-100">
              <Icon icon={Add} size={18} />
            </button>
          </Tooltip>
        </div>
      ) : (
        <FileRow
          key={row.path} label={row.label} depth={row.depth} icon={<Icon icon={CodeBlocks} size={18} />}
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
    <div className={`group flex items-center gap-1 pr-1.5 ${active ? 'bg-primary-container' : 'state-layer'}`}>
      <Tooltip content={title}>
        <button type="button" onClick={onClick} aria-current={active ? 'true' : undefined}
          className="flex-1 min-w-0 flex items-center gap-1.5 py-1.5 text-left text-sm"
          style={{ paddingLeft: `${0.75 + depth}rem` }}>
          <span className={`shrink-0 ${active ? 'text-primary' : 'text-on-surface-variant'}`}>{icon}</span>
          <span className={`truncate ${active ? 'text-on-surface font-medium' : 'text-on-surface-variant'}`}>{label}</span>
          {/* Ошибка может прилететь из файла, который сейчас закрыт — бейдж на его строке
              единственное место, где её видно. */}
          {errors > 0 && (
            <Tooltip content={`Ошибок: ${errors}`}><span className="shrink-0 text-error"><Icon icon={ErrorIcon} size={18} /></span></Tooltip>
          )}
          {errors === 0 && warnings > 0 && (
            <Tooltip content={`Замечаний: ${warnings}`}><span className="shrink-0 text-warning"><Icon icon={Warning} size={18} /></span></Tooltip>
          )}
          {dirty && <Tooltip content="Не сохранено"><span className="shrink-0 h-1.5 w-1.5 rounded-full bg-on-surface-variant" /></Tooltip>}
        </button>
      </Tooltip>
      {actions && <span className="shrink-0 opacity-0 group-hover:opacity-100 focus-within:opacity-100">{actions}</span>}
    </div>
  );
}
