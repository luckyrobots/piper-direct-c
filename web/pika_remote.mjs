// One page's Pika session over the generated API client (api.mjs). The Linux
// agent behind the Host is the only jaw authority (target-architecture Q5): this
// client owns no serial port, sends only what its caller asks for, and never retries.
//
// One client serves one page session. `owner` is the page's identity; the
// gripper it addresses is the Host's default assignment until connect() binds
// the canonical device the Host answered with, after which every request names
// that gripper explicitly.
import { createApi } from './api.mjs';

export class PikaClient {
  constructor({ owner, token, fetcher = fetch }) {
    // Call browser fetch as a function, never with a client as its receiver.
    this.owner = owner; this.api = createApi({ token, fetcher: (...args) => fetcher(...args) });
    this.gripper = null;
  }
  selector() { return this.gripper ? { pika_device: this.gripper.device, pika_distribution: this.gripper.distribution } : {}; }
  // Sends one gripper action as this page, addressed to the bound gripper.
  async send(operation, fields = {}, { timeout = 4000, keepalive = false } = {}) {
    const response = await operation({ owner: this.owner, ...this.selector(), ...fields }, { timeout, keepalive });
    const result = await response.json();
    if (!response.ok || !result.ok) throw Error(result.error || result.message || 'Pika backend request failed');
    return result;
  }
  // Reads only. `takeOver` is sent solely when the caller passes it: the Host
  // then closes a live session of another owner.
  async connect({ takeOver = false } = {}) {
    const result = await this.send(this.api.pikaConnect, takeOver ? { take_over: true } : {}, { timeout: 17000 });
    if (typeof result.device === 'string' && result.device) this.gripper = { device: result.device, distribution: result.distribution ?? null };
    return result;
  }
  // `clear` is the operator's finger-clear statement; the caller obtains it for this request.
  enable() { return this.send(this.api.pikaEnable, { clear: true }); }
  // keepalive lets a disable outlive the page that sent it (STOP, pagehide).
  disable() { return this.send(this.api.pikaDisable, {}, { keepalive: true }); }
  position(value) {
    if (!Number.isFinite(value)) throw Error('Invalid Pika value');
    return this.send(this.api.pikaPosition, { value });
  }
  heartbeat() { return this.send(this.api.pikaHeartbeat); }
  disconnect() { const closing = this.send(this.api.pikaDisconnect, {}, { keepalive: true }); this.gripper = null; return closing; }
  // The session is gone (lost or taken over): address the Host's default assignment again.
  forget() { this.gripper = null; }
  async snapshot() {
    // A null distribution (native Linux) is not sent: the Host then reads its current assignment's.
    const response = await this.api.pikaState(this.selector(), { timeout: 1000 });
    if (!response.ok) throw Error('Pika backend is unavailable');
    return response.json();
  }
}
