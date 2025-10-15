# Shards of the Everwild Prototype

A browser-based action roguelite sandbox intended to scale toward an MMO-style open world. The current build focuses on core scaffolding: entity management, modular spell definitions, procedural world regions, and prototype AI behaviours.

## Getting Started

1. Install dependencies (requires Node 18+):

   ```bash
   npm install
   ```

   > If npm registry access is restricted you can still explore the code; the project relies solely on dev-time tooling (`vite`, `typescript`).

2. Start the development server:

   ```bash
   npm run dev
   ```

3. Open the provided local URL to interact with the prototype arena. Use **WASD** to move, **Shift** to sprint, and number keys to cast abilities.

## Architecture Highlights

- **Data-driven content** – Spells, classes, and loot tables are defined in TypeScript objects (`src/game/data`). Adding new content is as simple as extending those arrays.
- **Lightweight ECS-inspired runtime** – Entities contain composable components, updated by dedicated systems (`src/game/systems`).
- **World generator** – The `World` class produces looping regions with distinct biomes and danger levels to inform encounters and events.
- **Extensible ability pipeline** – `AbilitySystem` supports projectile, area-of-effect, and scripted abilities, providing an easy on-ramp for more complex behaviours (summons, buffs, status effects).
- **Spawner hooks** – The `SpawnerSystem` emits camps, traders, dungeons, and world bosses, illustrating how large-scale events can be orchestrated.

## Next Steps

- Replace the geometric stand-ins with imported art or 2.5D characters.
- Sync the runtime state to a backend service for co-op / PvP experimentation.
- Expand the combat loop with hit reactions, status effect tracking, and AI goal selection.
- Connect loot drops to inventory UI and progression systems.

Contributions and experiments are welcome—extend the data tables or systems to prototype new ideas quickly.
