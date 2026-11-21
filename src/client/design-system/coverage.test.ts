import { describe, expect, it } from 'vitest';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readSourceInventory } from './source-inventory';
import { allStories } from './catalog-stories';
import { booleanStateProperties } from './story-boolean-states';
import { integrations } from './integrations';

const client = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const inventory = readSourceInventory(client);
const catalogStories = allStories({}, inventory);
const componentNames = [...new Set(catalogStories.map(s => s.component))];

describe('coverage of the live design system', () => {
  it('has a story or an explicit integration note for every visual export', () => {
    const missing = inventory.flatMap(f => f.exports).filter(e => e.visual && !componentNames.includes(e.name) && !integrations[e.name]);
    expect([...new Set(missing.map(e => `${e.file}: ${e.name}`))]).toEqual([]);
  });
  it('keeps stable unique links and usable case descriptions', () => {
    expect(new Set(catalogStories.map(s => s.id)).size).toBe(catalogStories.length);
    expect(catalogStories.every(s => s.id === `${s.component}::${s.variant}` && s.variant.includes('='))).toBe(true);
    expect(componentNames.every(name => catalogStories.some(s => s.component === name))).toBe(true);
  });
  it('represents every declared value of the visual variant axes', () => {
    const axes = new Set(['variant', 'density', 'precision', 'orientation', 'tone', 'size', 'side', 'align']);
    const missing: string[] = [];
    for (const entry of inventory.flatMap(f => f.exports).filter(e => e.visual && componentNames.includes(e.name))) {
      for (const property of entry.properties.filter(p => axes.has(p.name) && p.options?.length)) {
        const cases = catalogStories.filter(s => s.component === entry.name && (!s.renderAs || s.renderAs.endsWith('Example')));
        if (!cases.length) continue; // Context-bound parts are exercised by their owning component.
        for (const option of property.options!) if (!cases.some(s => s.props[property.name] === option)) missing.push(`${entry.name}.${property.name}=${option}`);
      }
    }
    expect([...new Set(missing)]).toEqual([]);
  });
  it('uses actual supported properties in direct component fixtures', () => {
    const exports = inventory.flatMap(f => f.exports);
    const unknown = new Set<string>();
    for (const story of catalogStories.filter(s => !s.renderAs)) {
      const entry = exports.find(e => e.name === story.component);
      if (!entry) continue;
      for (const key of Object.keys(story.props)) {
        if (!entry.propertyNames.includes(key) && !/^(aria-|data-)/.test(key) && story.props[key] !== undefined) unknown.add(`${story.component}.${key}`);
      }
    }
    expect([...unknown].sort()).toEqual([]);
  });
  it('represents the supported boolean visual states', () => {
    const missing: string[] = [];
    for (const entry of inventory.flatMap(f => f.exports).filter(e => e.visual && componentNames.includes(e.name))) {
      const cases = catalogStories.filter(s => s.component === entry.name && !s.renderAs);
      if (!cases.length) continue;
      for (const flag of booleanStateProperties(entry)) if (!cases.some(s => s.props[flag] === true)) missing.push(`${entry.name}.${flag}`);
    }
    expect([...new Set(missing)]).toEqual([]);
  });
});
