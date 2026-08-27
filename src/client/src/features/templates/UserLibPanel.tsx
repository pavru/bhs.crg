import { useState, useEffect, useLayoutEffect, useRef, useMemo, useCallback } from 'react';
import Editor from '@/shared/ui/CodeEditor';
import type * as monacoEditor from 'monaco-editor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/ThemeProvider';
import { useUserLibCompletion } from '@/shared/ui/typstUserLibCompletion';
import { useAssetCompletion } from '@/shared/ui/typstAssetCompletion';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Save, AlertCircle, AlertTriangle } from 'lucide-react';
import {
  useTypstUserLib, useSaveTypstUserLib,
  type UserLibFile, type UserLibCheck, type UserLibError,
} from '@/shared/api/typstUserLib';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
import { UserLibFileList } from './UserLibFileList';
import { UserLibPathDialog } from './UserLibPathDialog';
import {
  ENTRYPOINT, referencingFiles, mergeFiles, mergeText, treeKey, checkStillApplies,
} from './userLibTree';

// ─── User Typst library: точка входа + дерево файлов (issue #473) ─────────────

/** Пустое дерево модульной константой: инлайновый `[]` — новая ссылка на каждый рендер (#305). */
const NO_FILES: UserLibFile[] = [];
/** По той же причине: `errors` — зависимость эффекта маркеров, инлайновый `[]` гонял бы его на каждое нажатие клавиши. */
const NO_ERRORS: UserLibError[] = [];

export function UserLibPanel() {
  const { resolvedTheme } = useTheme();
  useUserLibCompletion();
  useAssetCompletion();
  const { data, isLoading, isFetching, dataUpdatedAt, refetch } = useTypstUserLib();
  const saveMutation = useSaveTypstUserLib();

  const [entry, setEntry] = useState('');
  const [files, setFiles] = useState<UserLibFile[]>(NO_FILES);
  const [selected, setSelected] = useState(ENTRYPOINT);
  const [check, setCheck] = useState<UserLibCheck | null>(null);
  const [checkAt, setCheckAt] = useState(0);
  const [checkedKey, setCheckedKey] = useState<string | null>(null);
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');
  const [pathDialog, setPathDialog] = useState<{ mode: 'create' | 'rename'; path: string } | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);

  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const foldedOnce = useRef(false);

  const serverEntry = data?.content ?? '';
  const serverFiles = data?.files ?? NO_FILES;

  // Перечитывание НЕ затирает несохранённое (issue #500): запрос обновляется по фокусу окна, и
  // безусловная синхронизация молча уносила бы правки во всех файлах — вместе с точками-маркерами,
  // по которым это можно было бы заметить.
  //
  // Сливаем ПО БАЗЕ — предыдущему снимку сервера, — а не держим глухой заслон «есть несохранённое,
  // значит серверные данные ждут» (issue #501): заслон закрывался бы навсегда от чужого нового
  // файла, потому что тот сам числится «изменённым», в списке не виден, а «Сохранить всё» отправило
  // бы дерево без него — и сервер удалил бы его как лишний.
  const baseRef = useRef<{ entry: string; files: UserLibFile[] } | null>(null);
  useEffect(() => {
    if (!data) return;
    const base = baseRef.current;
    baseRef.current = { entry: serverEntry, files: serverFiles };
    // Через функциональный setState, а не по entry/files из замыкания: они не в зависимостях (иначе
    // эффект бежал бы на каждое нажатие клавиши), и читать их отсюда значило бы читать устаревшее.
    setEntry(prev => mergeText(prev, base?.entry ?? null, serverEntry));
    setFiles(prev => mergeFiles(prev, base?.files ?? null, serverFiles));
  }, [data, serverEntry, serverFiles]);

  // Несохранённые файлы — точка-маркер на строке, идиома вкладок редактора.
  const dirty = useMemo(() => {
    const set = new Set<string>();
    if (entry !== serverEntry) set.add(ENTRYPOINT);
    const byPath = new Map(serverFiles.map(f => [f.path, f.content]));
    for (const f of files) if (byPath.get(f.path) !== f.content) set.add(f.path);
    for (const f of serverFiles) if (!files.some(x => x.path === f.path)) set.add(f.path);
    return set;
  }, [entry, files, serverEntry, serverFiles]);

  const isDirty = dirty.size > 0;

  // Выбранный файл мог исчезнуть, пока мы его смотрели: соседняя вкладка сохранила дерево без него,
  // и refetch подменил files. Вычисляем активный файл ПРИ ОТРИСОВКЕ, а не поправляем эффектом:
  // иначе один кадр редактор показывал бы пустоту, а нажатия в него пропадали бы молча —
  // updateCurrent перебирает files и не находит такого пути (issue #498).
  const active = selected === ENTRYPOINT || files.some(f => f.path === selected) ? selected : ENTRYPOINT;
  const currentContent = active === ENTRYPOINT
    ? entry
    : files.find(f => f.path === active)?.content ?? '';

  function updateCurrent(value: string) {
    if (active === ENTRYPOINT) setEntry(value);
    else setFiles(prev => prev.map(f => (f.path === active ? { ...f, content: value } : f)));
  }

  /**
   * Сохранение — ВСЕГО дерева разом. Пофайлового нет намеренно: правка файла и правка зовущего его
   * файла обязаны лечь вместе, иначе между двумя запросами библиотека не собирается, а её читает
   * генерация каждого документа.
   */
  const handleSave = useCallback(async () => {
    setError('');
    try {
      // Время снимаем ДО запроса (issue #505): сохранение само запускает перечитывание, и
      // mutateAsync ждёт его — onSuccess возвращает промис invalidateQueries. Отметка, снятая после
      // await, всегда оказывалась бы позже dataUpdatedAt, и условие «проверка свежее чтения» было бы
      // истинным после КАЖДОГО сохранения, подменяя собой сравнение отпечатков.
      const startedAt = Date.now();
      const res = await saveMutation.mutateAsync({ content: entry, files });
      setCheck(res.check);
      setCheckAt(startedAt);
      setCheckedKey(treeKey(res.content, res.files));   // дерево, к которому относится эта проверка
      setSavedMsg(true);
      setTimeout(() => setSavedMsg(false), 2000);
    } catch (err: unknown) {
      // Текст сервера, а не axios'ово «Request failed with status code 400» (issue #511): отказ
      // сохранения объясняется именно там — какой путь и чем не годится, — и без этого библиотека
      // выглядит несохраняемой без причины.
      const fromServer = (err as { response?: { data?: { error?: string } } })?.response?.data?.error;
      setError(fromServer ?? (err instanceof Error ? err.message : 'Ошибка сохранения'));
    }
  }, [entry, files, saveMutation]);

  // Ctrl+S — перехват на окне в фазе capture, чтобы опередить браузерное «Сохранить страницу», и по
  // e.code, потому что на русской раскладке e.key даёт «ы». Команду Monaco НЕ регистрируем: она
  // сработала бы вторым, параллельным сохранением.
  const saveRef = useRef(handleSave);
  /**
   * Свежий колбэк для отложенного вызова. Обновляется ЭФФЕКТОМ, а не присваиванием в рендере
   * (issue #858): рендер обязан быть чистым, а запись в ref — побочное действие, которое к тому же
   * случалось бы и на брошенном рендере (StrictMode, прерванный конкурентный проход).
   *
   * Эффект именно СЛОЙНЫЙ (useLayoutEffect). Обычный отложен до после отрисовки, и между коммитом
   * и его срабатыванием ref держит ПРЕДЫДУЩИЙ колбэк — с прежними entry/files. Ctrl+S, попавший в
   * эту щель, молча сохранил бы старое содержимое библиотеки. Слойный эффект выполняется в том же
   * задании, что и коммит: события в промежуток не попадают, щели нет.
   */
  useLayoutEffect(() => { saveRef.current = handleSave; });
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyS') {
        e.preventDefault();
        if (editorRef.current?.hasTextFocus()) void saveRef.current();
      }
    }
    window.addEventListener('keydown', onKeyDown, { capture: true });
    return () => window.removeEventListener('keydown', onKeyDown, { capture: true });
  }, []);

  // Уход со страницы с несохранённым деревом — единственное место, где предупреждаем: переключение
  // между файлами это навигация внутри правки, а не коммит, и диалог на нём быстро обесценился бы.
  useEffect(() => {
    if (!isDirty) return;
    const onBeforeUnload = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [isDirty]);

  // Библиотека читается как список функций; развёрнутой она простыня. Сворачиваем один раз при
  // первой загрузке — повтор схлопывал бы блок прямо во время правки.
  const foldFunctions = useCallback(() => {
    if (foldedOnce.current || !editorRef.current || !serverEntry.trim()) return;
    foldedOnce.current = true;
    const ed = editorRef.current;
    setTimeout(() => ed.getAction('editor.foldLevel1')?.run(), 0);
  }, [serverEntry]);
  useEffect(() => { foldFunctions(); }, [foldFunctions]);

  // Относится ли последняя проверка к тому, что сейчас на сервере (issue #502). Проверка — чистая
  // функция дерева, поэтому её вывод в силе, пока сохранённое дерево то же самое; отпечаток и
  // сравниваем. Второе условие — на случай, когда перечитывание после сохранения не дошло: тогда
  // проверка описывает дерево СВЕЖЕЕ прочитанного, и верить надо ей.
  //
  // Без этого мы показывали «всё в порядке» после того, как сосед сломал библиотеку: ошибки брались
  // из своей давней успешной проверки, и ни красной строки, ни строки «не проверялась» не было —
  // ровно то молчаливое зелёное, против которого написан баннер ниже. И наоборот: устаревшие ошибки
  // держали красную полосу, а она гасит блок замечаний, пряча уже свежие.
  const serverKey = useMemo(() => treeKey(serverEntry, serverFiles), [serverEntry, serverFiles]);
  const checkApplies = check !== null
    && checkStillApplies({ checkedKey, checkedAt: checkAt, serverKey, dataUpdatedAt });
  const errors = checkApplies ? check.errors : NO_ERRORS;
  // Ошибки сборки и ошибки неподключённых файлов разводим (issue #506): первые останавливают
  // генерацию ВСЕХ документов, вторые — только шаблонов, импортирующих такой файл напрямую.
  const buildErrors = errors.filter(e => e.inBuild);
  const orphanErrors = errors.filter(e => !e.inBuild);

  /** Ошибка в открытом файле — маркерами Monaco: там, где она есть. */
  useEffect(() => {
    const monaco = (window as unknown as { monaco?: typeof monacoEditor }).monaco;
    const model = editorRef.current?.getModel();
    if (!monaco || !model) return;
    const mine = errors.filter(e => e.path === active);
    monaco.editor.setModelMarkers(model, 'userlib', mine.map(e => ({
      severity: monaco.MarkerSeverity.Error,
      message: e.message,
      startLineNumber: Math.max(1, e.line), startColumn: Math.max(1, e.column),
      endLineNumber: Math.max(1, e.line), endColumn: Math.max(1, e.column) + 1,
    })));
  }, [errors, active]);

  function handleCreate(path: string) {
    setFiles(prev => [...prev, { path, content: `// ${path}\n` }]);
    // Точку входа НЕ трогаем (issue #492): импорты — забота пользователя. Про неподключённый файл
    // приложение молчит намеренно (issue #494): пока импорт дописывало оно само, «не подключён»
    // означало сбой, а теперь это обычный ход работы — файл создан, функции пишутся, подключат
    // позже. Сообщаем о том, что сломано (ошибки сборки), и молчим о том, что не закончено.
    setSelected(path);
  }

  function handleRename(from: string, to: string) {
    setFiles(prev => prev.map(f => (f.path === from ? { ...f, path: to } : f)));
    if (selected === from) setSelected(to);
  }

  function handleDelete(path: string) {
    setFiles(prev => prev.filter(f => f.path !== path));
    if (selected === path) setSelected(ENTRYPOINT);
    setDeleting(null);
  }

  if (isLoading) {
    return <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>;
  }

  // Чтение не удалось — показываем это, а не пустую панель (issue #504). Без ветки библиотека
  // выглядела как пустая: редактор чист, дерево пусто, «Изменено» нет, и ничто не говорит о сбое —
  // а «Сохранить всё» отправляло бы `{ content: '', files: [] }`, после чего сохранение целым
  // деревом (иначе половина рефакторинга легла бы без второй) удаляет ВСЕ файлы и стирает точку
  // входа. Одна неудачная загрузка при перезапуске API — и библиотеки нет.
  if (!data) {
    return (
      <div className="flex-1 flex flex-col items-center justify-center gap-3 text-sm">
        <AlertCircle size={20} className="text-danger" />
        <p className="text-fg2">Не удалось загрузить библиотеку.</p>
        <p className="text-fg4 text-xs max-w-sm text-center">
          Правка недоступна, пока библиотека не прочитана: сохранение заменяет дерево целиком и
          стёрло бы его.
        </p>
        <Button variant="outlined" size="sm" onClick={() => void refetch()} loading={isFetching}>
          Повторить
        </Button>
      </div>
    );
  }

  // Замечания до первого сохранения приходят из ответа чтения — чтобы дубликаты имён были видны и
  // после перезагрузки страницы, а не только сразу после сохранения (issue #492).
  //
  // Диагностика описывает СОХРАНЁННОЕ состояние и НЕ фильтруется (issue #498). Прошлая попытка
  // убрать записи о локально удалённых файлах давала худший исход, чем чинила: переименовал
  // сломанный файл — и красная строка исчезла, хотя сохранённая библиотека по-прежнему не
  // собирается и генерация всех документов стоит. Это ровно то «молчаливое зелёное», против
  // которого написан комментарий в UserLibFileList.
  //
  // Безопасным делаем ПЕРЕХОД: строка ведёт в файл, только если он есть в локальном дереве, иначе
  // это просто текст. Иначе клик открыл бы пустой редактор, и набранное молча пропадало бы —
  // updateCurrent перебирает files и не находит совпадения (issue #496).
  const alive = (path: string) => path === ENTRYPOINT || files.some(f => f.path === path);

  // Замечания — по тому же признаку (issue #500, #502). Порядком «сначала check» мы залипали: раз
  // сохранив в этой вкладке, навсегда закрывали пустым check.warnings всё, что появилось у соседей.
  // Но и «сначала data» слепо брать нельзя: чтение после сохранения может не дойти (сеть,
  // перезапуск сервера), и тогда мы держали бы на экране замечания ДО правки, выбросив точный ответ
  // проверки.
  const warnings = checkApplies ? check.warnings : data?.warnings ?? [];

  return (
    <div className="flex h-full min-h-0">
      <aside className="w-72 shrink-0 border-r border-stroke flex flex-col bg-base">
        <UserLibFileList
          files={files} selected={active} dirty={dirty} errors={errors} warnings={warnings}
          onSelect={setSelected}
          onCreate={folder => setPathDialog({ mode: 'create', path: folder ? `${folder}/` : '' })}
          onRename={p => setPathDialog({ mode: 'rename', path: p })}
          onDelete={p => setDeleting(p)}
        />
        <TemplateAssetsPanel scope="System" scopeId={null} title="Системные ассеты" />
      </aside>

      <div className="flex-1 min-w-0 flex flex-col">
        <div className="flex items-center justify-between px-4 py-2 border-b border-stroke bg-surface gap-3">
          <p className="text-xs text-fg3 truncate">
            {active === ENTRYPOINT ? (
              <>Доступен в каждом шаблоне через{' '}
                <code className="font-mono bg-muted px-1.5 py-0.5 rounded text-fg2">#import "userlib.typ": *</code>
              </>
            ) : (
              <>Файл библиотеки — <code className="font-mono bg-muted px-1.5 py-0.5 rounded text-fg2">userlib/{active}</code></>
            )}
          </p>
          <div className="flex items-center gap-2 shrink-0">
            {savedMsg && !isDirty && <span className="text-xs text-success">Сохранено</span>}
            {/* Полный текст под курсором: строка в шапке узкая, а объяснение отказа сервера
                («какой путь и чем не годится») в неё не помещается (issue #511). */}
            {error && <span className="text-xs text-danger max-w-xs truncate" title={error}>{error}</span>}
            {isDirty && <span className="text-xs text-fg3">Изменено: {dirty.size}</span>}
            <Button variant="filled" size="sm" onClick={handleSave} loading={saveMutation.isPending}
              icon={<Save size={12} />}>
              {saveMutation.isPending ? 'Сохранение…' : 'Сохранить всё'}
            </Button>
          </div>
        </div>

        {/* Постоянное состояние, а не всплывающее сообщение: пока библиотека не собирается, стоит
            генерация ВСЕХ документов, и больше этого нигде не видно. Зелёной строки на каждое
            сохранение нет — «всё хорошо» каждый раз это шум.

            Две полосы, а не одна (issue #506): сломанный файл, не подключённый к точке входа, сборке
            библиотеки не мешает, и говорить про него «генерация документов не пройдёт» — неправда.
            Но и молчать нельзя: шаблон вправе импортировать файл дерева напрямую, и один из наших
            так и делает. Typst останавливается на первом сломанном модуле, поэтому за одно
            сохранение виден один такой файл; починив его, следующее сохранение покажет следующий. */}
        {buildErrors.length > 0 && (
          <DiagnosticBlock tone="danger"
            title="Библиотека не собирается — генерация документов не пройдёт."
            items={buildErrors.map(e => ({ path: e.path, text: `${e.path}:${e.line} — ${e.message}` }))}
            alive={alive} onGo={setSelected} />
        )}
        {orphanErrors.length > 0 && (
          <DiagnosticBlock tone="warning"
            title="Файл не собирается. В сборку библиотеки он не входит, но шаблон, импортирующий его напрямую, не сгенерируется."
            items={orphanErrors.map(e => ({ path: e.path, text: `${e.path}:${e.line} — ${e.message}` }))}
            alive={alive} onGo={setSelected} />
        )}
        {/* Ошибки живут только в ответе на сохранение: проверка запускает Typst, и гонять его на
            каждое чтение (в том числе на каждый фокус окна) — плохой размен. Поэтому после
            перезагрузки мы про собираемость НЕ ЗНАЕМ — и говорим это прямо, а не показываем пустоту,
            которая читалась бы как «всё в порядке» (issue #500). */}
        {!checkApplies && (
          <div className="px-4 py-1.5 border-b border-stroke text-xs text-fg3 flex items-center gap-2">
            <AlertCircle size={12} className="shrink-0 text-fg4" />
            {check === null
              ? 'Собираемость библиотеки не проверялась в этом сеансе — сохраните, чтобы проверить.'
              : 'Библиотека изменилась после последней проверки — сохраните, чтобы проверить собираемость.'}
          </div>
        )}
        {/* Гасим замечания ошибками СБОРКИ, а не любыми (issue #507): пока библиотека не собирается,
            замечания и правда шум, но ошибка в неподключённом файле сборке не мешает — а такой файл
            это обычный ход работы (#494). Одна недописанная черновая строка прятала бы замечание о
            перекрытых именах, то есть о молчаливой поломке всех документов. */}
        {buildErrors.length === 0 && warnings.length > 0 && (
          <DiagnosticBlock tone="warning"
            items={warnings.map(w => ({ path: w.path, text: `${w.path} — ${w.message}` }))}
            alive={alive} onGo={setSelected} />
        )}

        <div className="flex-1 min-h-0 overflow-hidden">
          <Editor
            key={active}
            height="100%"
            defaultLanguage="typst"
            theme={resolvedTheme === 'dark' ? 'vs-dark' : 'vs'}
            value={currentContent}
            onChange={val => updateCurrent(val ?? '')}
            beforeMount={registerTypstLanguage}
            onMount={ed => { editorRef.current = ed; foldFunctions(); }}
            options={{
              minimap: { enabled: false },
              fontSize: 13,
              fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace",
              wordWrap: 'on',
              lineNumbers: 'on',
              scrollBeyondLastLine: false,
              automaticLayout: true,
              tabSize: 2,
              renderWhitespace: 'boundary',
            }}
          />
        </div>
      </div>

      {pathDialog && (
        <UserLibPathDialog
          mode={pathDialog.mode}
          initialPath={pathDialog.path}
          existing={files.map(f => f.path)}
          referencing={pathDialog.mode === 'rename' ? referencingFiles(files, pathDialog.path, entry) : []}
          onCancel={() => setPathDialog(null)}
          onSubmit={path => {
            if (pathDialog.mode === 'create') handleCreate(path);
            else handleRename(pathDialog.path, path);
            setPathDialog(null);
          }}
        />
      )}

      <ConfirmDialog
        open={deleting != null}
        onOpenChange={open => { if (!open) setDeleting(null); }}
        title={`Удалить «${deleting ?? ''}»?`}
        description={
          // Импорты приложение больше не правит (#492), поэтому обещать «строка подключения будет
          // убрана» нельзя — она останется и сломает сборку библиотеки, а с ней генерацию всех
          // документов. Говорим ровно то, что произойдёт.
          deleting && referencingFiles(files, deleting, entry).length > 0
            ? `На файл ссылаются: ${referencingFiles(files, deleting, entry).join(', ')}. `
              + 'Импорты останутся — уберите их вручную, иначе библиотека перестанет собираться, '
              + 'а с ней встанет генерация ВСЕХ документов.'
            : 'Файл будет удалён после сохранения. Импорты приложение не правит — если на файл '
              + 'где-то ссылаются, уберите ссылку сами.'
        }
        confirmLabel="Удалить"
        onConfirm={() => { if (deleting) handleDelete(deleting); }}
      />
    </div>
  );
}

/**
 * Полоса диагностики: заголовок (если есть) и до трёх строк со счётчиком остальных. Три полосы
 * устроены одинаково — ошибки сборки, ошибки неподключённых файлов и замечания (issue #506).
 */
function DiagnosticBlock({ tone, title, items, alive, onGo }: {
  tone: 'danger' | 'warning';
  title?: string;
  items: { path: string; text: string }[];
  alive: (path: string) => boolean;
  onGo: (path: string) => void;
}) {
  const Icon = tone === 'danger' ? AlertCircle : AlertTriangle;
  return (
    <div className={`px-4 py-2 border-b border-stroke flex items-start gap-2 ${
      tone === 'danger' ? 'bg-danger-subtle' : 'bg-warning-subtle'}`}>
      <Icon size={14} className={`shrink-0 mt-0.5 ${tone === 'danger' ? 'text-danger' : 'text-warning'}`} />
      <div className="min-w-0 text-xs">
        {title && (
          <p className={`font-medium ${tone === 'danger' ? 'text-danger' : 'text-warning'}`}>{title}</p>
        )}
        {items.slice(0, 3).map((it, i) => (
          <DiagnosticLine key={i} path={it.path} alive={alive(it.path)} text={it.text} onGo={onGo} />
        ))}
        {items.length > 3 && <p className="text-fg3">…и ещё {items.length - 3}</p>}
      </div>
    </div>
  );
}

/**
 * Строка диагностики. Ведёт в файл, только если он есть в локальном дереве: после локального
 * удаления или переименования переход открыл бы пустой редактор, и набранное молча пропадало бы
 * (issue #496). Саму запись при этом не прячем — она про сохранённое состояние (issue #498).
 */
function DiagnosticLine({ path, text, alive, onGo }: {
  path: string; text: string; alive: boolean; onGo: (path: string) => void;
}) {
  if (!alive) return <p className="text-fg3" title="Файла нет в текущем дереве — сохраните изменения">{text}</p>;
  return (
    <button type="button" onClick={() => onGo(path)}
      className="block text-left text-fg2 hover:text-fg1 hover:underline">
      {text}
    </button>
  );
}
