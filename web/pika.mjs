// Pika USB serial codec follows agilexrobotics/pika_sdk serial_comm.py and
// gripper.py. This is independent of the arm's CAN command/lease path.
import { RemotePikaPort } from './pika_remote.mjs';
import { setupCamera } from './bench.mjs';
export function packet(command, value = 0) {
  if (![10, 11, 22].includes(command) || !Number.isFinite(value)) throw Error('Invalid Pika command');
  const bytes = new Uint8Array(7);
  bytes[0] = command; new DataView(bytes.buffer).setFloat32(1, value, true);
  bytes[5] = 13; bytes[6] = 10; return bytes;
}
export const arrivalToleranceMm = 1.0;
const maxAngle = (180 - 43.99) * Math.PI / 180;
function linkage(angle) {
  const a = maxAngle - angle, h = .0325 * Math.sin(a);
  return (Math.sqrt(.058 ** 2 - (h - .01456) ** 2) + .0325 * Math.cos(a)) * 1000;
}
export function widthFromAngle(angle) { return 2 * (linkage(angle) - linkage(0)); }
export function angleFromWidth(width) {
  if (!Number.isFinite(width) || width < 0 || width > Math.min(90, widthFromAngle(maxAngle))) throw Error('Pika width is outside 0–90 mm');
  let lo = 0, hi = maxAngle;
  for (let n = 0; n < 40; n++) { const mid = (lo + hi) / 2; if (widthFromAngle(mid) < width) lo = mid; else hi = mid; }
  return (lo + hi) / 2;
}
export class JsonStream {
  buffer = '';
  push(text) {
    this.buffer += text;
    const out = [];
    while (true) {
      const start = this.buffer.indexOf('{');
      if (start < 0) { this.buffer = ''; break; }
      this.buffer = this.buffer.slice(start);
      let depth = 0, quoted = false, escape = false, end = -1;
      for (let i = 0; i < this.buffer.length; i++) {
        const ch = this.buffer[i];
        if (quoted) { if (escape) escape = false; else if (ch === '\\') escape = true; else if (ch === '"') quoted = false; }
        else if (ch === '"') quoted = true;
        else if (ch === '{') depth++;
        else if (ch === '}' && --depth === 0) { end = i + 1; break; }
      }
      if (end < 0) { if (this.buffer.length > 16384) this.buffer = ''; break; }
      const raw = this.buffer.slice(0, end); this.buffer = this.buffer.slice(end);
      // Vendor firmware emits trailing commas. Only remove them outside strings.
      let cleaned = '', inString = false, escaped = false;
      for (let i = 0; i < raw.length; i++) {
        const ch = raw[i];
        if (!inString && ch === ',' && /^\s*[}\]]/.test(raw.slice(i + 1))) continue;
        cleaned += ch;
        if (inString) { if (escaped) escaped = false; else if (ch === '\\') escaped = true; else if (ch === '"') inString = false; }
        else if (ch === '"') inString = true;
      }
      try { out.push(JSON.parse(cleaned)); } catch { /* Discard a malformed complete frame; await fresh telemetry. */ }
    }
    return out;
  }
}

if (typeof document !== 'undefined') {
  const $ = id => document.getElementById(id);
  let port, reader, reading, writer, busy = false, armed = false, feedback, at = 0, stream, simulation = true, serverAt = 0, stopPending = false, pending = null;
  let consent = false, autoConnect = true, robotEnabled = false, powerStopped = false;
  const confirmControl = async () => { consent = typeof window.confirmPiperControl === 'function' ? await window.confirmPiperControl() : window.confirm("Make sure it's safe to move the gripper."); return consent; };
  let backendSerial = false, backendDisabled = /Win/i.test(navigator.platform || ''), apiToken = '';
  const status = text => { $('pika-status').textContent = text; };
  const fresh = () => feedback && performance.now() - at < 2000;
  const allowed = () => !simulation && performance.now() - serverAt < 6000;
  function render() {
    $('pika-connect').disabled = busy || !!port || !allowed() || (!backendSerial && (backendDisabled || !navigator.serial));
    $('pika-disconnect').disabled = busy || !port;
    $('pika-enable').disabled = busy || !port || !fresh() || !allowed();
    $('pika-disable').disabled = busy || !port;
    for (const id of ['pika-close', 'pika-open']) $(id).disabled = busy || !!pending || !armed || !fresh() || !allowed();
    if ($('pika-measured')) $('pika-measured').textContent = feedback && fresh() ? `${widthFromAngle(feedback.motor.Position).toFixed(2)} mm` : '— mm';
    if ($('pika-zero')) $('pika-zero').disabled = busy || !!pending || !armed || !fresh() || !allowed();
    if ($('pika-slider')) {
      $('pika-slider').disabled = busy || !!pending || !armed || !fresh() || !allowed();
      if (fresh() && !busy && !pending && document.activeElement !== $('pika-slider')) $('pika-slider').value = Math.max(0, Math.min(90, widthFromAngle(feedback.motor.Position)));
    }
    $('pika-feedback').textContent = feedback ? `${fresh() ? 'FRESH' : 'STALE'} · model opening ${widthFromAngle(feedback.motor.Position).toFixed(2)} mm\n${JSON.stringify(feedback, null, 2)}` : 'No Pika telemetry';
  }
  async function write(command, value) {
    if (!port?.writable) throw Error('Pika serial is not connected');
    writer = port.writable.getWriter();
    try { await writer.write(packet(command, value)); } finally { writer.releaseLock(); writer = null; }
    if (stopPending && command !== 10) { stopPending = false; await disable(); }
  }
  async function disable() {
    armed = false; pending = null;
    if (port?.writable && !writer) await write(10);
    status('Disable sent; check raw feedback. Serial writes are not acknowledgements.');
  }
  async function disconnect() {
    armed = false;
    if (reader) await reader.cancel().catch(() => {});
    if (reading) await reading;
    if (port) await port.close().catch(() => {});
    port = null; feedback = null; at = 0; consent = false;
    status('Disconnected'); render();
  }
  async function readLoop() {
    const parser = new JsonStream(), decoder = new TextDecoder();
    reader = port.readable.getReader();
    try {
      while (true) {
        const { value, done } = await reader.read(); if (done) break;
        for (const data of parser.push(decoder.decode(value, { stream: true }))) {
          if (typeof data.motor?.Position === 'number' && Number.isFinite(data.motor.Position) && data.motorstatus) {
            feedback = data; at = performance.now();
            if (pending) {
              const measured = widthFromAngle(data.motor.Position);
              if (Math.abs(measured - pending.target) <= arrivalToleranceMm && Math.abs(data.motor.Speed) <= .02) {
                if (pending.arrived === null) pending.arrived = at;
                if (at - pending.arrived >= 150) {
                  status(`Reached ${measured.toFixed(2)} mm (target ${pending.target.toFixed(2)} mm; within ${arrivalToleranceMm.toFixed(1)} mm).`);
                  pending = null;
                }
              } else pending.arrived = null;
            }
            render();
          }
        }
      }
    } catch (e) { status(`Serial read stopped: ${e.message}`); }
    finally { reader.releaseLock(); reader = null; armed = false; pending = null; at = 0; const ended = port; port = null; if (ended) await ended.close().catch(() => {}); render(); }
  }
  async function run(fn) {
    if (busy) return;
    busy = true; render();
    try { await fn(); }
    catch (e) {
      const wasArmed = armed; armed = false;
      if (wasArmed) await disable().catch(() => {});
      status(e.message);
    } finally { busy = false; render(); }
  }
  const connect = async () => {
    if (!allowed()) throw Error('Pika hardware is unavailable in simulation or while the C server is offline');
    if (backendDisabled && !backendSerial) throw Error('Windows WCH serial is disabled after repeated driver crashes. Configure the Linux USB transport.');
    let chosen;
    if (backendSerial) chosen = await RemotePikaPort.connect({ token: () => apiToken, clear: () => consent });
    else {
      const granted = navigator.serial.getPorts ? (await navigator.serial.getPorts()).filter(p => p.getInfo().usbVendorId === 0x1a86) : [];
      chosen = granted.length === 1 ? granted[0] : await navigator.serial.requestPort({ filters: [{ usbVendorId: 0x1a86 }] });
      await chosen.open({ baudRate: 460800, dataBits: 8, stopBits: 1, parity: 'none', flowControl: 'none' });
    }
    port = chosen; feedback = null; armed = false; powerStopped = false; at = 0; reading = readLoop();
    status(`Connected ${backendSerial ? 'through the Linux serial driver' : 'at 460800 baud'}; reading only. Waiting for Pika motor telemetry.`);
  };
  $('pika-connect').onclick = () => { autoConnect = true; return run(connect); };
  $('pika-disconnect').onclick = () => { autoConnect = false; return run(async () => { await disable(); await disconnect(); }); };
  $('pika-enable').onclick = () => run(async () => {
    if (!allowed() || !fresh()) throw Error('Fresh gripper telemetry is required');
    if (!await confirmControl()) return;
    await write(11); armed = true; status('Enable sent (not acknowledged). Choose a bounded 1 or 5 mm step.');
  });
  $('pika-disable').onclick = () => run(disable);
  for (const [id, direction] of [['pika-close', -1], ['pika-open', 1]]) $(id).onclick = () => run(async () => {
    if (pending) throw Error('Wait for the current Pika step to finish');
    if (!allowed() || !fresh() || !armed || !document.hasFocus()) throw Error('Reconnect/enable with fresh telemetry and finger-clear confirmation first');
    const step = Number($('pika-step').value || 1);
    if (![1, 5].includes(step)) throw Error('Choose a 1 or 5 mm step');
    const measured = widthFromAngle(feedback.motor.Position), target = measured + direction * step;
    if (!Number.isFinite(measured) || measured < 0 || measured > 90) throw Error('Measured opening is outside the model range; no movement sent');
    await write(22, angleFromWidth(target));
    pending = { target, started: performance.now(), arrived: null };
    status(`Requested ${target.toFixed(2)} mm; waiting for measured arrival.`);
  });

  if ($('pika-zero')) $('pika-zero').onclick = () => run(async () => {
    if (!armed || !allowed() || !fresh()) throw Error('Enable the gripper first');
    const deadline = performance.now() + 120000;
    while (armed && fresh() && allowed() && performance.now() < deadline) {
      const measured = widthFromAngle(feedback.motor.Position);
      if (measured <= arrivalToleranceMm) { status(`Closed to ${measured.toFixed(2)} mm.`); return; }
      const target = Math.max(0, measured - 5);
      await write(22, angleFromWidth(target)); pending = { target, started: performance.now(), arrived: null };
      while (pending && armed && allowed()) await new Promise(resolve => setTimeout(resolve, 50));
    }
    if (armed) await disable();
  });
  setInterval(() => { if (autoConnect && backendSerial && allowed() && !port && !busy && !document.hidden && document.hasFocus()) run(connect); }, 2500);
  window.addEventListener('focus', () => { if (autoConnect && backendSerial && allowed() && !port && !busy && !document.hidden && document.hasFocus()) run(connect); });
  if ($('pika-slider')) {
    $('pika-slider').oninput = () => { $('pika-slider-target').textContent = `Target ${Number($('pika-slider').value).toFixed(1)} mm · release to move`; };
    $('pika-slider').onchange = () => {
      const goal = Number($('pika-slider').value);
      return run(async () => {
        if (!Number.isFinite(goal) || goal < 0 || goal > 90) throw Error('Pika opening must be 0–90 mm');
        if (!armed || !allowed() || !fresh() || !document.hasFocus()) throw Error('Enable the gripper with fresh telemetry first');
        await write(22, angleFromWidth(goal));
        pending = { target:goal, started:performance.now(), arrived:null };
        status(`Requested opening ${goal.toFixed(1)} mm; waiting for measured arrival.`);
      });
    };
  }
  if ($('pika-expand')) $('pika-expand').onclick = () => {
    const expanded = $('pika-details').hidden;
    $('pika-details').hidden = !expanded;
    $('pika-row').classList.toggle('expanded', expanded);
    $('pika-expand').setAttribute('aria-expanded', String(expanded));
    $('pika-expand').textContent = expanded ? 'Less ▴' : 'Details ▾';
  };
  const stopPika = (reason = 'Operator stop') => {
    powerStopped = true;
    if (!armed && !busy) return;
    armed = false;
    if (writer) stopPending = true;
    else if (port) disable().then(() => status(`${reason}; disable sent.`)).catch(e => status(`Disable could not be sent: ${e.message}`));
  };
  $('stop').addEventListener('click', () => stopPika());
  window.addEventListener('piper-robot-disable', () => stopPika('Robot disabled'));
  document.addEventListener('keydown', e => { if (e.key === 'Escape') stopPika(); });
  // A bounded serial position step is not the arm's continuous control lease.
  // Browser chrome/tool focus changes must not cut it off mid-step. Hidden
  // pages, lost telemetry, STOP and server loss still request disable.
  document.addEventListener('visibilitychange', () => { if (document.hidden) { stopPika('Dashboard hidden'); if (port && !busy) run(disconnect); } });
  setInterval(() => {
    if (armed && !fresh()) stopPika('Pika telemetry stale');
    else if (armed && !allowed()) stopPika('Controller server unavailable');
    else if (pending && performance.now() - pending.started > 6000) stopPika('Pika step timed out');
    render();
  }, 100);
  async function serverMode() {
    try {
      const r = await fetch('/api/state', { cache: 'no-store', signal: AbortSignal.timeout(1000) }); if (!r.ok) throw Error();
      const s = await r.json(); simulation = s.simulated !== false; serverAt = performance.now(); apiToken = s.token || '';
      backendSerial = s.pika?.configured && ['linux-serial', 'wsl-linux-serial'].includes(s.pika.transport);
      const robot = s.devices?.find(d => d.id === s.selected);
      robotEnabled = !!robot?.fresh && !robot.recovery_required && !robot.status?.arm_status && !robot.status?.error && robot.joints?.every(j => j.enabled === true);
      if (!robotEnabled) powerStopped = false;
      if (backendSerial && port && !busy && fresh()) {
        if (robotEnabled && !armed && !powerStopped && !document.hidden && document.hasFocus()) {
          // Robot enable is the shared power action. This sends no position target.
          run(async () => { consent = true; await write(11); armed = true; status('Enabled with robot · ready'); });
        } else if (!robotEnabled && armed) run(disable);
      }
      backendDisabled = s.pika?.transport === 'disabled' || (!backendSerial && /Win/i.test(navigator.platform || ''));
      if ($('pika-transport-description')) $('pika-transport-description').textContent = backendSerial ?
        'Separate from the standard CAN gripper. The C# backend uses the Linux USB serial driver; Windows WCH COM is bypassed. Connecting reads telemetry only.' :
        backendDisabled ? 'Pika serial is disabled until the Linux USB transport is configured. The Windows WCH driver crashed in both tested versions.' :
        'Separate from the standard CAN gripper. Select the Pika CH340 serial device. Connecting reads telemetry only.';
    }
    catch { simulation = true; }
    render(); setTimeout(serverMode, 1000);
  }
  if (!navigator.serial) status('Use desktop Chrome or Edge for Web Serial. No serial connection attempted.');
  serverMode();

  setupCamera({key:'piper.camera.pika.v1',videoId:'pika-video',deviceId:'pika-camera-device',listId:'pika-camera-list',startId:'pika-camera-start',stopId:'pika-camera-stop',statusId:'pika-camera-status',preferred:'DECXIN',width:640,height:480});
  window.addEventListener('pagehide', () => stopPika());
}
