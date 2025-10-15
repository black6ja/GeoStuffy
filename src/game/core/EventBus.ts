import { EventBus } from '../types';

type Handler = (payload: unknown) => void;

export class SimpleEventBus implements EventBus {
  private listeners: Map<string, Set<Handler>> = new Map();

  on<T>(event: string, handler: (payload: T) => void): void {
    const bucket = this.listeners.get(event) ?? new Set<Handler>();
    bucket.add(handler as Handler);
    this.listeners.set(event, bucket);
  }

  off<T>(event: string, handler: (payload: T) => void): void {
    const bucket = this.listeners.get(event);
    if (!bucket) return;
    bucket.delete(handler as Handler);
    if (bucket.size === 0) {
      this.listeners.delete(event);
    }
  }

  emit<T>(event: string, payload: T): void {
    const bucket = this.listeners.get(event);
    if (!bucket) return;
    for (const handler of bucket) {
      handler(payload);
    }
  }
}
