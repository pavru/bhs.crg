import { ArrayFieldEditor, ComplexFieldGroup } from '@/features/document-sets/fields/ComplexFields';
import { DocRefCatalogPickerField } from '@/features/document-sets/fields/DocRefCatalogPickerField';
import { DocArrayField } from '@/features/document-sets/fields/DocRefField';
import { FieldTypeRegistry } from '@/features/document-sets/fields/fieldTypeRegistry';
import { FileField } from '@/features/document-sets/fields/FileField';
import { ImageField } from '@/features/document-sets/fields/ImageField';
import { isLockedField } from '@/features/document-sets/fields/lockedFields';
import { PrimitiveInput } from '@/features/document-sets/fields/PrimitiveInput';
import { ReferenceCatalogContext } from '@/features/document-sets/fields/referenceCatalog';
import { apiClient } from '@/shared/api/client';
import type { SchemaField } from '@/shared/api/schema';
import type { EntryCatalogScope } from '@/shared/api/workEntryCatalog';
import { useReportDetailsSchema } from '@/shared/api/workReports';
import type { MissingAcceptanceField } from '@/shared/api/workReview';
import { Banner } from '@/shared/ui/Banner';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { detailValues } from './reportDetails';

type DetailsSchema = NonNullable<ReturnType<typeof useReportDetailsSchema>['data']>;

export function ReportCustomerFields({ details, missingFields, disabled, scope, onChange, title = 'Поля заказчика' }: {
  details: unknown;
  missingFields?: MissingAcceptanceField[];
  disabled: boolean;
  scope: EntryCatalogScope;
  onChange: (key: string, value: unknown) => void;
  title?: string;
}) {
  const schema = useReportDetailsSchema();
  if (schema.isError && !schema.data) return <Banner tone="error"
    actions={[{ label: 'Повторить', onClick: () => void schema.refetch() }]}>
    {apiError(schema.error, 'Не удалось прочитать поля заказчика.')}
  </Banner>;
  if (!schema.data?.fields.length) return null;

  const data = schema.data;
  const values = detailValues(details);
  const missing = new Set(missingFields?.map(field => field.key));
  const { siteId, date, reportId, reportVersion } = scope;
  return <FieldTypeRegistry.Provider value={data}><ReferenceCatalogContext.Provider value={{
    key: ['work', 'field-references', siteId, date, reportId, reportVersion],
    load: (typeId, union, archived, q, signal) => apiClient.get(
      `/work/entry/sites/${encodeURIComponent(siteId)}/catalog/field-references`,
      { params: { date, reportId, typeId, union, archived, q }, signal },
    ).then(response => response.data),
  }}>
    <fieldset className="space-y-2" disabled={disabled}>
      <legend className="text-label-large">{title}</legend>
      <div className="grid items-start gap-3 md:grid-cols-2">
        {data.fields.map(field => <CustomerField key={field.key} field={field} data={data}
          value={values[field.key]} missing={missing.has(field.key)} disabled={disabled} siteId={siteId}
          onChange={value => onChange(field.key, value)} />)}
      </div>
    </fieldset>
  </ReferenceCatalogContext.Provider></FieldTypeRegistry.Provider>;
}

function CustomerField({ field, data, value, missing, disabled, siteId, onChange }: {
  field: SchemaField; data: DetailsSchema; value: unknown; missing: boolean; disabled: boolean; siteId: string;
  onChange: (value: unknown) => void;
}) {
  const label = field.title || field.key;
  const props = { field, value, onChange, allDocTypes: data.types };
  const scoped = { scope: 'Construction' as const, scopeId: siteId };
  const hint = missing ? 'Нужно к приёмке' : undefined;
  const container = ['array', 'complex', 'file', 'image', 'doc-ref', 'doc-array'].includes(field.type);
  const readOnly = disabled || isLockedField(field);

  if (field.type === 'boolean') {
    return <Select label={label} hint={hint} placeholder="Не указано" emptyItem="Не указано"
      value={typeof value === 'boolean' ? String(value) : undefined} disabled={readOnly}
      onValueChange={answer => onChange(answer === '' ? null : answer === 'true')}>
      <SelectItem value="true">Да</SelectItem>
      <SelectItem value="false">Нет</SelectItem>
    </Select>;
  }

  if (!container) {
    return <PrimitiveInput field={field} value={value} label={label} onChange={onChange} readOnly={readOnly}
      hint={hint} invalid={false} primitiveTypeDef={data.primitiveTypes.find(type => type.id === field.typeId)}
      enumTypeDef={data.enumTypes.find(type => type.id === field.typeId)} />;
  }

  let control;
  switch (field.type) {
    case 'image': control = <ImageField value={value} onChange={onChange} readOnly={readOnly} />; break;
    case 'file': control = <FileField value={value} onChange={onChange} readOnly={readOnly} />; break;
    case 'complex': control = !readOnly && <ComplexFieldGroup {...props} {...scoped} docRefMode="work" showValidation={false} />; break;
    case 'array': control = !readOnly && <ArrayFieldEditor {...props} {...scoped} docRefMode="work" showValidation={false} />; break;
    case 'doc-ref': control = !readOnly && <DocRefCatalogPickerField {...props} {...scoped} />; break;
    case 'doc-array': control = !readOnly && <DocArrayField {...props} otherInstances={[]} />; break;
  }
  return <div className="space-y-1">
    <p className="text-body-medium">{label}</p>
    {control || <p className="text-body-small text-on-surface-variant">{value == null ? '—' : JSON.stringify(value)}</p>}
    {hint && <StatusText tone="warning">{hint}</StatusText>}
  </div>;
}
