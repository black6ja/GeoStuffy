import './style.css';
import { Game } from './game/core/Game';
import { abilityCatalog } from './game/data/abilities';
import { classCatalog } from './game/data/classes';
import { lootTables } from './game/data/loot';

const app = document.querySelector<HTMLDivElement>('#app');
if (!app) {
  throw new Error('App container missing');
}

const canvasWrapper = document.createElement('div');
canvasWrapper.className = 'canvas-wrapper';

const canvas = document.createElement('canvas');
canvas.width = window.innerWidth;
canvas.height = window.innerHeight * 0.7;

const hud = document.createElement('div');
hud.className = 'hud';
const title = document.createElement('h1');
title.textContent = 'Shards of the Everwild';
const status = document.createElement('p');
status.textContent = 'WASD to move • Mouse to aim • 1-4 to cast abilities';
hud.appendChild(title);
hud.appendChild(status);

canvasWrapper.appendChild(canvas);
canvasWrapper.appendChild(hud);
app.appendChild(canvasWrapper);
app.appendChild(createMetaPanel());

const game = new Game(canvas, {
  abilityCatalog,
  classCatalog,
  lootTables
});

game.bootstrap();

window.addEventListener('resize', () => {
  canvas.width = window.innerWidth;
  canvas.height = window.innerHeight * 0.7;
});

function createMetaPanel(): HTMLElement {
  const panel = document.createElement('div');
  panel.className = 'panel';

  const abilitySection = document.createElement('section');
  const abilityTitle = document.createElement('h2');
  abilityTitle.textContent = 'Prototype Ability Grid';
  const abilityList = document.createElement('ul');
  abilityCatalog.forEach((ability) => {
    const item = document.createElement('li');
    const badge = document.createElement('span');
    badge.className = 'badge';
    badge.dataset.tier = ability.tier;
    badge.textContent = ability.tier.toUpperCase();
    item.textContent = `${ability.name} — ${ability.summary}`;
    item.prepend(badge);
    abilityList.appendChild(item);
  });
  abilitySection.appendChild(abilityTitle);
  abilitySection.appendChild(abilityList);

  const classSection = document.createElement('section');
  const classTitle = document.createElement('h2');
  classTitle.textContent = 'Classes & Archetypes';
  const classList = document.createElement('ul');
  classCatalog.forEach((klass) => {
    const item = document.createElement('li');
    item.innerHTML = `<strong>${klass.name}</strong> — ${klass.fantasy}. Signature: ${klass.signatureAbilities
      .map((id) => abilityCatalog.find((a) => a.id === id)?.name || id)
      .join(', ')}`;
    classList.appendChild(item);
  });
  classSection.appendChild(classTitle);
  classSection.appendChild(classList);

  const controls = document.createElement('section');
  const controlsTitle = document.createElement('h2');
  controlsTitle.textContent = 'Controls & Systems';
  const controlsList = document.createElement('ul');
  controlsList.innerHTML = `
    <li><kbd>WASD</kbd> to move, hold <kbd>Shift</kbd> for sprint.</li>
    <li><kbd>Mouse</kbd> aims abilities. <kbd>1-4</kbd> casts spells based on loadout.</li>
    <li>Procedural events spawn camps, traders, and world bosses over time.</li>
    <li>Data-driven config files make it easy to expand items, spells, and encounters.</li>
  `;
  controls.appendChild(controlsTitle);
  controls.appendChild(controlsList);

  panel.appendChild(abilitySection);
  panel.appendChild(classSection);
  panel.appendChild(controls);
  return panel;
}
