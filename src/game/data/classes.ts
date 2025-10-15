import { ClassDefinition } from '../types';

const baseStats = {
  level: 1,
  experience: 0,
  health: 120,
  maxHealth: 120,
  mana: 60,
  maxMana: 60,
  attackPower: 10,
  abilityPower: 12,
  defense: 6,
  movementSpeed: 120
};

export const classCatalog: ClassDefinition[] = [
  {
    id: 'spellblade',
    name: 'Spellblade',
    fantasy: 'Arcane duelist weaving swordplay and spells.',
    startingStats: { ...baseStats, attackPower: 14, movementSpeed: 140 },
    signatureAbilities: ['ember_bolt', 'waystone_shift']
  },
  {
    id: 'warden',
    name: 'Warden of the Grove',
    fantasy: 'Shields allies while summoning wild spirits.',
    startingStats: { ...baseStats, health: 160, maxHealth: 160, defense: 10 },
    signatureAbilities: ['verdant_wave', 'void_orb']
  },
  {
    id: 'astral_archon',
    name: 'Astral Archon',
    fantasy: 'Bends constellations into devastating beams.',
    startingStats: { ...baseStats, mana: 90, maxMana: 90, abilityPower: 20 },
    signatureAbilities: ['astral_beam', 'lunar_burst']
  }
];
