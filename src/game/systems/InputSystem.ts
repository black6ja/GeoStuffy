import { AbilityCastRequest } from '../core/events';
import { Entity, GameRuntime, Vector2 } from '../types';

export class InputSystem {
  private keys = new Set<string>();
  private pointer: Vector2 = { x: 0, y: 0 };
  private queuedAbilities: string[] = [];
  private sprinting = false;

  constructor(private canvas: HTMLCanvasElement, private runtime: GameRuntime) {
    window.addEventListener('keydown', (event) => this.handleKeyDown(event));
    window.addEventListener('keyup', (event) => this.handleKeyUp(event));
    window.addEventListener('mousemove', (event) => this.handlePointer(event));
    window.addEventListener('blur', () => this.keys.clear());
  }

  update() {
    const player = this.runtime.entities.list.find((entity) => entity.controller.type === 'player');
    if (!player) return;

    const direction = this.computeMovementVector();
    const speed = player.stats.movementSpeed * (this.sprinting ? 1.5 : 1);
    player.position.velocity.x = direction.x * speed;
    player.position.velocity.y = direction.y * speed;

    while (this.queuedAbilities.length > 0) {
      const abilityId = this.queuedAbilities.shift();
      if (!abilityId) break;
      this.runtime.eventBus.emit<AbilityCastRequest>('ability:cast', {
        caster: player.id,
        abilityId,
        direction: this.computeAimVector(player)
      });
    }
  }

  private handleKeyDown(event: KeyboardEvent) {
    const key = event.key.toLowerCase();
    this.keys.add(key);
    if (['1', '2', '3', '4'].includes(key)) {
      event.preventDefault();
      const player = this.runtime.entities.list.find((entity) => entity.controller.type === 'player');
      if (!player) return;
      const abilityId = player.controller.abilityBar[Number(key) - 1];
      if (abilityId) {
        this.queuedAbilities.push(abilityId);
      }
    }
    if (key === 'shift') {
      this.sprinting = true;
    }
  }

  private handleKeyUp(event: KeyboardEvent) {
    const key = event.key.toLowerCase();
    this.keys.delete(key);
    if (key === 'shift') {
      this.sprinting = false;
    }
  }

  private handlePointer(event: MouseEvent) {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer = {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top
    };
  }

  private computeMovementVector(): Vector2 {
    let x = 0;
    let y = 0;
    if (this.keys.has('w')) y -= 1;
    if (this.keys.has('s')) y += 1;
    if (this.keys.has('a')) x -= 1;
    if (this.keys.has('d')) x += 1;

    const length = Math.hypot(x, y) || 1;
    return { x: x / length, y: y / length };
  }

  private computeAimVector(player: Entity): Vector2 {
    const rect = this.canvas.getBoundingClientRect();
    const worldTarget = {
      x: this.pointer.x - rect.width / 2,
      y: this.pointer.y - rect.height / 2
    };
    const direction = {
      x: worldTarget.x - player.position.position.x,
      y: worldTarget.y - player.position.position.y
    };
    const length = Math.hypot(direction.x, direction.y) || 1;
    return { x: direction.x / length, y: direction.y / length };
  }
}
