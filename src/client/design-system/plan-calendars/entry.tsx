import { createRoot } from 'react-dom/client';
import { StoryProviders } from '../story-providers';
import { PlanCalendars } from './PlanCalendars';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';
import './calendar.css';

const params = new URLSearchParams(location.search);
const theme = params.get('theme') === 'dark' ? 'dark' : 'light';
document.documentElement.dataset.theme = theme;
createRoot(document.getElementById('root')!).render(
  <StoryProviders theme={theme} component="PlanCalendars"><PlanCalendars params={params} /></StoryProviders>,
);
