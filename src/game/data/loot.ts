import { LootTable } from '../types';

export const lootTables: LootTable[] = [
  {
    id: 'wilds-common',
    entries: [
      { id: 'copper_coin', weight: 4, minLevel: 1, tags: ['currency'] },
      { id: 'healing_potion', weight: 2, minLevel: 1, tags: ['consumable'] },
      { id: 'ember_shard', weight: 1, minLevel: 2, tags: ['crafting'] }
    ]
  },
  {
    id: 'world-boss',
    entries: [
      { id: 'astral_core', weight: 1, minLevel: 5, tags: ['legendary', 'crafting'] },
      { id: 'ancient_relic', weight: 1, minLevel: 5, tags: ['trinket'] },
      { id: 'pile_of_gold', weight: 2, minLevel: 5, tags: ['currency'] }
    ]
  }
];
