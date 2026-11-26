import { createRoot } from 'react-dom/client';
import { StoryProviders } from '../story-providers';
import { PublicationMockup } from './PublicationMockup';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';

const params = new URLSearchParams(location.search);
const theme = params.get('theme') === 'dark' ? 'dark' : 'light';
const page = params.get('page');
document.documentElement.dataset.theme = theme;
createRoot(document.getElementById('root')!).render(
  <StoryProviders theme={theme} component="Work27">
    <PublicationMockup variant={page === 'b' || page === 'c' ? page : 'a'} />
  </StoryProviders>,
);
