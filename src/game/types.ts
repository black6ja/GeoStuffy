export type EntityId = number;

export interface Vector2 {
  x: number;
  y: number;
}

export type AbilityTier = 'common' | 'rare' | 'epic' | 'legendary';

export interface AbilityEffect {
  type: 'projectile' | 'aoe' | 'buff' | 'summon' | 'heal' | 'damage-over-time';
  magnitude: number;
  radius?: number;
  duration?: number;
  status?: 'burn' | 'freeze' | 'shock' | 'poison' | 'haste' | 'shield';
}

export interface AbilityDefinition {
  id: string;
  name: string;
  tier: AbilityTier;
  resourceCost: number;
  cooldown: number;
  range: number;
  travelSpeed?: number;
  tags: string[];
  summary: string;
  effects: AbilityEffect[];
  scripted?: (context: AbilityContext) => void;
}

export interface AbilityContext {
  caster: EntityId;
  origin: Vector2;
  direction: Vector2;
  game: GameRuntime;
}

export interface ClassDefinition {
  id: string;
  name: string;
  fantasy: string;
  startingStats: StatsComponent;
  signatureAbilities: string[];
}

export interface LootTableEntry {
  id: string;
  weight: number;
  minLevel: number;
  maxLevel?: number;
  tags: string[];
}

export interface LootTable {
  id: string;
  entries: LootTableEntry[];
}

export interface StatsComponent {
  level: number;
  experience: number;
  health: number;
  maxHealth: number;
  mana: number;
  maxMana: number;
  attackPower: number;
  abilityPower: number;
  defense: number;
  movementSpeed: number;
}

export interface PositionComponent {
  position: Vector2;
  velocity: Vector2;
}

export interface AppearanceComponent {
  shape: 'triangle' | 'square' | 'pentagon' | 'hexagon' | 'circle';
  color: string;
  size: number;
}

export interface ControllerComponent {
  type: 'player' | 'companion' | 'hostile' | 'neutral';
  abilityBar: string[];
}

export interface BehaviorComponent {
  routine: 'wander' | 'pursue-player' | 'ranged-kite' | 'trader' | 'boss';
  target?: EntityId;
  cooldown: number;
}

export interface FactionComponent {
  name: 'players' | 'wildlife' | 'traders' | 'bosses';
  hostility: Record<string, number>;
}

export interface AbilityStateComponent {
  cooldowns: Record<string, number>;
}

export interface InventoryComponent {
  gold: number;
  items: string[];
}

export interface Entity {
  id: EntityId;
  position: PositionComponent;
  stats: StatsComponent;
  appearance: AppearanceComponent;
  controller: ControllerComponent;
  behavior?: BehaviorComponent;
  faction: FactionComponent;
  abilities: AbilityStateComponent;
  inventory: InventoryComponent;
}

export interface GameRuntime {
  deltaTime: number;
  eventBus: EventBus;
  entities: EntityStore;
  world: WorldState;
  registry: Registry;
}

export interface Registry {
  abilities: AbilityDefinition[];
  loot: LootTable[];
  classes: ClassDefinition[];
}

export interface WorldState {
  width: number;
  height: number;
  regions: WorldRegion[];
  timeOfDay: number;
}

export interface WorldRegion {
  id: string;
  biome: 'emberlands' | 'frostwilds' | 'shadowfen' | 'astral-grove';
  dangerLevel: number;
  anchor: Vector2;
}

export interface EntityStore {
  list: Entity[];
  create(entity: Partial<Entity>): Entity;
  remove(id: EntityId): void;
  getById(id: EntityId): Entity | undefined;
}

export interface EventBus {
  on<T>(event: string, handler: (payload: T) => void): void;
  off<T>(event: string, handler: (payload: T) => void): void;
  emit<T>(event: string, payload: T): void;
}
