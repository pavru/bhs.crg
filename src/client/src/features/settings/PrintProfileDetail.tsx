import { useState, type ReactNode } from 'react';
import { Info, RectangleHorizontal, RectangleVertical, RotateCcw, Trash2 } from 'lucide-react';
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
  useDeletePrintProfile, useResetPrintProfile, useUpdatePrintProfile, type PrintProfile,
} from '@/shared/api/printProfiles';
import {
  FITS, MARGIN_SIDES, ORIENTATIONS, PAPERS, fromDraft, isModified, marginMm, sameDraft, toDraft, validateDraft,
  type PrintProfileDraft,
} from './printProfileRules';
import { SheetSketch } from './SheetSketch';

/** Ошибки под полями относятся к ТОМУ профилю, при котором случились: открыли другой — сняты. */
const noIssues = (): Record<string, string> => ({});

/**
 * Карточка профиля печати (issue #1455). Выбор бумаги, ориентации и вписывания — чипами: вариантов
 * два-три, и все видны сразу. Поля стоят вокруг наброска листа — каждое у своей стороны, и набросок
 * меняется вместе с ними.
 *
 * <p>Проверка — на сохранении, «Сохранить» не выключается: ошибка стоит под полем, а не прячет
 * причину за серой кнопкой. Отказ сервера с адресом поля ложится туда же.</p>
 */
export function PrintProfileDetail({ profile, otherNames, onDeleted }: {
  profile: PrintProfile;
  /** Названия остальных профилей — для «такое название уже есть» до отправки. */
  otherNames: string[];
  onDeleted: () => void;
}) {
  const toast = useToast();
  const update = useUpdatePrintProfile();
  const reset = useResetPrintProfile();
  const del = useDeletePrintProfile();
  const [draft, setDraft] = useServerForm(profile, toDraft);
  const [issues, setIssues] = useServerForm(profile, noIssues);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);

  const dirty = !sameDraft(draft, toDraft(profile));
  const modified = isModified(profile);

  /** Правка поля снимает его ошибку: она говорила о прежнем значении. */
  function edit(patch: Partial<PrintProfileDraft>, paths: string[]) {
    setDraft(d => ({ ...d, ...patch }));
    if (paths.some(p => issues[p])) setIssues(prev => Object.fromEntries(Object.entries(prev).filter(([k]) => !paths.includes(k))));
  }

  async function save() {
    const found = validateDraft(draft, otherNames);
    setIssues(found);
    if (Object.keys(found).length > 0) throw new Error('Профиль не прошёл проверку');
    try {
      await update.mutateAsync({ id: profile.id, ...fromDraft(draft) });
      toast.success('Профиль сохранён');
    } catch (e) {
      const byPath = refusalIssues(e);
      if (Object.keys(byPath).length > 0) setIssues(byPath);
      else toast.apiError(e, 'Профиль не сохранён');
      throw e;
    }
  }

  const marginIssues = [...new Set(MARGIN_SIDES.map(s => issues[`margins.${s.key}`]).filter(Boolean))];

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
        onRevert={() => { setDraft(toDraft(profile)); setIssues({}); }}
        actions={
          <>
            {/* Сброс — только у правленого профиля по умолчанию; у своего заводских параметров нет.
                Удалить профиль по умолчанию нельзя — кнопки у него нет вовсе. */}
            {modified && (
              <Button variant="text" size="sm" icon={<RotateCcw size={14} />} onClick={() => setConfirmReset(true)}>
                Сбросить к заводским
              </Button>
            )}
            {!profile.isSystem && (
              <Button variant="text" size="sm" icon={<Trash2 size={14} />} onClick={() => setConfirmDelete(true)}>
                Удалить
              </Button>
            )}
          </>
        } />

      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5" data-testid="print-profile-detail">
        <p className="text-xs text-fg3">
          {profile.isSystem
            ? 'Профиль по умолчанию: по нему строится вид каждой книги без своей раскладки. Удалить его нельзя, править — можно.'
            : 'Применяется, когда его выбрали при «Перестроить» у файла счёта.'}
        </p>
        <WhereApplied />

        <div className="max-w-xl space-y-5">
          <TextField label="Название" value={draft.name} maxLength={80} readOnly={profile.isSystem}
            hint={profile.isSystem ? 'Название профиля по умолчанию не меняется' : undefined}
            error={issues.name} onChange={e => edit({ name: e.target.value }, ['name'])} />

          <ChoiceRow label="Бумага" error={issues.paper}>
            {PAPERS.map(p => (
              <Chip key={p.value} selected={draft.paper === p.value} onClick={() => edit({ paper: p.value }, ['paper'])}>
                {p.value} <span className="text-fg4">{p.shortMm} × {p.longMm}</span>
              </Chip>
            ))}
          </ChoiceRow>

          <ChoiceRow label="Ориентация" error={issues.orientation}>
            {ORIENTATIONS.map(o => (
              <Chip key={o.value} selected={draft.orientation === o.value}
                icon={o.value === 'portrait' ? RectangleVertical : RectangleHorizontal}
                onClick={() => edit({ orientation: o.value }, ['orientation'])}>
                {o.label}
              </Chip>
            ))}
          </ChoiceRow>

          <ChoiceRow label="Вписать в страницу" error={issues.fit}
            hint={FITS.find(f => f.value === draft.fit)?.hint}>
            {FITS.map(f => (
              <Chip key={f.value} selected={draft.fit === f.value} onClick={() => edit({ fit: f.value }, ['fit'])}>
                {f.label}
              </Chip>
            ))}
          </ChoiceRow>

          <MarginsAround draft={draft} issues={issues}
            onChange={(side, value) => edit({ margins: { ...draft.margins, [side]: value } },
              // Нехватка места стоит под правым или нижним полем, а меняют её любым из пары.
              side === 'left' || side === 'right'
                ? ['margins.left', 'margins.right'] : ['margins.top', 'margins.bottom'])} />
          {marginIssues.length > 0 && (
            <ul role="alert" className="space-y-0.5 text-xs text-danger">
              {marginIssues.map(text => <li key={text}>{text}</li>)}
            </ul>
          )}
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
    <div className="flex gap-2 rounded-lg bg-muted p-3 text-xs text-fg3">
      <Info size={14} className="shrink-0 mt-0.5 text-fg2" />
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

/** Подпись и ряд чипов одного выбора; пояснение и ошибка — под рядом. */
function ChoiceRow({ label, hint, error, children }: {
  label: string; hint?: string; error?: string; children: ReactNode;
}) {
  return (
    <div role="group" aria-label={label}>
      <div className="text-xs text-fg2 mb-1.5">{label}</div>
      <div className="flex flex-wrap gap-2">{children}</div>
      {error
        ? <p className="mt-1 text-xs text-danger">{error}</p>
        : hint && <p className="mt-1 text-xs text-fg3">{hint}</p>}
    </div>
  );
}

/** Четыре поля у своих сторон наброска: верхнее над листом, нижнее под ним, левое и правое по бокам. */
function MarginsAround({ draft, issues, onChange }: {
  draft: PrintProfileDraft;
  issues: Record<string, string>;
  onChange: (side: keyof PrintProfileDraft['margins'], value: string) => void;
}) {
  const field = (side: keyof PrintProfileDraft['margins']) => {
    const meta = MARGIN_SIDES.find(s => s.key === side)!;
    return (
      <TextField label={meta.label} value={draft.margins[side]} inputMode="numeric" containerClassName="w-24"
        trailing={<span className="text-xs text-fg4 pr-3">мм</span>}
        invalid={!!issues[`margins.${side}`]}
        onChange={e => onChange(side, e.target.value)} />
    );
  };
  const shortfall = !!(issues['margins.right'] || issues['margins.bottom'] || issues['margins.left'] || issues['margins.top']);

  return (
    <div role="group" aria-label="Поля, мм">
      <div className="text-xs text-fg2 mb-2">Поля, мм</div>
      <div className="grid grid-cols-3 items-center justify-items-center gap-3 w-fit">
        <span />{field('top')}<span />
        {field('left')}
        <SheetSketch size="lg" paper={draft.paper} orientation={draft.orientation} fit={draft.fit}
          invalid={shortfall}
          margins={{
            top: marginMm(draft.margins.top), bottom: marginMm(draft.margins.bottom),
            left: marginMm(draft.margins.left), right: marginMm(draft.margins.right),
          }} />
        {field('right')}
        <span />{field('bottom')}<span />
      </div>
    </div>
  );
}
