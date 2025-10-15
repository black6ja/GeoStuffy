import { DamageEvent, LootDropEvent } from '../core/events';
import { Entity, GameRuntime } from '../types';

export class CombatSystem {
  constructor(private runtime: GameRuntime) {
    this.runtime.eventBus.on<DamageEvent>('combat:damage', (payload) => this.resolveDamage(payload));
  }

  update(): void {
    for (const entity of [...this.runtime.entities.list]) {
      if (entity.stats.health <= 0) {
        this.handleDeath(entity);
      }
    }
  }

  private resolveDamage(payload: DamageEvent) {
    const target = this.runtime.entities.getById(payload.target);
    if (!target) return;
    const mitigated = Math.max(1, payload.amount - target.stats.defense * 0.4);
    target.stats.health -= mitigated;
  }

  private handleDeath(entity: Entity) {
    this.runtime.eventBus.emit<LootDropEvent>('loot:drop', {
      origin: entity.id,
      tableId: entity.faction.name === 'bosses' ? 'world-boss' : 'wilds-common'
    });
    this.runtime.entities.remove(entity.id);
  }
}
