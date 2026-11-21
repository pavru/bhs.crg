import metadata from 'virtual:catalog-metadata';
import { allStories } from './catalog-stories';

export const catalogStories = allStories({}, metadata.inventory);
export const componentNames = [...new Set(catalogStories.map(s => s.component))].sort();
