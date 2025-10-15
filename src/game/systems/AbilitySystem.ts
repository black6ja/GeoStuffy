import { AbilityCastRequest, DamageEvent } from '../core/events';
import {
  AbilityDefinition,
  AbilityStateComponent,
  Entity,
  GameRuntime,
  Vector2
} from '../types';

interface Projectile {
  id: number;
  ability: AbilityDefinition;
  caster: Entity;
  position: Vector2;
  direction: Vector2;
  speed: number;
  life: number;
}

let nextProjectileId = 1;

export class AbilitySystem {
  projectiles: Projectile[] = [];
  private queuedCasts: AbilityCastRequest[] = [];

  constructor(private runtime: GameRuntime) {
    this.runtime.eventBus.on<AbilityCastRequest>('ability:cast', (payload) => {
      this.queuedCasts.push(payload);
    });
  }

  update(): void {
    const { deltaTime, entities } = this.runtime;

    for (const entity of entities.list) {
      this.tickCooldowns(entity.abilities, deltaTime);
    }

    while (this.queuedCasts.length > 0) {
      const cast = this.queuedCasts.shift();
      if (!cast) break;
      this.processCast(cast);
    }

    this.updateProjectiles();
  }

  private tickCooldowns(state: AbilityStateComponent, dt: number) {
    for (const key of Object.keys(state.cooldowns)) {
      state.cooldowns[key] = Math.max(0, state.cooldowns[key] - dt);
    }
  }

  private processCast(request: AbilityCastRequest) {
    const caster = this.runtime.entities.getById(request.caster);
    if (!caster) return;
    const ability = this.runtime.registry.abilities.find((a) => a.id === request.abilityId);
    if (!ability) return;

    const remaining = caster.abilities.cooldowns[ability.id] ?? 0;
    if (remaining > 0 || caster.stats.mana < ability.resourceCost) {
      return;
    }

    caster.stats.mana = Math.max(0, caster.stats.mana - ability.resourceCost);
    caster.abilities.cooldowns[ability.id] = ability.cooldown;

    const origin = {
      x: caster.position.position.x,
      y: caster.position.position.y
    };

    if (ability.scripted) {
      ability.scripted({
        caster: caster.id,
        origin,
        direction: request.direction,
        game: this.runtime
      });
      return;
    }

    ability.effects.forEach((effect) => {
      if (effect.type === 'projectile') {
        this.spawnProjectile(ability, caster, origin, request.direction, effect);
      } else if (effect.type === 'aoe') {
        this.resolveAoe(ability, caster, origin, effect.radius ?? 120, effect.magnitude);
      } else if (effect.type === 'heal') {
        caster.stats.health = Math.min(caster.stats.maxHealth, caster.stats.health + effect.magnitude);
      }
    });
  }

  private spawnProjectile(
    ability: AbilityDefinition,
    caster: Entity,
    origin: Vector2,
    direction: Vector2,
    effect: AbilityDefinition['effects'][number]
  ) {
    const normalized = normalize(direction);
    const speed = ability.travelSpeed ?? 420;
    this.projectiles.push({
      id: nextProjectileId++,
      ability,
      caster,
      position: { ...origin },
      direction: normalized,
      speed,
      life: ability.range / speed,
    });
  }

  private resolveAoe(
    ability: AbilityDefinition,
    caster: Entity,
    origin: Vector2,
    radius: number,
    magnitude: number
  ) {
    for (const entity of this.runtime.entities.list) {
      if (entity.id === caster.id) continue;
      if (!isHostile(caster, entity)) continue;
      const distance = distanceBetween(origin, entity.position.position);
      if (distance <= radius) {
        this.runtime.eventBus.emit<DamageEvent>('combat:damage', {
          source: caster.id,
          target: entity.id,
          amount: magnitude + caster.stats.abilityPower,
          tags: ability.tags
        });
      }
    }
  }

  private updateProjectiles() {
    const { deltaTime } = this.runtime;
    this.projectiles = this.projectiles.filter((projectile) => {
      projectile.position.x += projectile.direction.x * projectile.speed * deltaTime;
      projectile.position.y += projectile.direction.y * projectile.speed * deltaTime;
      projectile.life -= deltaTime;
      if (projectile.life <= 0) {
        return false;
      }

      for (const target of this.runtime.entities.list) {
        if (target.id === projectile.caster.id) continue;
        if (!isHostile(projectile.caster, target)) continue;
        const distance = distanceBetween(projectile.position, target.position.position);
        if (distance < target.appearance.size + 8) {
          this.runtime.eventBus.emit<DamageEvent>('combat:damage', {
            source: projectile.caster.id,
            target: target.id,
            amount: projectile.ability.effects[0]?.magnitude ?? 10,
            tags: projectile.ability.tags
          });
          return false;
        }
      }

      return true;
    });
  }
}

function normalize(vec: Vector2): Vector2 {
  const length = Math.hypot(vec.x, vec.y) || 1;
  return { x: vec.x / length, y: vec.y / length };
}

function distanceBetween(a: Vector2, b: Vector2): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

function isHostile(a: Entity, b: Entity): boolean {
  return (a.faction.hostility[b.faction.name] ?? 0) < 0;
}
