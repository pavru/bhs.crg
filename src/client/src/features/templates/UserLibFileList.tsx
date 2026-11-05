import { Icon } from '@/shared/ui/Icon';
import { Add, CodeBlocks, ErrorIcon, Folder, Login, Warning } from '@/shared/ui/icons';
import { NavSection } from '@/shared/ui/ListDetailShell';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { buildRows, ENTRYPOINT } from './userLibTree';
import type { UserLibFile, UserLibError, UserLibWarning } from '@/shared/api/typstUserLib';
import { Tooltip } from '@/shared/ui/Tooltip';
import { IconButton } from '@/shared/ui/Button';
import { ListItem } from '@/shared/ui/ListItem';
import { listDepthPad } from '@/shared/ui/listDepth';
import type { IconDef } from '@/shared/ui/icons/types';
import type { ReactNode } from 'react';

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
        label={ENTRYPOINT} depth={0} icon={Login} supporting="Подключает всё, что ниже"
        active={selected === ENTRYPOINT} dirty={dirty.has(ENTRYPOINT)}
        errors={errorsBy.get(ENTRYPOINT) ?? 0} warnings={warningsBy.get(ENTRYPOINT) ?? 0}
        onClick={() => onSelect(ENTRYPOINT)}
      />

      <div className="flex items-center justify-between pr-2">
        <NavSection label="Файлы библиотеки" />
        <IconButton label="Создать файл" className="-my-1" onClick={() => onCreate()} icon={Add} />
      </div>

      {files.length === 0 ? (
        <p className="px-4 py-2 text-body-small text-on-surface-variant">
          Пока всё в одном файле. Создайте файл, чтобы вынести часть функций.
        </p>
      ) : rows.map(row => row.kind === 'folder' ? (
        /* «+» на папке — то же правило, что у заголовка секции, уровнем ниже: «создать в этом
           контейнере». Кебаб был бы хуже — единственный пункт под безликим глифом и лишний клик.
           Это ускоритель, а не единственный путь: создание через шапку секции остаётся. */
        <div key={`d:${row.path}`}
          className={`flex items-center gap-4 min-h-12 ${listDepthPad(row.depth)} pr-2 text-title-small text-on-surface-variant`}>
          <Icon icon={Folder} size={24} className="shrink-0" />
          <span className="truncate flex-1">{row.label}</span>
          <IconButton label={`Создать файл в «${row.path}»`} onClick={() => onCreate(row.path)} icon={Add} />
        </div>
      ) : (
        <FileRow
          key={row.path} label={row.label} depth={row.depth} icon={CodeBlocks}
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

/**
 * Строка файла — `ListItem` ([UI-84], sharedui-md3-18) с отступом `depth` и «⋮» соседом цели, видным без
 * наведения. Полный путь — подсказкой `title` обрезанной подписи.
 */
function FileRow({ label, depth, icon, active, dirty, errors, warnings, title, supporting, onClick, actions }: {
  label: string; depth: number; icon: IconDef; active: boolean; dirty: boolean;
  errors: number; warnings: number; title?: string; supporting?: string; onClick: () => void; actions?: ReactNode;
}) {
  return (
    <ListItem icon={icon} headline={label} title={title} supporting={supporting} depth={depth} selected={active}
      onClick={onClick} action={actions}
      trailing={(errors > 0 || warnings > 0 || dirty) && (
        <>
          {/* Ошибка может прилететь из файла, который сейчас закрыт — значок на его строке
              единственное место, где её видно. */}
          {errors > 0 && (
            <Tooltip content={`Ошибок: ${errors}`}><span className="shrink-0 flex text-error"><Icon icon={ErrorIcon} size={18} /></span></Tooltip>
          )}
          {errors === 0 && warnings > 0 && (
            <Tooltip content={`Замечаний: ${warnings}`}><span className="shrink-0 flex text-warning"><Icon icon={Warning} size={18} /></span></Tooltip>
          )}
          {dirty && <Tooltip content="Не сохранено"><span className="shrink-0 h-1.5 w-1.5 rounded-full bg-on-surface-variant" /></Tooltip>}
        </>
      )} />
  );
}
