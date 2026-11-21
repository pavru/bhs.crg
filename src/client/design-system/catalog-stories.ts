import { createStories } from './stories';
import { extraStories } from './stories-extra';
import type { Registry } from './story-model';
import type { SourceFile } from './source-inventory';
import { addBooleanStates } from './story-boolean-states';

export function allStories(registry: Registry = {}, inventory: SourceFile[] = []) {
  const base = createStories(registry);
  const stories = [...new Map([...base, ...extraStories(registry, base)].map(story => [story.id, story])).values()];
  return addBooleanStates(stories, inventory);
}
