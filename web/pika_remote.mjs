// HTTP client for the Host's Pika routes. The Linux agent behind the Host is
// the only jaw authority (target-architecture Q5): this client owns no serial
// port, sends only what its caller asks for, and never retries.
//
// One client serves one page session. `owner` is the page's identity; the
// gripper it addresses is the Host's default assignment until connect() binds
// the canonical device the Host answered with, after which every request names
// that gripper explicitly.
export class PikaClient {
  constructor({ owner, token, fetcher = fetch }) {
    // Call browser fetch as a function, never with this client as its receiver.
    this.owner = owner; this.token = token; this.fetcher = (...args) => fetcher(...args);
    this.gripper = null;
  }
  selector() { return this.gripper ? { pika_device: this.gripper.device, pika_distribution: this.gripper.distribution } : {}; }
  async post(action, extra = {}, { timeout = 4000, keepalive = false } = {}) {
    const response = await this.fetcher(`/api/pika/${action}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Piper-Token': this.token() },
      body: JSON.stringify({ owner: this.owner, ...this.selector(), ...extra }), keepalive, signal: AbortSignal.timeout(timeout),
    });
    const result = await response.json();
    if (!response.ok || !result.ok) throw Error(result.error || result.message || 'Pika backend request failed');
    return result;
  }
  // Reads only. `takeOver` is sent solely when the caller passes it: the Host
  // then closes a live session of another owner.
  async connect({ takeOver = false } = {}) {
    const result = await this.post('connect', takeOver ? { take_over: true } : {}, { timeout: 17000 });
    if (typeof result.device === 'string' && result.device) this.gripper = { device: result.device, distribution: result.distribution ?? null };
    return result;
  }
  // `clear` is the operator's finger-clear statement; the caller obtains it for this request.
  enable() { return this.post('enable', { clear: true }); }
  // keepalive lets a disable outlive the page that sent it (STOP, pagehide).
  disable() { return this.post('disable', {}, { keepalive: true }); }
  position(value) {
    if (!Number.isFinite(value)) throw Error('Invalid Pika value');
    return this.post('position', { value });
  }
  heartbeat() { return this.post('heartbeat'); }
  disconnect() { const closing = this.post('disconnect', {}, { keepalive: true }); this.gripper = null; return closing; }
  // The session is gone (lost or taken over): address the Host's default assignment again.
  forget() { this.gripper = null; }
  async snapshot() {
    const query = new URLSearchParams();
    if (this.gripper) { query.set('pika_device', this.gripper.device); if (this.gripper.distribution !== null) query.set('pika_distribution', this.gripper.distribution); }
    const response = await this.fetcher(`/api/pika/state${this.gripper ? `?${query}` : ''}`, { cache: 'no-store', signal: AbortSignal.timeout(1000) });
    if (!response.ok) throw Error('Pika backend is unavailable');
    return response.json();
  }
}
