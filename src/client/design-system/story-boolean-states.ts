import type { SourceFile } from './source-inventory';
import { storyId, type Story } from './story-model';

export const visualBooleanFlags = new Set([
  'disabled', 'loading', 'readOnly', 'required', 'invalid', 'fromScan', 'selected', 'checked', 'indeterminate',
  'busy', 'isDirty', 'dirty', 'fullScreen', 'wide', 'dense', 'compact', 'multiline', 'dragged', 'dropTarget',
  'flush', 'focused', 'decorative', 'confirm', 'partial', 'more', 'pending', 'dimmed', 'fixed', 'back',
]);
export function booleanStateProperties(entry: SourceFile['exports'][number]) {
  const native = ['disabled', 'readOnly', 'required'].filter(name => entry.propertyNames.includes(name) && !entry.properties.some(p => p.name === name));
  return [...entry.properties.filter(p => visualBooleanFlags.has(p.name) && /^boolean(?: \| undefined)?$/.test(p.type)).map(p => p.name), ...native];
}
/** Each supported boolean visual axis gets a real, valid base composition. */
export function addBooleanStates(stories: Story[], inventory: SourceFile[]): Story[] {
  const added: Story[] = [];
  for (const component of new Set(stories.map(s => s.component))) {
    const entry = inventory.flatMap(f => f.exports).find(e => e.name === component);
    const cases = stories.filter(s => s.component === component && !s.renderAs);
    if (!entry || !cases.length) continue;
    const base = cases[0];
    for (const flag of booleanStateProperties(entry)) {
      const values = flag === 'selected' ? [false, true] : [true];
      for (const value of values) {
        if (cases.some(s => s.props[flag] === value)) continue;
        const variant = `Flag=${flag}, Value=${value}`;
        const props = { ...base.props, [flag]: value };
        if (component === 'Card') { delete props.onClick; delete props.to; }
        added.push({ ...base, props, variant, id: storyId(component, variant), state: undefined });
      }
    }
  }
  return [...stories, ...added];
}
