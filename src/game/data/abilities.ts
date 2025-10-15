import { AbilityDefinition } from '../types';

export const abilityCatalog: AbilityDefinition[] = [
  {
    id: 'ember_bolt',
    name: 'Ember Bolt',
    tier: 'common',
    resourceCost: 5,
    cooldown: 0.8,
    range: 620,
    travelSpeed: 520,
    tags: ['fire', 'projectile'],
    summary: 'Rapid-fire bolt of ember flame that scorches wildlife.',
    effects: [
      {
        type: 'projectile',
        magnitude: 18,
        status: 'burn'
      }
    ]
  },
  {
    id: 'verdant_wave',
    name: 'Verdant Wave',
    tier: 'rare',
    resourceCost: 14,
    cooldown: 4,
    range: 360,
    tags: ['nature', 'heal'],
    summary: 'Eruptive pulse of nature energy that heals allies.',
    effects: [
      {
        type: 'heal',
        magnitude: 35,
        radius: 160
      }
    ]
  },
  {
    id: 'void_orb',
    name: 'Void Orb',
    tier: 'rare',
    resourceCost: 10,
    cooldown: 2.5,
    range: 520,
    travelSpeed: 360,
    tags: ['void', 'projectile', 'slow'],
    summary: 'Orbiting sphere of void energy that slows upon impact.',
    effects: [
      {
        type: 'projectile',
        magnitude: 24,
        status: 'freeze'
      }
    ]
  },
  {
    id: 'astral_beam',
    name: 'Astral Beam',
    tier: 'legendary',
    resourceCost: 25,
    cooldown: 8,
    range: 680,
    tags: ['astral', 'aoe'],
    summary: 'Channel a column of starlight that disintegrates foes.',
    effects: [
      {
        type: 'aoe',
        magnitude: 54,
        radius: 140,
        status: 'shock'
      }
    ]
  },
  {
    id: 'lunar_burst',
    name: 'Lunar Burst',
    tier: 'epic',
    resourceCost: 18,
    cooldown: 6,
    range: 500,
    tags: ['astral', 'aoe'],
    summary: 'Explosive shards of moonlight erupt around the caster.',
    effects: [
      {
        type: 'aoe',
        magnitude: 42,
        radius: 200,
        status: 'shock'
      }
    ]
  },
  {
    id: 'waystone_shift',
    name: 'Waystone Shift',
    tier: 'epic',
    resourceCost: 12,
    cooldown: 7,
    range: 420,
    tags: ['utility', 'movement'],
    summary: 'Teleport forward and leave a decoy to taunt enemies.',
    effects: [],
    scripted: ({ caster, direction, game }) => {
      const entity = game.entities.getById(caster);
      if (!entity) return;
      entity.position.position.x += direction.x * 140;
      entity.position.position.y += direction.y * 140;
      console.info('Waystone Shift executed. Future: spawn decoy entity.');
    }
  }
];
