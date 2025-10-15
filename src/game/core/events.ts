import { EntityId, Vector2 } from '../types';

export interface AbilityCastRequest {
  caster: EntityId;
  abilityId: string;
  direction: Vector2;
}

export interface DamageEvent {
  source: EntityId;
  target: EntityId;
  amount: number;
  tags: string[];
}

export interface SpawnEvent {
  kind: 'camp' | 'trader' | 'dungeon' | 'world-boss';
}

export interface LootDropEvent {
  origin: EntityId;
  tableId: string;
}
