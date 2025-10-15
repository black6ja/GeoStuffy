import { GameRuntime } from '../types';

export class BehaviorSystem {
  private elapsed = 0;

  update(runtime: GameRuntime): void {
    this.elapsed += runtime.deltaTime;
    for (const entity of runtime.entities.list) {
      if (!entity.behavior) continue;
      entity.behavior.cooldown -= runtime.deltaTime;
      if (entity.behavior.cooldown > 0) continue;

      switch (entity.behavior.routine) {
        case 'wander':
          this.applyWander(entity);
          break;
        case 'pursue-player':
          this.applyPursuit(entity, runtime);
          break;
        case 'trader':
          this.applyIdle(entity);
          break;
        case 'boss':
          this.applyBoss(entity, runtime);
          break;
        default:
          this.applyIdle(entity);
      }
      entity.behavior.cooldown = 0.75 + Math.random() * 0.5;
    }
  }

  private applyWander(entity: GameRuntime['entities']['list'][number]) {
    const angle = Math.random() * Math.PI * 2;
    const speed = entity.stats.movementSpeed * 0.4;
    entity.position.velocity.x += Math.cos(angle) * speed;
    entity.position.velocity.y += Math.sin(angle) * speed;
  }

  private applyPursuit(entity: GameRuntime['entities']['list'][number], runtime: GameRuntime) {
    const player = runtime.entities.list.find((candidate) => candidate.controller.type === 'player');
    if (!player) return;
    const dx = player.position.position.x - entity.position.position.x;
    const dy = player.position.position.y - entity.position.position.y;
    const length = Math.hypot(dx, dy) || 1;
    entity.position.velocity.x += (dx / length) * entity.stats.movementSpeed;
    entity.position.velocity.y += (dy / length) * entity.stats.movementSpeed;
  }

  private applyBoss(entity: GameRuntime['entities']['list'][number], runtime: GameRuntime) {
    this.applyPursuit(entity, runtime);
    runtime.eventBus.emit('boss:taunt', { id: entity.id, time: this.elapsed });
  }

  private applyIdle(entity: GameRuntime['entities']['list'][number]) {
    entity.position.velocity.x *= 0.5;
    entity.position.velocity.y *= 0.5;
  }
}
