import { useId } from 'react';
import { catalogLabels, materialName, useCrewCandidates, useEntryCatalog,
  type EntryCatalogScope, type MaterialOption, type WorkTypeOption } from '@/shared/api/workEntryCatalog';
import type { RequiredReportField } from '@/shared/api/workReports';
import { Banner } from '@/shared/ui/Banner';
import { IconButton } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Delete } from '@/shared/ui/icons';
import { MaterialPicker } from '@/shared/ui/MaterialPicker';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { TextField } from '@/shared/ui/TextField';
import { WorkVariantPicker } from '@/shared/ui/WorkTypePicker';
import { apiError } from '@/shared/utils/apiError';
import type { DraftLine } from './reportContents';
import { missingForId, requiredFieldTitle } from './reportPresentation';

interface LineEditorProps {
  scope: EntryCatalogScope;
  line: DraftLine;
  siteReady: boolean;
  disabled: boolean;
  onChange: (changes: Partial<DraftLine>) => void;
}

export function ReportLineDetails({ scope, line, siteReady, type, disabled, errors, onChange, pickedMaterials, onPickMaterial }:
  LineEditorProps & { type?: WorkTypeOption; errors?: RequiredReportField[];
    pickedMaterials: Record<string, MaterialOption>; onPickMaterial: (material: MaterialOption) => void }) {
  const sections = useEntryCatalog(scope, 'sections', '', siteReady);
  const sectionItems = sections.data?.items ?? [];
  const missingSection = line.sectionId && !sectionItems.some(item => item.id === line.sectionId);
  const missing = missingForId(line, type);
  const locationError = !line.location?.trim() && errors?.some(item => item.field === 'location');
  return <div className="space-y-4">
    {missing.length > 0 && <StatusText tone="warning">
      Нужно для ИД · {missing.map(field => requiredFieldTitle[field].toLowerCase()).join(', ')}
    </StatusText>}
    <div className="grid gap-3 md:grid-cols-2">
      <WorkVariantPicker type={type} value={line.variantId} disabled={disabled} onChange={variantId => onChange({ variantId })} />
      <Select label="Раздел стройки" value={line.sectionId ?? undefined} placeholder="Не выбран" disabled={disabled || !siteReady}
        emptyItem="Без раздела" onValueChange={id => onChange({ sectionId: id || null })}>
        {missingSection && line.sectionId && <SelectItem value={line.sectionId} disabled>Раздел недоступен</SelectItem>}
        {sectionItems.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
      </Select>
      <div data-report-field="location" tabIndex={-1}>
        <TextField label="Место работ" value={line.location ?? ''} readOnly={disabled}
          error={locationError ? 'Укажите место работ для подачи отчёта.' : undefined}
          onChange={event => onChange({ location: event.target.value })} />
      </div>
      <TextField label="Комментарий" value={line.comment ?? ''} readOnly={disabled}
        onChange={event => onChange({ comment: event.target.value })} />
    </div>
    {sections.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void sections.refetch() }]}>
      {apiError(sections.error, 'Не удалось загрузить разделы стройки.')}
    </Banner>}
    <LineMaterials scope={scope} line={line} siteReady={siteReady} disabled={disabled} onChange={onChange}
      missing={!line.materials.length && !!errors?.some(item => item.field === 'materials')}
      picked={pickedMaterials} onPick={onPickMaterial} />
    <CrewFields scope={scope} line={line} siteReady={siteReady} disabled={disabled} onChange={onChange} />
  </div>;
}

function LineMaterials({ scope, line, siteReady, disabled, onChange, missing, picked, onPick }:
  LineEditorProps & { missing: boolean; picked: Record<string, MaterialOption>; onPick: (material: MaterialOption) => void }) {
  const description = useId();
  const catalog = useEntryCatalog(scope, 'materials', '', siteReady && line.materials.length > 0);
  const labels = catalogLabels(catalog.data);
  return <fieldset className="space-y-2">
    <legend className="text-label-large">Материалы</legend>
    {line.materials.map((item, index) => {
      const material = labels.get(item.nomenclatureId) ?? picked[item.nomenclatureId];
      const name = material ? materialName(material) : 'Материал недоступен';
      return <div key={item.nomenclatureId} className="flex flex-wrap items-center gap-2">
        <span className="min-w-36 flex-1 text-body-medium">{name}</span>
        <TextField label={`Примечание к материалу ${index + 1}`} value={item.note ?? ''} readOnly={disabled}
          onChange={event => onChange({ materials: line.materials.map(other => other === item ? { ...other, note: event.target.value } : other) })} />
        {!disabled && <IconButton icon={Delete} label={`Убрать материал ${index + 1}`}
          onClick={() => onChange({ materials: line.materials.filter(other => other !== item) })} />}
      </div>;
    })}
    {catalog.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void catalog.refetch() }]}>
      {apiError(catalog.error, 'Не удалось загрузить названия материалов.')}
    </Banner>}
    <div data-report-field="materials" tabIndex={-1} className="w-fit max-w-full">
      {missing && <p id={description} className="text-body-small text-error">Добавьте материал для подачи отчёта.</p>}
      {!disabled && siteReady && <MaterialPicker scope={scope} excluded={line.materials.map(item => item.nomenclatureId)}
        describedBy={missing ? description : undefined} onPick={material => {
          onPick(material);
          onChange({ materials: [...line.materials, { nomenclatureId: material.id, note: null }] });
        }} />}
    </div>
  </fieldset>;
}

function CrewFields({ scope, line, siteReady, disabled, onChange }: LineEditorProps) {
  const candidates = useCrewCandidates(scope, siteReady && line.isCrew);
  const items = candidates.data?.items ?? [];
  const choices = [...items, ...line.participants.filter(id => !items.some(person => person.id === id))
    .map(id => ({ id, fullName: 'Участник недоступен' }))];
  return <div className="space-y-3">
    <Checkbox label="Бригадная работа" checked={line.isCrew} disabled={disabled}
      onChange={isCrew => onChange(isCrew ? { isCrew } : { isCrew, participants: [], crewRole: null })} />
    {line.isCrew && <>
      <div className="max-w-sm">
        <Select label="Роль в бригаде" value={line.crewRole ?? undefined} placeholder="Не указана" disabled={disabled}
          emptyItem="Без роли" onValueChange={crewRole => onChange({ crewRole: crewRole || null })}>
          <SelectItem value="lead">Бригадир</SelectItem>
          <SelectItem value="member">Участник</SelectItem>
        </Select>
      </div>
      <fieldset className="flex flex-wrap gap-x-4 gap-y-1">
        <legend className="text-label-large">Участники бригады</legend>
        {choices.map(person => <Checkbox key={person.id} label={person.fullName} disabled={disabled}
          checked={line.participants.includes(person.id)} onChange={checked => onChange({
            participants: checked ? [...line.participants, person.id] : line.participants.filter(id => id !== person.id),
          })} />)}
      </fieldset>
      {candidates.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void candidates.refetch() }]}>
        {apiError(candidates.error, 'Не удалось загрузить участников бригады.')}
      </Banner> : (candidates.isPending && siteReady || candidates.data?.reason || !items.length) &&
        <p role="status" className="text-body-small text-on-surface-variant">{!siteReady ? 'Укажите стройку и дату смены.'
          : candidates.isPending ? 'Загрузка участников…' : candidates.data?.reason
            ? 'Участники недоступны по вашим назначениям.' : 'На эту дату нет назначенных участников.'}</p>}
    </>}
  </div>;
}
