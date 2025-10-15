import { LootDropEvent, SpawnEvent } from '../core/events';
import {
  AbilityStateComponent,
  AppearanceComponent,
  BehaviorComponent,
  ControllerComponent,
  Entity,
  GameRuntime,
  InventoryComponent,
  PositionComponent,
  StatsComponent
} from '../types';

export class SpawnerSystem {
  private elapsed = 0;

  constructor(private runtime: GameRuntime) {
    this.runtime.eventBus.on<LootDropEvent>('loot:drop', (payload) => this.handleLoot(payload));
  }

  update(): void {
    this.elapsed += this.runtime.deltaTime;
    if (this.elapsed > 8) {
      this.elapsed = 0;
      const roll = Math.random();
      const event: SpawnEvent['kind'] = roll > 0.9 ? 'world-boss' : roll > 0.7 ? 'dungeon' : roll > 0.4 ? 'trader' : 'camp';
      this.spawnEncounter(event);
    }
  }

  private spawnEncounter(kind: SpawnEvent['kind']) {
    switch (kind) {
      case 'camp':
        this.spawnWildGroup(4, 'shadowfen');
        break;
      case 'trader':
        this.spawnTrader();
        break;
      case 'dungeon':
        this.spawnWildGroup(6, 'emberlands');
        break;
      case 'world-boss':
        this.spawnBoss();
        break;
    }
  }

  private spawnWildGroup(count: number, biome: string) {
    const center = this.pickRegionAnchor(biome);
    for (let i = 0; i < count; i++) {
      const position = this.randomizedPosition(center, 180);
      this.runtime.entities.create(createEnemy(position));
    }
  }

  private spawnBoss() {
    const anchor = this.pickRegionAnchor('astral-grove');
    const boss = createEnemy(anchor);
    boss.stats.health *= 8;
    boss.stats.maxHealth *= 8;
    boss.stats.movementSpeed *= 0.8;
    boss.controller.abilityBar = ['astral_beam', 'lunar_burst'];
    boss.behavior = {
      routine: 'boss',
      cooldown: 1.5
    };
    boss.appearance.size = 48;
    boss.appearance.color = '#ff9f43';
    boss.faction.name = 'bosses';
    boss.faction.hostility = { players: -1, wildlife: -1, traders: -1, bosses: 1 };
    this.runtime.entities.create(boss);
  }

  private spawnTrader() {
    const anchor = this.pickRegionAnchor('frostwilds');
    const trader = createTrader(anchor);
    this.runtime.entities.create(trader);
  }

  private handleLoot(event: LootDropEvent) {
    const table = this.runtime.registry.loot.find((loot) => loot.id === event.tableId);
    if (!table) return;
    const reward = rollTable(table);
    if (!reward) return;
    // For now we simply log to the console to show the drop.
    console.info('[Loot]', event.origin, 'dropped', reward);
  }

  private pickRegionAnchor(biome: string) {
    const region = this.runtime.world.regions.find((candidate) => candidate.biome === biome);
    return region?.anchor ?? { x: 0, y: 0 };
  }

  private randomizedPosition(anchor: { x: number; y: number }, radius: number) {
    const angle = Math.random() * Math.PI * 2;
    const distance = Math.random() * radius;
    return {
      position: {
        x: anchor.x + Math.cos(angle) * distance,
        y: anchor.y + Math.sin(angle) * distance
      },
      velocity: { x: 0, y: 0 }
    } as PositionComponent;
  }
}

function createEnemy(position: PositionComponent): Entity {
  const stats: StatsComponent = {
    level: 1,
    experience: 0,
    health: 60,
    maxHealth: 60,
    mana: 35,
    maxMana: 35,
    attackPower: 6,
    abilityPower: 8,
    defense: 4,
    movementSpeed: 65
  };

  const controller: ControllerComponent = {
    type: 'hostile',
    abilityBar: ['void_orb']
  };

  const behavior: BehaviorComponent = {
    routine: 'pursue-player',
    cooldown: Math.random() * 0.5
  };

  const appearance: AppearanceComponent = {
    shape: 'triangle',
    color: '#ff4757',
    size: 22
  };

  const faction = {
    name: 'wildlife' as const,
    hostility: { players: -1, wildlife: 0, traders: -0.2, bosses: -0.5 }
  };

  const abilities: AbilityStateComponent = {
    cooldowns: {}
  };

  const inventory: InventoryComponent = {
    gold: 0,
    items: []
  };

  return {
    id: 0,
    position,
    stats,
    appearance,
    controller,
    behavior,
    faction,
    abilities,
    inventory
  };
}

function createTrader(position: PositionComponent): Entity {
  const stats: StatsComponent = {
    level: 1,
    experience: 0,
    health: 120,
    maxHealth: 120,
    mana: 40,
    maxMana: 40,
    attackPower: 2,
    abilityPower: 4,
    defense: 2,
    movementSpeed: 30
  };
  const controller: ControllerComponent = {
    type: 'neutral',
    abilityBar: []
  };
  const behavior: BehaviorComponent = {
    routine: 'trader',
    cooldown: 2
  };
  const appearance: AppearanceComponent = {
    shape: 'square',
    color: '#1e90ff',
    size: 26
  };
  const faction = {
    name: 'traders' as const,
    hostility: { players: 0.8, wildlife: -0.4, traders: 1, bosses: -1 }
  };
  const abilities: AbilityStateComponent = { cooldowns: {} };
  const inventory: InventoryComponent = {
    gold: 120,
    items: ['healing_potion', 'mystic_thread']
  };
  return {
    id: 0,
    position,
    stats,
    appearance,
    controller,
    behavior,
    faction,
    abilities,
    inventory
  };
}

function rollTable(table: GameRuntime['registry']['loot'][number]) {
  const total = table.entries.reduce((sum, entry) => sum + entry.weight, 0);
  let roll = Math.random() * total;
  for (const entry of table.entries) {
    roll -= entry.weight;
    if (roll <= 0) return entry.id;
  }
  return undefined;
}
