import { materialName, type EntryCatalogScope, type MaterialOption } from '@/shared/api/workEntryCatalog';
import { WorkCatalogPicker } from './WorkCatalogPicker';

export function MaterialPicker({ scope, excluded, describedBy, onPick }: {
  scope: EntryCatalogScope;
  excluded: readonly string[];
  describedBy?: string;
  onPick: (material: MaterialOption) => void;
}) {
  return <WorkCatalogPicker scope={scope} kind="materials" title="Материалы · Справочник" label="Добавить материал"
    display="Добавить из справочника" describedBy={describedBy} excluded={excluded} name={materialName} onPick={onPick} />;
}
