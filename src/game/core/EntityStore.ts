import { Entity, EntityId, EntityStore } from '../types';

let nextEntityId: EntityId = 1;

export class InMemoryEntityStore implements EntityStore {
  list: Entity[] = [];

  create(entity: Partial<Entity>): Entity {
    const full: Entity = {
      id: nextEntityId++,
      position: entity.position!,
      stats: entity.stats!,
      appearance: entity.appearance!,
      controller: entity.controller!,
      behavior: entity.behavior,
      faction: entity.faction!,
      abilities: entity.abilities!,
      inventory: entity.inventory!
    };
    this.list.push(full);
    return full;
  }

  remove(id: EntityId): void {
    this.list = this.list.filter((entity) => entity.id !== id);
  }

  getById(id: EntityId): Entity | undefined {
    return this.list.find((entity) => entity.id === id);
  }
}
