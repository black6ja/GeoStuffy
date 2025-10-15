import { AbilitySystem } from '../systems/AbilitySystem';
import { BehaviorSystem } from '../systems/BehaviorSystem';
import { CombatSystem } from '../systems/CombatSystem';
import { InputSystem } from '../systems/InputSystem';
import { MovementSystem } from '../systems/MovementSystem';
import { SpawnerSystem } from '../systems/SpawnerSystem';
import { abilityCatalog as defaultAbilities } from '../data/abilities';
import { classCatalog as defaultClasses } from '../data/classes';
import { lootTables as defaultLoot } from '../data/loot';
import { SimpleEventBus } from './EventBus';
import { InMemoryEntityStore } from './EntityStore';
import { World } from '../world/World';
import {
  AbilityDefinition,
  ClassDefinition,
  Entity,
  GameRuntime,
  LootTable,
  Registry,
  StatsComponent,
  Vector2
} from '../types';

interface GameConfig {
  abilityCatalog?: AbilityDefinition[];
  classCatalog?: ClassDefinition[];
  lootTables?: LootTable[];
}

export class Game {
  private ctx: CanvasRenderingContext2D;
  private runtime: GameRuntime;
  private abilitySystem: AbilitySystem;
  private inputSystem: InputSystem;
  private behaviorSystem: BehaviorSystem;
  private movementSystem: MovementSystem;
  private combatSystem: CombatSystem;
  private spawnerSystem: SpawnerSystem;
  private lastTimestamp = 0;
  private running = false;

  constructor(private canvas: HTMLCanvasElement, config?: GameConfig) {
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('Canvas rendering context missing');
    this.ctx = ctx;

    const registry: Registry = {
      abilities: config?.abilityCatalog ?? defaultAbilities,
      classes: config?.classCatalog ?? defaultClasses,
      loot: config?.lootTables ?? defaultLoot
    };

    const eventBus = new SimpleEventBus();
    const world = new World(3200, 3200);
    const entities = new InMemoryEntityStore();

    this.runtime = {
      deltaTime: 0,
      eventBus,
      entities,
      world,
      registry
    };

    this.abilitySystem = new AbilitySystem(this.runtime);
    this.inputSystem = new InputSystem(canvas, this.runtime);
    this.behaviorSystem = new BehaviorSystem();
    this.movementSystem = new MovementSystem();
    this.combatSystem = new CombatSystem(this.runtime);
    this.spawnerSystem = new SpawnerSystem(this.runtime);
  }

  bootstrap() {
    this.spawnPlayer();
    this.spawnCompanion();
    this.spawnInitialWildlife();
    this.running = true;
    requestAnimationFrame((timestamp) => this.loop(timestamp));
  }

  private loop(timestamp: number) {
    if (!this.running) return;
    const dt = (timestamp - this.lastTimestamp) / 1000 || 0;
    this.lastTimestamp = timestamp;

    this.runtime.deltaTime = Math.min(dt, 0.033);
    this.runtime.world.tick(this.runtime.deltaTime);

    this.inputSystem.update();
    this.behaviorSystem.update(this.runtime);
    this.movementSystem.update(this.runtime);
    this.abilitySystem.update();
    this.spawnerSystem.update();
    this.combatSystem.update();

    this.render();
    requestAnimationFrame((time) => this.loop(time));
  }

  private render() {
    const ctx = this.ctx;
    const { width, height } = this.canvas;
    ctx.resetTransform();
    ctx.clearRect(0, 0, width, height);

    const player = this.runtime.entities.list.find((entity) => entity.controller.type === 'player');
    const camera = player?.position.position ?? { x: 0, y: 0 };

    this.drawBackground(camera);
    this.drawRegions(camera);
    this.drawEntities(camera);
    this.drawProjectiles(camera);
    if (player) {
      this.drawAbilityBar(player);
      this.drawVitals(player);
    }
  }

  private drawBackground(camera: Vector2) {
    const ctx = this.ctx;
    const gradient = ctx.createRadialGradient(
      this.canvas.width / 2,
      this.canvas.height / 2,
      80,
      this.canvas.width / 2,
      this.canvas.height / 2,
      this.canvas.width
    );
    gradient.addColorStop(0, 'rgba(12, 16, 40, 0.95)');
    gradient.addColorStop(1, 'rgba(4, 6, 16, 0.7)');
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);

    ctx.save();
    ctx.translate(this.canvas.width / 2 - camera.x * 0.05, this.canvas.height / 2 - camera.y * 0.05);
    ctx.fillStyle = 'rgba(64, 82, 182, 0.06)';
    for (let i = 0; i < 220; i++) {
      const x = Math.cos(i) * 1800;
      const y = Math.sin(i * 1.23) * 1800;
      ctx.fillRect(x, y, 2, 2);
    }
    ctx.restore();
  }

  private drawRegions(camera: Vector2) {
    const ctx = this.ctx;
    ctx.save();
    ctx.translate(this.canvas.width / 2 - camera.x, this.canvas.height / 2 - camera.y);
    for (const region of this.runtime.world.regions) {
      const color = regionColor(region.biome);
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(region.anchor.x, region.anchor.y, 260, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.restore();
  }

  private drawEntities(camera: Vector2) {
    const ctx = this.ctx;
    ctx.save();
    ctx.translate(this.canvas.width / 2 - camera.x, this.canvas.height / 2 - camera.y);

    for (const entity of this.runtime.entities.list) {
      const { position, appearance } = entity;
      ctx.save();
      ctx.translate(position.position.x, position.position.y);
      ctx.fillStyle = applyFactionTint(appearance.color, entity.faction.name);
      ctx.strokeStyle = 'rgba(255,255,255,0.12)';
      ctx.lineWidth = 2;
      drawShape(ctx, appearance.shape, appearance.size);
      ctx.restore();
    }
    ctx.restore();
  }

  private drawProjectiles(camera: Vector2) {
    const ctx = this.ctx;
    ctx.save();
    ctx.translate(this.canvas.width / 2 - camera.x, this.canvas.height / 2 - camera.y);
    for (const projectile of this.abilitySystem.projectiles) {
      ctx.fillStyle = '#fbc531';
      ctx.beginPath();
      ctx.arc(projectile.position.x, projectile.position.y, 6, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.restore();
  }

  private drawAbilityBar(player: Entity) {
    const ctx = this.ctx;
    if (player.controller.abilityBar.length === 0) {
      return;
    }
    const barWidth = 320;
    const barHeight = 60;
    const x = this.canvas.width / 2 - barWidth / 2;
    const y = this.canvas.height - barHeight - 24;
    ctx.save();
    ctx.fillStyle = 'rgba(10, 12, 26, 0.9)';
    ctx.fillRect(x, y, barWidth, barHeight);
    ctx.strokeStyle = 'rgba(95, 176, 255, 0.4)';
    ctx.lineWidth = 2;
    ctx.strokeRect(x, y, barWidth, barHeight);

    const slotWidth = barWidth / player.controller.abilityBar.length;
    player.controller.abilityBar.forEach((abilityId, index) => {
      const ability = this.runtime.registry.abilities.find((a) => a.id === abilityId);
      if (!ability) return;
      const slotX = x + index * slotWidth;
      ctx.fillStyle = 'rgba(28, 32, 64, 0.8)';
      ctx.fillRect(slotX + 4, y + 6, slotWidth - 8, barHeight - 12);
      ctx.fillStyle = '#f5f7ff';
      ctx.font = '12px Oxanium, sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(`${index + 1}: ${ability.name}`, slotX + slotWidth / 2, y + 26);
      const remaining = player.abilities.cooldowns[abilityId] ?? 0;
      if (remaining > 0) {
        ctx.fillStyle = 'rgba(255, 99, 71, 0.8)';
        ctx.fillText(remaining.toFixed(1) + 's', slotX + slotWidth / 2, y + 46);
      }
    });
    ctx.restore();
  }

  private drawVitals(player: Entity) {
    const ctx = this.ctx;
    ctx.save();
    ctx.fillStyle = 'rgba(10, 12, 26, 0.8)';
    ctx.fillRect(24, this.canvas.height - 90, 180, 70);
    ctx.strokeStyle = 'rgba(95, 176, 255, 0.4)';
    ctx.strokeRect(24, this.canvas.height - 90, 180, 70);
    ctx.fillStyle = '#f5f7ff';
    ctx.font = '14px Oxanium, sans-serif';
    ctx.fillText(`Health ${Math.round(player.stats.health)} / ${player.stats.maxHealth}`, 34, this.canvas.height - 60);
    ctx.fillText(`Mana ${Math.round(player.stats.mana)} / ${player.stats.maxMana}`, 34, this.canvas.height - 40);
    ctx.restore();
  }

  private spawnPlayer() {
    const archetype = this.runtime.registry.classes[0];
    const stats: StatsComponent = { ...archetype.startingStats };
    const player: Entity = this.runtime.entities.create({
      position: {
        position: { x: 0, y: 0 },
        velocity: { x: 0, y: 0 }
      },
      stats,
      appearance: {
        shape: 'hexagon',
        color: '#8c7ae6',
        size: 28
      },
      controller: {
        type: 'player',
        abilityBar: archetype.signatureAbilities
      },
      behavior: undefined,
      faction: {
        name: 'players',
        hostility: { players: 1, wildlife: -1, traders: 0.6, bosses: -1 }
      },
      abilities: {
        cooldowns: {}
      },
      inventory: {
        gold: 0,
        items: []
      }
    });
    player.stats.health = stats.maxHealth;
    player.stats.mana = stats.maxMana;
  }

  private spawnCompanion() {
    const player = this.runtime.entities.list.find((entity) => entity.controller.type === 'player');
    if (!player) return;
    const companion = this.runtime.entities.create({
      position: {
        position: { x: player.position.position.x + 60, y: player.position.position.y + 20 },
        velocity: { x: 0, y: 0 }
      },
      stats: {
        level: 1,
        experience: 0,
        health: 80,
        maxHealth: 80,
        mana: 30,
        maxMana: 30,
        attackPower: 8,
        abilityPower: 6,
        defense: 4,
        movementSpeed: 110
      },
      appearance: {
        shape: 'circle',
        color: '#20bf6b',
        size: 20
      },
      controller: {
        type: 'companion',
        abilityBar: ['ember_bolt']
      },
      behavior: {
        routine: 'wander',
        cooldown: 0.5
      },
      faction: {
        name: 'players',
        hostility: { players: 1, wildlife: -1, traders: 0.6, bosses: -1 }
      },
      abilities: {
        cooldowns: {}
      },
      inventory: {
        gold: 0,
        items: []
      }
    });
    companion.stats.health = companion.stats.maxHealth;
    companion.stats.mana = companion.stats.maxMana;
  }

  private spawnInitialWildlife() {
    const ring = 12;
    for (let i = 0; i < ring; i++) {
      const angle = (i / ring) * Math.PI * 2;
      const distance = 260 + Math.random() * 140;
      const enemy = this.runtime.entities.create({
        position: {
          position: { x: Math.cos(angle) * distance, y: Math.sin(angle) * distance },
          velocity: { x: 0, y: 0 }
        },
        stats: {
          level: 1,
          experience: 0,
          health: 55,
          maxHealth: 55,
          mana: 30,
          maxMana: 30,
          attackPower: 7,
          abilityPower: 6,
          defense: 3,
          movementSpeed: 90
        },
        appearance: {
          shape: 'triangle',
          color: '#ff6b81',
          size: 20
        },
        controller: {
          type: 'hostile',
          abilityBar: ['void_orb']
        },
        behavior: {
          routine: 'wander',
          cooldown: Math.random()
        },
        faction: {
          name: 'wildlife',
          hostility: { players: -1, wildlife: 0.2, traders: -0.5, bosses: -0.7 }
        },
        abilities: {
          cooldowns: {}
        },
        inventory: {
          gold: 0,
          items: []
        }
      });
      enemy.stats.health = enemy.stats.maxHealth;
      enemy.stats.mana = enemy.stats.maxMana;
    }
  }
}

function drawShape(ctx: CanvasRenderingContext2D, shape: Entity['appearance']['shape'], size: number) {
  ctx.beginPath();
  switch (shape) {
    case 'circle':
      ctx.arc(0, 0, size, 0, Math.PI * 2);
      break;
    case 'square':
      ctx.rect(-size, -size, size * 2, size * 2);
      break;
    case 'triangle':
      ctx.moveTo(-size, size);
      ctx.lineTo(0, -size);
      ctx.lineTo(size, size);
      ctx.closePath();
      break;
    case 'pentagon':
    case 'hexagon':
      const sides = shape === 'pentagon' ? 5 : 6;
      for (let i = 0; i < sides; i++) {
        const angle = (i / sides) * Math.PI * 2 - Math.PI / 2;
        const x = Math.cos(angle) * size;
        const y = Math.sin(angle) * size;
        if (i === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      }
      ctx.closePath();
      break;
  }
  ctx.fill();
  ctx.stroke();
}

function regionColor(biome: string) {
  switch (biome) {
    case 'emberlands':
      return 'rgba(255, 107, 53, 0.12)';
    case 'frostwilds':
      return 'rgba(52, 172, 224, 0.12)';
    case 'shadowfen':
      return 'rgba(10, 189, 227, 0.08)';
    case 'astral-grove':
      return 'rgba(190, 144, 212, 0.12)';
    default:
      return 'rgba(95, 176, 255, 0.08)';
  }
}

function applyFactionTint(color: string, faction: Entity['faction']['name']) {
  const overlay = {
    players: 'rgba(95, 176, 255, 0.3)',
    wildlife: 'rgba(255, 107, 129, 0.3)',
    traders: 'rgba(46, 204, 113, 0.3)',
    bosses: 'rgba(255, 159, 67, 0.3)'
  }[faction] ?? 'rgba(255, 255, 255, 0.25)';
  const ctx = document.createElement('canvas').getContext('2d');
  if (!ctx) return color;
  ctx.canvas.width = ctx.canvas.height = 1;
  ctx.fillStyle = color;
  ctx.fillRect(0, 0, 1, 1);
  ctx.globalAlpha = 0.6;
  ctx.fillStyle = overlay;
  ctx.fillRect(0, 0, 1, 1);
  const data = ctx.getImageData(0, 0, 1, 1).data;
  return `rgba(${data[0]}, ${data[1]}, ${data[2]}, ${Math.round(data[3] / 2.55) / 100})`;
}
