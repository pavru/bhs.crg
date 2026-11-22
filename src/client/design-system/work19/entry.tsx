import { createRoot } from 'react-dom/client';
import { StoryProviders } from '../story-providers';
import { ClosingCanvas } from './ClosingCanvas';
import { Review } from './Review';
import { scenarios, variants } from './model';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';
import './work19.css';

const params = new URLSearchParams(location.search);
const theme = params.get('theme') === 'dark' ? 'dark' : 'light';
const variant = variants.find(item => item.id === params.get('variant')) ?? variants[0];
const scenario = scenarios.find(item => item.id === params.get('scenario')) ?? scenarios[0];

document.documentElement.dataset.theme = theme;
createRoot(document.getElementById('root')!).render(
  <StoryProviders theme={theme} component="Work19">
    {params.has('canvas') ? <ClosingCanvas variant={variant.id} scenario={scenario.id} /> : <Review params={params} />}
  </StoryProviders>,
);
