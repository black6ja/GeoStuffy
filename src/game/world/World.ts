import { Vector2, WorldRegion, WorldState } from '../types';

const REGION_BIOMES: WorldRegion['biome'][] = [
  'emberlands',
  'frostwilds',
  'shadowfen',
  'astral-grove'
];

export class World implements WorldState {
  width: number;
  height: number;
  regions: WorldRegion[];
  timeOfDay = 0;

  constructor(width: number, height: number, seed = 1337) {
    this.width = width;
    this.height = height;
    this.regions = this.generateRegions(seed);
  }

  tick(delta: number) {
    this.timeOfDay = (this.timeOfDay + delta * 0.01) % 1;
  }

  private generateRegions(seed: number): WorldRegion[] {
    const regions: WorldRegion[] = [];
    const random = mulberry32(seed);
    const regionCount = 32;
    for (let i = 0; i < regionCount; i++) {
      const biome = REGION_BIOMES[i % REGION_BIOMES.length];
      const anchor: Vector2 = {
        x: random() * this.width - this.width / 2,
        y: random() * this.height - this.height / 2
      };
      regions.push({
        id: `${biome}-${i}`,
        biome,
        dangerLevel: Math.floor(random() * 5) + 1,
        anchor
      });
    }
    return regions;
  }
}

function mulberry32(seed: number) {
  return function () {
    let t = (seed += 0x6d2b79f5);
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
