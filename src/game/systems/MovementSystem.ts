import { GameRuntime } from '../types';

export class MovementSystem {
  update(runtime: GameRuntime): void {
    const bounds = {
      minX: -runtime.world.width / 2,
      maxX: runtime.world.width / 2,
      minY: -runtime.world.height / 2,
      maxY: runtime.world.height / 2
    };

    for (const entity of runtime.entities.list) {
      entity.position.position.x = clamp(
        entity.position.position.x + entity.position.velocity.x * runtime.deltaTime,
        bounds.minX,
        bounds.maxX
      );
      entity.position.position.y = clamp(
        entity.position.position.y + entity.position.velocity.y * runtime.deltaTime,
        bounds.minY,
        bounds.maxY
      );

      entity.position.velocity.x *= 0.8;
      entity.position.velocity.y *= 0.8;
    }
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
