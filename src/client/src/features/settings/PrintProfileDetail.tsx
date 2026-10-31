import { useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent, type MutableRefObject } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CropLandscape, CropPortrait, Delete, Info, RestartAlt, type IconDef } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { DetailHeader, DetailTitle } from '@/shared/ui/ListDetailShell';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { useServerForm } from '@/shared/hooks/useServerForm';
import { refusalIssues } from '@/shared/api/refusals';
import {
  useDeletePrintProfile, useResetPrintProfile, useUpdatePrintProfile, type PrintMargins, type PrintProfile,
} from '@/shared/api/printProfiles';
import {
  FITS, MARGIN_PAIRS, MARGIN_SIDES, NAME_MAX, ORIENTATIONS, PAPERS, fieldIssue, fromDraft, isModified, marginMm,
  pairIssue, sameDraft, toDraft, validateDraft, type PrintProfileDraft,
} from './printProfileRules';
import { SheetSketch } from './SheetSketch';

/** Ошибки и попытка сохранить относятся к ТОМУ профилю, при котором случились: открыли другой — сняты. */
const noIssues = (): Record<string, string> => ({});
const notTried = () => false;

/** Пути, которые проверяются по размеру страницы: смена бумаги или ориентации меняет их ответ. */
const MARGIN_PATHS = MARGIN_SIDES.map(s => `margins.${s.key}`);

/** Что карточка сообщает странице: есть ли несохранённое и идёт ли сохранение — для вопроса при уходе. */
export interface EditState { dirty: boolean; saving: boolean }

/**
 * Карточка профиля печати (issue #1455). Выбор бумаги, ориентации и вписывания — чипами-радио: вариантов
 * два-три, и все видны сразу. Поля стоят вокруг наброска листа — каждое у своей стороны, и набросок
 * меняется вместе с ними.
 *
 * <p>Проверка — на сохранении, «Сохранить» не выключается: ошибка стоит под полем, а не прячет
 * причину за серой кнопкой. После первой попытки проверка идёт на каждой правке — ошибка уходит, как
 * только её исправили, и не остаётся устаревшей (поменяли бумагу — «остаётся N мм» пересчитано). Отказ
 * сервера с адресом поля ложится туда же и снимается правкой этого поля.</p>
 *
 * <p>Несохранённое карточка не теряет молча: о нём знает страница (`onEditState`), и уйти к другому
 * профилю или с экрана можно только через вопрос; «Сохранить и перейти» зовёт `saveRef`.</p>
 */
export function PrintProfileDetail({ profile, otherNames, onDeleted, onEditState, saveRef }: {
  profile: PrintProfile;
  /** Названия остальных профилей — для «такое название уже есть» до отправки. */
  otherNames: string[];
  onDeleted: () => void;
  onEditState: (state: EditState) => void;
  saveRef: MutableRefObject<() => Promise<void>>;
}) {
  const toast = useToast();
  const update = useUpdatePrintProfile();
  const reset = useResetPrintProfile();
  const del = useDeletePrintProfile();
  const [draft, setDraft] = useServerForm(profile, toDraft);
  const [serverIssues, setServerIssues] = useServerForm(profile, noIssues);
  const [tried, setTried] = useServerForm(profile, notTried);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);

  const dirty = !sameDraft(draft, toDraft(profile));
  const modified = isModified(profile);
  const issues = { ...serverIssues, ...(tried ? validateDraft(draft, otherNames) : {}) };

  /** Правка снимает отказ сервера о том, что правят: он говорил о прежнем значении. */
  function edit(patch: Partial<PrintProfileDraft>, paths: string[]) {
    setDraft(d => ({ ...d, ...patch }));
    if (paths.some(p => serverIssues[p]))
      setServerIssues(prev => Object.fromEntries(Object.entries(prev).filter(([k]) => !paths.includes(k))));
  }

  async function save() {
    setTried(true);
    if (Object.keys(validateDraft(draft, otherNames)).length > 0) throw new Error('Профиль не прошёл проверку');
    try {
      await update.mutateAsync({ id: profile.id, ...fromDraft(draft) });
      toast.success('Профиль сохранён');
    } catch (e) {
      const byPath = refusalIssues(e);
      if (Object.keys(byPath).length > 0) setServerIssues(byPath);
      else toast.apiError(e, 'Профиль не сохранён');
      throw e;
    }
  }

  // Слойный эффект, а не присваивание в рендере: «Сохранить и перейти» берёт сохранение с текущим черновиком.
  useLayoutEffect(() => { saveRef.current = save; });
  useEffect(() => { onEditState({ dirty, saving: update.isPending }); }, [dirty, update.isPending, onEditState]);
  // Удалённый или сменённый профиль уносит свою правку с собой — странице незачем спрашивать о ней.
  useEffect(() => () => onEditState({ dirty: false, saving: false }), [onEditState]);

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <DetailHeader
        heading={
          <span className="flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0">
            <DetailTitle>{profile.name}</DetailTitle>
            {/* Пометки «по умолчанию» нет: системный профиль так и называется, и пометка повторяла бы имя. */}
            {modified && <StatusBadge>изменён</StatusBadge>}
          </span>
        }
        dirty={dirty} saving={update.isPending} onSaveAll={save}
        onRevert={() => { setDraft(toDraft(profile)); setServerIssues({}); setTried(false); }}
        actions={
          <>
            {/* Сброс — только у правленого профиля по умолчанию; у своего заводских параметров нет.
                Удалить профиль по умолчанию нельзя — кнопки у него нет вовсе. */}
            {modified && (
              <Button variant="text" icon={<Icon icon={RestartAlt} size={18} />} onClick={() => setConfirmReset(true)}>
                Сбросить к заводским
              </Button>
            )}
            {!profile.isSystem && (
              <Button variant="text" icon={<Icon icon={Delete} size={18} />} onClick={() => setConfirmDelete(true)}>
                Удалить
              </Button>
            )}
          </>
        } />

      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5" data-testid="print-profile-detail">
        <p className="text-body-small text-on-surface-variant">
          {profile.isSystem
            ? 'Профиль по умолчанию: по нему строится вид каждой книги без своей раскладки. Удалить его нельзя, править — можно.'
            : 'Применяется, когда его выбрали при «Перестроить» у файла счёта.'}
        </p>
        <WhereApplied />

        <div className="max-w-xl space-y-5">
          <TextField label="Название" value={draft.name} maxLength={NAME_MAX} readOnly={profile.isSystem}
            hint={profile.isSystem ? 'Название профиля по умолчанию не меняется' : undefined}
            error={issues.name} onChange={e => edit({ name: e.target.value }, ['name'])} />

          <ChoiceRow label="Бумага" error={issues.paper} value={draft.paper}
            options={PAPERS.map(p => ({
              value: p.value, label: <>{p.value} <span className="text-on-surface-variant">{p.shortMm} × {p.longMm}</span></>,
            }))}
            onChange={paper => edit({ paper }, ['paper', ...MARGIN_PATHS])} />

          <ChoiceRow label="Ориентация" error={issues.orientation} value={draft.orientation}
            options={ORIENTATIONS.map(o => ({
              value: o.value, label: o.label, icon: o.value === 'portrait' ? CropPortrait : CropLandscape,
            }))}
            onChange={orientation => edit({ orientation }, ['orientation', ...MARGIN_PATHS])} />

          <ChoiceRow label="Вписать в страницу" error={issues.fit} value={draft.fit}
            hint={FITS.find(f => f.value === draft.fit)?.hint}
            options={FITS.map(f => ({ value: f.value, label: f.label }))}
            onChange={fit => edit({ fit }, ['fit'])} />

          <MarginsAround draft={draft} issues={issues}
            // Ошибка пары правится любой из её сторон — правка одной снимает отказ сервера у обеих.
            onChange={(side, value) => edit({ margins: { ...draft.margins, [side]: value } },
              MARGIN_PAIRS.find(p => p.includes(side))!.map(s => `margins.${s}`))} />
        </div>
      </div>

      <ConfirmDialog
        open={confirmDelete} onOpenChange={setConfirmDelete}
        title="Удалить профиль?"
        description={`Профиль «${profile.name}» будет удалён. Построенные по нему виды останутся как есть; `
          + 'при следующем «Перестроить» они пойдут по профилю по умолчанию.'}
        confirmLabel="Удалить"
        onConfirm={async () => {
          await del.mutateAsync({ id: profile.id });
          toast.success('Профиль удалён');
          onDeleted();
        }} />

      <ConfirmDialog
        open={confirmReset} onOpenChange={setConfirmReset}
        title="Сбросить к заводским?"
        description="Ваши правки профиля по умолчанию будут заменены заводскими параметрами. Уже построенные виды не изменятся."
        confirmLabel="Сбросить" confirmDanger={false}
        onConfirm={async () => {
          await reset.mutateAsync({ id: profile.id });
          toast.success('Профиль сброшен к заводским');
        }} />
    </div>
  );
}

/**
 * Где профиль действует. Последняя фраза обязательна: администратор поправит поля, откроет счёт и
 * увидит прежний вид — построенные виды профиль сам не меняет.
 */
function WhereApplied() {
  return (
    <div className="flex gap-2 rounded-md bg-surface-container p-3 text-body-small text-on-surface-variant">
      <Icon icon={Info} size={18} className="shrink-0 mt-0.5 text-on-surface-variant" />
      <div className="space-y-1">
        <p>
          Профиль действует на книги Excel (.xlsx), где автор не задал вписывание или масштаб. Бумагу и
          ориентацию, записанные в книге, профиль по умолчанию сохраняет, свой профиль ставит свои. Файлы .xls
          и Word профиль пока не меняет.
        </p>
        <p>Уже построенные виды не меняются — их перестраивают у счёта кнопкой «Перестроить».</p>
      </div>
    </div>
  );
}

/**
 * Один выбор из двух-трёх — радиогруппа чипов по APG: одна остановка Tab, стрелки переводят выбор и
 * фокус. Чипы с `aria-pressed` диктор назвал бы независимыми переключателями, а не «одним из трёх».
 */
function ChoiceRow<T extends string>({ label, hint, error, value, options, onChange }: {
  label: string; hint?: string; error?: string; value: T;
  options: { value: T; label: React.ReactNode; icon?: IconDef }[];
  onChange: (value: T) => void;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const labelId = useId();
  const onKeyDown = (e: KeyboardEvent) => {
    const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[e.key];
    if (!step) return;
    e.preventDefault();
    const at = options.findIndex(o => o.value === value);
    const next = (at + step + options.length) % options.length;
    onChange(options[next].value);
    refs.current[next]?.focus();
  };
  return (
    <div>
      <div id={labelId} className="text-body-small text-on-surface-variant mb-1.5">{label}</div>
      <div role="radiogroup" aria-labelledby={labelId} onKeyDown={onKeyDown} className="flex flex-wrap gap-2">
        {options.map((o, i) => (
          <Chip key={o.value} ref={el => { refs.current[i] = el; }} role="radio" selected={o.value === value}
            tabIndex={o.value === value ? 0 : -1} icon={o.icon}
            onClick={() => { if (o.value !== value) onChange(o.value); }}>
            {o.label}
          </Chip>
        ))}
      </div>
      {error
        ? <p className="mt-1 text-body-small text-error">{error}</p>
        : hint && <p className="mt-1 text-body-small text-on-surface-variant">{hint}</p>}
    </div>
  );
}

/**
 * Четыре поля у своих сторон наброска: верхнее над листом, нижнее под ним, левое и правое по бокам.
 * Ошибка самого поля — под ним; нехватка места — ошибка пары, она под наброском и связана с обоими
 * полями пары (`aria-describedby`): диктор на поле прочтёт, что не так.
 */
function MarginsAround({ draft, issues, onChange }: {
  draft: PrintProfileDraft;
  issues: Record<string, string>;
  onChange: (side: keyof PrintMargins, value: string) => void;
}) {
  const pairsId = useId();
  const pairs = MARGIN_PAIRS.map(pair => ({ pair, text: pairIssue(issues, pair) }));
  const pairOf = (side: keyof PrintMargins) => pairs.find(p => p.pair.includes(side))!;

  const field = (side: keyof PrintMargins) => {
    const meta = MARGIN_SIDES.find(s => s.key === side)!;
    const own = fieldIssue(issues, side);
    const ofPair = pairOf(side).text;
    return (
      <TextField label={meta.label} value={draft.margins[side]} inputMode="numeric" containerClassName="w-32"
        trailing={<span className="text-body-small text-on-surface-variant pr-3">мм</span>}
        error={own} invalid={!!ofPair} aria-invalid={!!(own || ofPair) || undefined}
        aria-describedby={ofPair ? `${pairsId}-${side}` : undefined}
        onChange={e => onChange(side, e.target.value)} />
    );
  };

  return (
    <div role="group" aria-label="Поля, мм">
      <div className="text-body-small text-on-surface-variant mb-2">Поля, мм</div>
      <div className="grid grid-cols-3 items-center justify-items-center gap-3 w-fit">
        <span />{field('top')}<span />
        {field('left')}
        <SheetSketch size="lg" paper={draft.paper} orientation={draft.orientation} fit={draft.fit}
          invalid={pairs.some(p => p.text)}
          margins={{
            top: marginMm(draft.margins.top), bottom: marginMm(draft.margins.bottom),
            left: marginMm(draft.margins.left), right: marginMm(draft.margins.right),
          }} />
        {field('right')}
        <span />{field('bottom')}<span />
      </div>
      {pairs.some(p => p.text) && (
        <ul role="alert" className="mt-2 space-y-0.5 text-body-small text-error">
          {pairs.filter(p => p.text).map(p => (
            // У каждой стороны пары — свой id на ту же строку: aria-describedby ссылается по одному.
            <li key={p.pair.join()} id={`${pairsId}-${p.pair[0]}`}>
              <span id={`${pairsId}-${p.pair[1]}`}>{p.text}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
