// Pika gripper panel. The Linux agent behind the managed Host is the only jaw
// authority (docs/target-architecture.md Q5): this page owns no serial port
// and drives the gripper solely through the Host's Pika routes. The linkage
// model follows agilexrobotics/pika_sdk gripper.py. This is independent of
// the arm's CAN command/lease path.
import { PikaClient } from './pika_remote.mjs';
import { setupCamera } from './bench.mjs';
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
if (typeof document !== 'undefined') {
  const $ = id => document.getElementById(id);
  // The page identity and operator prompt come from app.js: the Pika session
  // and the arm lease share one owner, so the Host's coordinated stop finds
  // this page's gripper.
  const page = window.piperPage;
  const client = new PikaClient({ owner: page.owner, token: page.token });
  /* Web-C/D Pika session. Every control state lives in the `session` record;
     each field below names the only code that changes it. The plain variables
     after it are data (telemetry and the last server snapshot).

     session.phase (setPhase):
       disconnected -> connecting     the operator clicks Connect
       connecting   -> read-only      the Host session is open; telemetry is read, nothing is sent
       connecting   -> disconnected   connecting fails
       read-only    -> armed          the operator clicked Enable, confirmed this gripper's
                                      prompt, and the Host accepted the enable
       armed        -> moving         a position was accepted; `target` awaits measured arrival
       moving       -> moving         a new position replaces the pending one
       moving       -> armed          measured arrival within tolerance
       armed|moving -> read-only      disarm(): disable, STOP, robot not enabled, stale
                                      telemetry, server loss or a failed request
       any          -> disconnected   the watch ends (session lost or taken over) or the
                                      operator disconnects
     There is no automatic way out of read-only or disconnected: connecting and
     powering the jaws each need an operator click in this page, so a STOP, a
     robot power change or a focused tab can never re-arm or take a gripper.
     session.target   exists only while moving (setPhase enforces it).
     session.busy     one operator operation runs at a time (run()).
     session.request  exists only while an enable or position request is in
                      flight (request()). STOP (stopPika) marks it interrupted;
                      a failed or interrupted request is followed by a disable,
                      because it may have been applied after STOP's own disable
                      or despite a lost reply. The record dies with the request,
                      so a STOP can never leak into a later command (B18).
     session.watch    identity of the running heartbeat/telemetry loop; present
                      exactly while connected (watch(), drop(), disconnect()). */
  const transitions = {
    disconnected: ['connecting'],
    connecting: ['read-only', 'disconnected'],
    'read-only': ['armed', 'disconnected'],
    armed: ['moving', 'read-only', 'disconnected'],
    moving: ['armed', 'moving', 'read-only', 'disconnected'],
  };
  const session = { phase: 'disconnected', target: null, busy: false, request: null, watch: null };
  function setPhase(next, nextTarget = null) {
    if ((next !== session.phase || next === 'moving') && !transitions[session.phase].includes(next)) throw Error(`Invalid Pika transition ${session.phase} -> ${next}`);
    if ((next === 'moving') !== !!nextTarget) throw Error('A Pika target exists only while moving');
    session.phase = next; session.target = nextTarget;
  }
  const armed = () => session.phase === 'armed' || session.phase === 'moving';
  const connected = () => session.phase !== 'disconnected' && session.phase !== 'connecting';
  const disarm = () => { if (armed()) setPhase('read-only'); };
  // Data: telemetry and the last /api/state snapshot. `host` is null until a
  // state arrives, then whether the managed Host (which has Pika routes) serves the page.
  let feedback, at = 0, simulation = true, serverAt = 0, host = null, assigned = null, configured = false, robotEnabled = false;
  const status = text => { $('pika-status').textContent = text; };
  const fresh = () => feedback && performance.now() - at < 2000;
  const allowed = () => configured && !simulation && performance.now() - serverAt < 6000;
  const gripperName = () => client.gripper?.device || assigned?.device || 'the assigned gripper';
  function render() {
    const movable = !session.busy && session.phase === 'armed' && fresh() && allowed();
    $('pika-connect').disabled = session.busy || session.phase !== 'disconnected' || !allowed();
    $('pika-disconnect').disabled = session.busy || !connected();
    $('pika-enable').disabled = session.busy || session.phase !== 'read-only' || !fresh() || !allowed() || !robotEnabled;
    // Disable is a stop: it is never blocked by an operation in flight.
    $('pika-disable').disabled = !configured;
    for (const id of ['pika-close', 'pika-open', 'pika-zero', 'pika-slider']) $(id).disabled = !movable;
    $('pika-measured').textContent = feedback && fresh() ? `${widthFromAngle(feedback.motor.Position).toFixed(2)} mm` : '— mm';
    if (fresh() && !session.busy && session.phase !== 'moving' && document.activeElement !== $('pika-slider')) $('pika-slider').value = Math.max(0, Math.min(90, widthFromAngle(feedback.motor.Position)));
    $('pika-feedback').textContent = feedback ? `${fresh() ? 'FRESH' : 'STALE'} · model opening ${widthFromAngle(feedback.motor.Position).toFixed(2)} mm\n${JSON.stringify(feedback, null, 2)}` : 'No Pika telemetry';
  }
  // Sends the jaw disable whatever this page believes about the session.
  async function sendDisable() { disarm(); await client.disable(); }
  /* One enable or position request. Returns true only when the Host accepted
     it and no STOP arrived meanwhile; the caller arms or moves only then. */
  async function request(send) {
    const record = session.request = { interrupted: false };
    let failure = null;
    try { await send(); } catch (error) { failure = error; } finally { session.request = null; }
    if (!failure && !record.interrupted) return true;
    await sendDisable().catch(() => {});
    if (failure) throw failure;
    return false;
  }
  function accept(data) {
    if (typeof data.motor?.Position !== 'number' || !Number.isFinite(data.motor.Position) || !data.motorstatus) return;
    feedback = data; at = performance.now();
    if (session.phase === 'moving') {
      const measured = widthFromAngle(data.motor.Position);
      if (Math.abs(measured - session.target.width) <= arrivalToleranceMm && Math.abs(data.motor.Speed) <= .02) {
        if (session.target.arrived === null) session.target.arrived = at;
        if (at - session.target.arrived >= 150) {
          status(`Reached ${measured.toFixed(2)} mm (target ${session.target.width.toFixed(2)} mm; within ${arrivalToleranceMm.toFixed(1)} mm).`);
          setPhase('armed');
        }
      } else session.target.arrived = null;
    }
    render();
  }
  // The session ended without the operator asking (lost, expired or taken
  // over). The disable is best effort: the Host refuses it if another owner
  // holds the gripper now, and the agent's own watchdog covers a lost link.
  function drop(message) {
    session.watch = null; setPhase('disconnected'); feedback = null; at = 0;
    client.disable().catch(() => {}); client.forget();
    status(message); render();
  }
  // Keeps the Host session alive and projects its telemetry while connected.
  async function watch() {
    const watching = session.watch = {};
    let beat = -Infinity;
    try {
      while (session.watch === watching) {
        if (performance.now() - beat > 400) { await client.heartbeat(); beat = performance.now(); }
        const snapshot = await client.snapshot();
        if (session.watch !== watching) return;
        if (snapshot.owner !== page.owner) throw Error(snapshot.owner ? 'Another session took over this Pika. Nothing more is sent from this page.' : snapshot.message || 'The Pika session ended.');
        if (!snapshot.connected) throw Error(snapshot.message || 'The Pika agent disconnected.');
        if (snapshot.fresh && snapshot.telemetry) accept(snapshot.telemetry);
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    } catch (error) { if (session.watch === watching) drop(error.message); }
  }
  async function disconnect() {
    session.watch = null;
    if (session.phase !== 'disconnected') setPhase('disconnected');
    feedback = null; at = 0;
    try { await client.disconnect(); status('Disconnected'); }
    catch (error) { status(`Disconnect was not confirmed: ${error.message}`); }
    render();
  }
  async function run(fn) {
    if (session.busy) return;
    session.busy = true; render();
    try { await fn(); }
    catch (e) {
      if (armed()) await sendDisable().catch(() => {});
      status(e.message);
    } finally { session.busy = false; render(); }
  }
  async function moveTo(width) {
    const angle = angleFromWidth(width);
    if (await request(() => client.position(angle)) && armed()) setPhase('moving', { width, started: performance.now(), arrived: null });
  }
  /* Connecting only reads. A gripper that another session owns is taken only
     after the operator confirms a prompt naming that owner, and only if that
     same owner still holds it when the request is sent. */
  const connect = async () => {
    if (!allowed()) throw Error('Pika is unavailable: it needs the Host with an assigned gripper, outside simulation.');
    const foreign = assigned?.owner && assigned.owner !== page.owner ? assigned.owner : null;
    if (foreign) {
      if (!await page.ask({ title: 'Take over the Pika gripper', confirm: 'Take over',
        text: `Gripper ${gripperName()} is controlled by another session (owner ${foreign}). Taking over closes that session and disables its jaws. Confirm now that nobody is using it.` })) { status('Not connected: the gripper stays with its current owner.'); return; }
      if ((await client.snapshot()).owner !== foreign) throw Error('Pika ownership changed while you were deciding. Nothing was taken; review the owner and connect again.');
    }
    setPhase('connecting');
    try { await client.connect({ takeOver: !!foreign }); } catch (e) { setPhase('disconnected'); throw e; }
    feedback = null; at = 0; setPhase('read-only'); watch();
    status(`Connected to ${gripperName()} through the Host; reading only. Jaw power needs your Enable.`);
  };
  $('pika-connect').onclick = () => run(connect);
  $('pika-disconnect').onclick = () => run(async () => { await sendDisable().catch(() => {}); await disconnect(); });
  // Jaw power: one explicit click, one confirmation naming this gripper, one request.
  $('pika-enable').onclick = () => run(async () => {
    const ready = () => session.phase === 'read-only' && allowed() && fresh();
    if (!ready()) throw Error('Connect and wait for fresh gripper telemetry first');
    if (!robotEnabled) throw Error('Enable the robot first: the jaws are disabled whenever the robot is not enabled.');
    if (!await page.ask({ title: 'Enable the Pika jaws', confirm: 'Enable jaws',
      text: `Confirm now: fingers and objects are clear of the jaws of ${gripperName()}. Enabling powers the jaws; it sends no position target.` })) { status('Jaws not enabled: confirmation was declined.'); return; }
    if (!ready() || !robotEnabled) throw Error('The gripper or robot changed while you were deciding; nothing was enabled.');
    if (await request(() => client.enable()) && session.phase === 'read-only') { setPhase('armed'); status('Jaws enabled. Choose a bounded 1 or 5 mm step.'); }
  });
  $('pika-disable').onclick = () => stopPika('Disabled by operator');
  for (const [id, direction] of [['pika-close', -1], ['pika-open', 1]]) $(id).onclick = () => run(async () => {
    if (session.phase === 'moving') throw Error('Wait for the current Pika step to finish');
    if (!allowed() || !fresh() || !armed() || !document.hasFocus()) throw Error('Connect and enable the jaws with fresh telemetry first');
    const step = Number($('pika-step').value || 1);
    if (![1, 5].includes(step)) throw Error('Choose a 1 or 5 mm step');
    const measured = widthFromAngle(feedback.motor.Position), width = measured + direction * step;
    if (!Number.isFinite(measured) || measured < 0 || measured > 90) throw Error('Measured opening is outside the model range; no movement sent');
    await moveTo(width);
    status(`Requested ${width.toFixed(2)} mm; waiting for measured arrival.`);
  });
  $('pika-zero').onclick = () => run(async () => {
    if (!armed() || !allowed() || !fresh()) throw Error('Enable the gripper first');
    const deadline = performance.now() + 120000;
    while (armed() && fresh() && allowed() && performance.now() < deadline) {
      const measured = widthFromAngle(feedback.motor.Position);
      if (measured <= arrivalToleranceMm) { status(`Closed to ${measured.toFixed(2)} mm.`); return; }
      await moveTo(Math.max(0, measured - 5));
      while (session.phase === 'moving' && allowed()) await new Promise(resolve => setTimeout(resolve, 50));
    }
    if (armed()) await sendDisable();
  });
  $('pika-slider').oninput = () => { $('pika-slider-target').textContent = `Target ${Number($('pika-slider').value).toFixed(1)} mm · release to move`; };
  $('pika-slider').onchange = () => {
    const goal = Number($('pika-slider').value);
    return run(async () => {
      if (!Number.isFinite(goal) || goal < 0 || goal > 90) throw Error('Pika opening must be 0–90 mm');
      if (!armed() || !allowed() || !fresh() || !document.hasFocus()) throw Error('Enable the gripper with fresh telemetry first');
      await moveTo(goal);
      status(`Requested opening ${goal.toFixed(1)} mm; waiting for measured arrival.`);
    });
  };
  $('pika-expand').onclick = () => {
    const expanded = $('pika-details').hidden;
    $('pika-details').hidden = !expanded;
    $('pika-row').classList.toggle('expanded', expanded);
    $('pika-expand').setAttribute('aria-expanded', String(expanded));
    $('pika-expand').textContent = expanded ? 'Less ▴' : 'Details ▾';
  };
  /* Every stop of the jaws. It always sends the disable: what this page
     believes (not armed, not connected, an enable that seemed to fail) never
     suppresses it, because an enable whose reply was lost may have powered the
     jaws. Only a server known to have no Pika routes is skipped. The Host
     refuses a disable for a gripper this page does not own, which is the
     correct outcome and is not reported as an error when not connected. */
  function stopPika(reason = 'Operator stop') {
    disarm();
    if (session.request) session.request.interrupted = true;
    render();
    if (host === false) return Promise.resolve();
    return client.disable().then(
      () => { status(`${reason}; jaw disable sent.`); },
      error => { if (connected()) status(`${reason}; the jaw disable was not confirmed: ${error.message}`); });
  }
  // app.js raises piper-stop for the STOP button and Escape.
  window.addEventListener('piper-stop', () => stopPika());
  window.addEventListener('piper-robot-disable', () => stopPika('Robot disabled'));
  // A bounded position step is not the arm's continuous control lease.
  // Browser chrome/tool focus changes must not cut it off mid-step. Hidden
  // pages, lost telemetry, STOP and server loss still request disable. A
  // hidden page disconnects only after its disable was answered.
  document.addEventListener('visibilitychange', () => { if (document.hidden) stopPika('Dashboard hidden').then(() => { if (connected() && !session.busy) run(disconnect); }); });
  window.addEventListener('pagehide', () => { stopPika(); if (connected()) client.disconnect().catch(() => {}); });
  setInterval(() => {
    if (armed() && !fresh()) stopPika('Pika telemetry stale');
    else if (armed() && !allowed()) stopPika('Controller server unavailable');
    else if (session.phase === 'moving' && performance.now() - session.target.started > 6000) stopPika('Pika step timed out');
    render();
  }, 100);
  // app.js polls /api/state and dispatches each result here (null when a poll fails).
  window.addEventListener('piper-state', ({ detail: s }) => {
    if (!s) { simulation = true; render(); return; }
    simulation = s.simulated !== false; serverAt = performance.now();
    host = typeof s.backend === 'string'; assigned = s.pika || null;
    configured = !!assigned?.configured && ['linux-serial', 'wsl-linux-serial'].includes(assigned.transport);
    const robot = s.devices?.find(d => d.id === s.selected);
    robotEnabled = !!robot?.fresh && !robot.recovery_required && !robot.status?.arm_status && !robot.status?.error && !!robot.joints?.every(j => j.enabled === true);
    // Robot power only ever takes jaw power away. It never gives it back.
    if (!robotEnabled && armed()) stopPika('Robot is not enabled');
    $('pika-transport-description').textContent = !host ? 'The Pika gripper is driven only through the managed Host. This standalone server does not control it.' :
      simulation ? 'Pika hardware is unavailable in simulation.' :
      configured ? 'Separate from the standard CAN gripper. The Host drives the Pika through its Linux serial agent, the only jaw authority. Connecting reads telemetry only; jaw power needs your Enable.' :
      'No Pika gripper is assigned to this robot. Assign one in the Host configuration.';
    render();
  });
  render();

  setupCamera({key:'piper.camera.pika.v1',videoId:'pika-video',deviceId:'pika-camera-device',listId:'pika-camera-list',startId:'pika-camera-start',stopId:'pika-camera-stop',statusId:'pika-camera-status',preferred:'DECXIN',width:640,height:480});
}
