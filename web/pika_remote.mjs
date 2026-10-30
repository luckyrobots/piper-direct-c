// SerialPort-shaped adapter for the C# backend. Physical serial is opened by
// the Linux agent; navigator.serial is never used by this transport.
export class RemotePikaPort {
  static async connect({ token, clear, fetcher = fetch }) {
    const port = new RemotePikaPort({ token, clear, fetcher });
    await port.post('connect', { take_over: true }); port.start(); return port;
  }
  constructor({ token, clear, fetcher }) {
    // Call browser fetch as a function, never with this port as its receiver.
    this.token = token; this.clear = clear; this.fetcher = (...args) => fetcher(...args);
    this.owner = crypto.randomUUID(); this.closed = false; this.lastHeartbeat = 0;
    this.writable = { getWriter: () => {
      if (this.writerLocked) throw Error('Pika writer is already locked');
      this.writerLocked = true;
      return { write: bytes => this.write(bytes), releaseLock: () => { this.writerLocked = false; } };
    } };
  }
  async post(action, extra = {}) {
    const response = await this.fetcher(`/api/pika/${action}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Piper-Token': this.token() },
      body: JSON.stringify({ owner: this.owner, ...extra }), signal: AbortSignal.timeout(action === 'connect' ? 17000 : 4000),
    });
    const result = await response.json();
    if (!response.ok || !result.ok) throw Error(result.error || result.message || 'Pika backend request failed');
    return result;
  }
  async write(bytes) {
    if (this.closed || bytes.length !== 7 || bytes[5] !== 13 || bytes[6] !== 10) throw Error('Invalid Pika packet');
    const value = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getFloat32(1, true);
    if (!Number.isFinite(value)) throw Error('Invalid Pika value');
    if (bytes[0] === 10) return this.post('disable');
    if (bytes[0] === 11) return this.post('enable', { clear: !!this.clear() });
    if (bytes[0] === 22) return this.post('position', { value });
    throw Error('Unsupported Pika command');
  }
  start() {
    this.readable = new ReadableStream({
      start: controller => {
        this.pumping = (async () => {
          const encoder = new TextEncoder();
          try {
            while (!this.closed) {
              if (performance.now() - this.lastHeartbeat > 400) {
                await this.post('heartbeat'); this.lastHeartbeat = performance.now();
              }
              const response = await this.fetcher('/api/pika/state', { cache: 'no-store', signal: AbortSignal.timeout(1000) });
              if (!response.ok) throw Error('Pika backend is unavailable');
              const state = await response.json();
              if (!state.connected) throw Error(state.message || 'Pika agent disconnected');
              if (!this.closed && state.fresh && state.telemetry) controller.enqueue(encoder.encode(JSON.stringify(state.telemetry)));
              await new Promise(resolve => setTimeout(resolve, 100));
            }
          } catch (error) { if (!this.closed) controller.error(error); }
        })();
      },
      cancel: () => { this.closed = true; },
    });
  }
  async close() {
    this.closed = true; await this.pumping;
    await this.post('disconnect');
  }
}
