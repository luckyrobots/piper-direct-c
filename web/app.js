import { createApi, reply } from './api.mjs';

const $ = id => document.getElementById(id);

const names = ['Base', 'Shoulder', 'Elbow', 'Wrist roll', 'Wrist pitch', 'Tool roll'];

const owner = crypto.randomUUID();

/* The server identity last seen by poll(): its POST token and whether the

   managed Host (rather than the standalone C daemon) serves this page. It is

   kept across failed polls so STOP can still be delivered when `state` is stale. */

let server = { token: '', host: false };

function identify(next) { server = { token: next.token || '', host: typeof next.backend === 'string' }; return server; }

// Every request this page sends goes through the generated client (web/api.mjs).

const api = createApi({ token: () => server.token, fetcher: (route, init) => fetch(route, init) });

let state, device, selectedJoint = 1, lastPoll = 0, selectedId = '';

let toastTimer;

const cards = [];

/* Operator prompt. One modal dialog asks one question at a time. ask()

   resolves true only when the operator presses the confirm button of that

   question; cancelling, dismissing, or STOP resolves false. A question asked

   while another is open is refused (false): nothing queues behind the operator.

   The prompt is not a modal dialog: a modal makes the header inert, and STOP

   must stay clickable. Only the workspace below the header is made inert. */

let prompting = null;

function ask({ title, text, confirm }) {

  if (prompting) return Promise.resolve(false);

  return new Promise(resolve => {

    const dialog = $('safety-prompt'), workspace = $('shell');

    $('safety-title').textContent = title; $('safety-text').textContent = text; $('safety-continue').textContent = confirm;

    $('safety-continue').disabled = false; $('safety-cancel').disabled = false;

    const finish = answer => { if (prompting !== finish) return; prompting = null; workspace.inert = false; dialog.close(); resolve(answer); };

    prompting = finish;

    $('safety-continue').onclick = () => finish(true);

    $('safety-cancel').onclick = () => finish(false);

    workspace.inert = true; dialog.show(); $('safety-cancel').focus();

  });

}

/* Web-B motion approval. One record, changed only by setApproval().

     unapproved -> prompting   a motion command needs approval (approveMotion)

     prompting  -> approved    the operator confirms the motion statement

     prompting  -> unapproved  the operator cancels, or STOP dismisses the prompt

     approved   -> unapproved  the selected adapter or server token changes (revokeApproval)

   `prompt` is the shared answer promise, present only while prompting. A revoke

   during prompting leaves the open prompt to decide. Approval covers exactly

   the two statements in motionStatement and nothing else: power and recovery

   actions are confirmed one by one (see attestations). */

const motionStatement = Object.freeze({ title: 'Make sure it is safe', text: 'Confirm for the motion commands of this session: people and objects are clear of the arm and gripper, and this dashboard is the only controller commanding the arm. Motor power and recovery actions ask again each time.', confirm: 'Enable controls' });

const motionFlags = Object.freeze({ standalone: true, clear: true });

let approval = { phase: 'unapproved', prompt: null };

function setApproval(phase, prompt = null) { approval = { phase, prompt }; }

function revokeApproval() { if (approval.phase === 'approved') setApproval('unapproved'); }

function approveMotion() {

  if (approval.phase === 'approved') return Promise.resolve(true);

  if (approval.phase === 'prompting') return approval.prompt;

  const prompt = ask(motionStatement).then(answer => { setApproval(answer ? 'approved' : 'unapproved'); return answer; });

  setApproval('prompting', prompt);

  return prompt;

}

// The page identity and prompt shared with pika.mjs: one owner for the arm lease and the Pika session.

window.piperPage = Object.freeze({ owner, ask, token: () => server.token });

function showError(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { $('toast').hidden = true; }, 7000); }

function fixed(value, digits = 3) { return Number.isFinite(value) ? value.toFixed(digits) : '—'; }

function chooseJoint(index) { selectedJoint = index; cards.forEach((c, i) => c.element.classList.toggle('selected', i + 1 === index)); $('active-joint').textContent = `J${index} SELECTED`; }

/* Server attestations (P1). src/controller.c reads `standalone` on every

   /api/command, `supported` on power and recovery actions, `clear` on enable,

   reset, verify-current and motion, and `reset_completed` on verify-reset.

   teach-return also reads `button_off`, plus `same_pose_confirmed` (measured-pose

   firmware) or `zero_pose_confirmed` (older firmware), so it has one statement

   per strategy; `action` names the command a statement sends when it differs.

   A flag is sent only when the operator has confirmed the statement it stands

   for: each power or recovery action below asks its own question every time,

   and motion commands carry motionFlags under the session's motion approval.

   Nothing here is implied by another action or added without a prompt. */

const attestations = Object.freeze({

  enable: { flags: { standalone: true, clear: true, supported: true }, title: 'Enable motor power', confirm: 'Enable motors',

    text: 'Confirm now: the arm is supported, people and objects are clear of it, and this dashboard is the only controller commanding it. Enabling sends no position target.' },

  disable: { flags: { standalone: true, supported: true }, title: 'Disable motor power', confirm: 'Disable motors',

    text: 'Confirm now: the arm is supported, because disabling removes holding torque and the arm can fall, and this dashboard is the only controller commanding it.' },

  reset: { flags: { standalone: true, clear: true, supported: true }, title: 'Reset the controller', confirm: 'Reset controller',

    text: 'Confirm now: the arm is supported and stationary, people and objects are clear of it, and this dashboard is the only controller commanding it. A reset disables the motors and can remove holding torque.' },

  'verify-current': { flags: { standalone: true, clear: true, supported: true }, title: 'Clear the saved stop', confirm: 'Clear saved stop',

    text: 'Confirm now: you inspected the arm after the stop, it is supported and stationary with its motors enabled, people and objects are clear of it, and this dashboard is the only controller commanding it. This clears the recovery latch.' },

  'verify-disabled': { flags: { standalone: true, supported: true }, title: 'Clear the saved stop', confirm: 'Clear saved stop',

    text: 'Confirm now: you inspected the arm after the stop, it is supported and stationary with its motors disabled, and this dashboard is the only controller commanding it. This clears the recovery latch.' },

  'verify-reset': { flags: { standalone: true, supported: true, reset_completed: true }, title: 'Verify the completed reset', confirm: 'Reset completed',

    text: 'Confirm now: the controller reset or power cycle has completed, the arm is supported and stationary, and this dashboard is the only controller commanding it. This clears the recovery latch.' },

  'teach-return': { flags: { standalone: true, clear: true, supported: true, button_off: true, same_pose_confirmed: true }, title: 'Return to CAN control', confirm: 'Return to CAN',

    text: 'Confirm now: the teach button light is off, the arm is supported and stationary at the pose it holds now, people and objects are clear of it, and this dashboard is the only controller commanding it. The arm holds this measured pose; no earlier target resumes, and no homing or motor-power change runs.' },

  'teach-return-zero': { action: 'teach-return', flags: { standalone: true, clear: true, supported: true, button_off: true, zero_pose_confirmed: true }, title: 'Return to CAN control', confirm: 'Return to CAN',

    text: 'Confirm now: the arm is at its physical zero pose, the teach button light is off, the arm is supported during the standby/CAN change, people and objects are clear of it, and this dashboard is the only controller commanding it. No homing, reset or position target is sent.' },

});

// The selected arm as this page last saw it, and this page as the requester.

function arm() { return { device_id: device?.id, generation: device?.generation, owner }; }

// Sends one arm control and answers its result; a refusal is thrown.

async function send(operation, fields) {

  if (!state) throw Error('The controller server is unavailable.');

  return reply(await operation(fields, { timeout: 1500 }));

}

/* Web-A control lease. One {phase, request_id} record, changed only by setLease().

     none     -> pending   perform() posts a request

     leased   -> pending   another request (e.g. hold) is posted; request_id is kept

     pending  -> leased    the reply carries a request_id, or one was already held

     pending  -> none      the reply has none and none was held, or the request fails

     any      -> stopping  STOP is posted; heartbeats stop and in-flight replies are ignored

     stopping -> none      the STOP reply or failure arrives

     any      -> none      server token change, completed/foreign motion, heartbeat

                           failure, or leaving the page

   Only one non-STOP request is in flight (`pending` rejects others). A reply

   applies only if the lease is still the record its request created, so a reply

   that arrives after STOP, leaving or a token change cannot re-create a lease.

   Such a late reply may still name a request the server admitted; release()

   stops exactly that request, so no motion outlives the page that left it. */

const noLease = Object.freeze({ phase: 'none', request_id: 0 });

let lease = noLease;

function setLease(phase, request_id = 0) { lease = phase === 'none' ? noLease : Object.freeze({ phase, request_id }); return lease; }

// Automatic cleanup stop, scoped to one request this page created and no longer holds.

function release(request_id, device_id) {

  if (!server.token) return;

  api.stop({ request_id, owner, device_id }, { keepalive: true }).catch(() => {});

}

async function perform(operation, fields = arm()) {

  if (lease.phase === 'pending' || lease.phase === 'stopping') return;

  const attempt = setLease('pending', lease.request_id), sentTo = device?.id;

  try {

    const result = await send(operation, fields);

    if (lease === attempt) setLease(result.request_id || attempt.request_id ? 'leased' : 'none', result.request_id || attempt.request_id);

    else if (result.request_id && result.request_id !== lease.request_id) release(result.request_id, sentTo);

    $('message').textContent = result.message;

    return result;

  } catch (error) { if (lease === attempt) setLease(attempt.request_id ? 'leased' : 'none', attempt.request_id); showError(error.message); }

}

/* Operator STOP (the STOP button and Escape). It stops everything this server

   controls: every arm and the gripper. The request names no device, generation

   or request, so no client-side belief can make the server refuse or narrow it,

   and it is sent even when the last state poll failed. The Host is asked for

   its coordinated stop (arms and gripper hold, attempted independently); the

   standalone C daemon for its global arm stop. pika.mjs hears `piper-stop` and

   always sends the jaw disable itself. An open operator prompt is refused. */

async function sendStop(identity) {

  const response = await (identity.host ? api.coordinatedStop : api.stop)({ owner }, { token: identity.token, keepalive: true, timeout: identity.host ? 5000 : 1500 });

  return { status: response.status, ok: response.ok, result: await response.json().catch(() => ({})) };

}

async function readIdentity() {

  const response = await api.state({}, { timeout: 1000 });

  if (!response.ok) throw Error('The controller server did not answer.');

  return identify(await response.json());

}

function stopOutcome({ ok, result }) {

  const arm = result.arm || result;

  let message = arm.message || arm.error || result.error || 'The server refused STOP.';

  try { message = JSON.parse(message).message || message; } catch { /* A plain sentence. */ }

  if (result.arm ? !arm.ok : !ok || result.ok === false) throw Error(message);

  return result.pika && !result.pika.ok ? `${message} Gripper hold: ${result.pika.message}` : message;

}

async function stopAll() {

  const stopping = setLease('stopping');

  prompting?.(false);

  window.dispatchEvent(new Event('piper-stop'));

  try {

    // A known token is used at once. A refused or failed delivery is retried

    // once against the server's current identity (it may have restarted).

    let stopped = server.token ? await sendStop(server).catch(() => null) : null;

    if (!stopped || stopped.status === 403 || stopped.status === 404) stopped = await sendStop(await readIdentity());

    $('message').textContent = stopOutcome(stopped);

  } catch (error) { showError(`STOP was not confirmed: ${error.message} Use the physical stop if motion continues.`); }

  finally { if (lease === stopping) setLease('none'); }

}

async function command(action, fields = {}) {

  const statement = attestations[action];

  if (!await (statement ? ask(statement) : approveMotion())) return;

  return perform(api.command, { ...arm(), speed_percent: Number($('speed').value), tolerance_deg: Number($('tolerance').value), ...(statement ? statement.flags : motionFlags), action: statement?.action ?? action, ...fields });

}

function jog(index, sign) {

  const step = Number($('step').value);

  if (!Number.isFinite(step) || step <= 0 || step > 5) return showError('Jog step must be greater than 0 and at most 5°.');

  if (!canMove()) return showError(device?.blocker || 'Enable motors first.');

  command('jog', { joint: index, degrees: sign * step });

}

for (let i = 1; i <= 6; i++) {

  const element = document.createElement('section'); element.className = 'joint';

  element.innerHTML = `<div class="joint-top"><h3><b>J${i}</b>${names[i - 1]}</h3><span class="joint-state">NO DATA</span></div><div class="readout"><strong>—</strong><span>measured</span></div><div class="target"><label for="target-${i}" class="sr-only">Joint ${i} target degrees</label><input id="target-${i}" type="number" step="0.1" placeholder="Target °" aria-label="Joint ${i} target degrees"><button class="move" aria-label="Move joint ${i}">Move</button></div><div class="range-labels"><span>Limits unknown</span><span>—</span></div><div class="telemetry">Waiting for motor feedback</div><div class="jog-row"><button class="minus" aria-label="Jog joint ${i} negative">−</button><input class="slider" type="range" min="-180" max="180" step="0.001" value="0" disabled aria-label="Joint ${i} target angle" title="Release to move to target angle"><button class="plus" aria-label="Jog joint ${i} positive">+</button><button class="zero" aria-label="Zero joint ${i}">Zero</button></div>`;

  $('joints').append(element);

  const card = { element, input: element.querySelector('input[type=number]'), slider: element.querySelector('.slider'), touched: false };

  element.addEventListener('pointerdown', () => chooseJoint(i));

  card.input.addEventListener('input', () => { card.touched = true; if (card.input.value !== '') card.slider.value = card.input.value; });

  card.slider.addEventListener('input', () => { card.touched = true; card.input.value = Number(card.slider.value).toFixed(3); });

  card.slider.addEventListener('change', async () => {

    if (!canMove()) return showError(device?.blocker || 'Enable motors first.');

    const degrees = Number(card.slider.value);

    const joint = device?.joints[i - 1];

    if (!joint?.limits_fresh || !Number.isFinite(degrees) || degrees < joint.min_deg || degrees > joint.max_deg) return showError('Fresh joint limits are required.');

    await command('joint', { joint: i, degrees });

    card.touched = false;

  });

  element.querySelector('.move').onclick = () => { if (card.input.value === '' || !Number.isFinite(Number(card.input.value))) return showError('Enter a target angle first.'); command('joint', { joint: i, degrees: Number(card.input.value) }); };

  element.querySelector('.minus').onclick = () => jog(i, -1);

  element.querySelector('.plus').onclick = () => jog(i, 1);

  element.querySelector('.zero').onclick = () => command('zero', { joint: i });

  cards.push(card);

}

chooseJoint(1);

function idle() { return !!state && !state.motion.active && !state.query_active; }

function canAct() { return idle() && !!device && !device.blocker && device.joints.every(j => j.enabled === true) && lastPoll > performance.now() - 600; }

function canMove() { return canAct() && device.range_entry_deg === 0; }

function render() {

  if (!state) return;

  device = state.devices.find(d => d.id === state.selected);

  if (selectedId !== state.selected) { selectedId = state.selected; cards.forEach(c => { c.touched = false; c.input.value = ''; }); revokeApproval(); }

  const options = state.devices.map(d => `${d.id}|${d.name}|${d.interface}|${d.present}`).join(';');

  if ($('device').dataset.options !== options) {

    $('device').replaceChildren(...state.devices.map(d => new Option(`${d.name || d.id}${d.interface ? ` · ${d.interface}` : ''}${d.present ? '' : ' · missing'}`, d.id)));

    if (!state.devices.length) $('device').add(new Option('No adapters detected', ''));

    $('device').dataset.options = options;

  }

  $('device').value = state.selected; $('device').disabled = !idle();

  $('mode').textContent = state.simulated ? 'SIMULATOR' : 'HARDWARE'; $('simulation').hidden = !state.simulated;

  $('connection').textContent = !device ? 'No USB-CAN adapter detected.' : device.fresh ? 'Live robot feedback' : device.present ? 'Adapter detected · no fresh robot data' : 'Adapter disconnected';

  $('connection').className = `connection ${device?.fresh ? 'good' : 'bad'}`;

  $('firmware').textContent = device?.firmware || 'Unknown'; $('hardware').textContent = device?.hardware || 'Unknown';

  $('control-state').textContent = device?.fresh ? (['Standby', 'Computer control', 'Teaching'][device.status.control] ?? `Mode ${device.status.control}`) : '—';

  $('teaching-state').textContent = device?.fresh ? (['Off', 'Recording', 'Recording ended', 'Playing', 'Paused', 'Resuming', 'Playback ended', 'Going to start'][device.status.teach_state] ?? 'Unknown') : '—';

  $('rx').textContent = device?.rx_frames ?? '—'; $('tx').textContent = device?.tx_frames ?? '—';

  const allEnabled = device?.joints.every(j => j.enabled === true);

  const allDisabled = device?.joints.every(j => j.enabled === false);

  const approved = approval.phase === 'approved';

  const outside = device?.range_entry_deg > 0;

  let blocker = device?.blocker || (!device ? 'Connect a USB-CAN adapter to begin.' : !allEnabled ? (approved ? 'Ready to enable motors. Enabling sends no joint-position target.' : 'Motors are off. Click Enable motors to begin.') : outside ? `Joint controls blocked: ${device.joints.filter(j => j.actual_deg < j.min_deg || j.actual_deg > j.max_deg).map(j => `J${j.index} ${fixed(j.actual_deg)}° outside ${j.min_deg}…${j.max_deg}°`).join('; ')}. Clear the path, then use Move into range.` : !approved ? 'Ready to move.' : 'Ready to move. Choose a joint and enter an angle, or use − / + step.');

  if (device?.recovery_required && !device.reset_pending) blocker = `A stop is saved for this arm. ${allDisabled || allEnabled ? 'Inspect the arm, then use Verify recovery to clear it' : 'Wait for consistent motor feedback, then verify recovery'}${allDisabled ? ' before enabling.' : '.'}`;

  if (state.query_active) blocker = 'Reading settings for this connection… controls will be ready shortly.';

  $('blocker').textContent = blocker; $('blocker').classList.toggle('ready', canMove());

  $('align').hidden = !outside || !allEnabled || !!device?.recovery_required;

  $('align').disabled = !canAct() || device?.range_entry_deg > 3;

  const corrections = device?.joints.filter(j => Number.isFinite(j.range_entry_target_deg) && Math.abs(j.range_entry_target_deg - j.actual_deg) > .0005) || [];

  // The arm's own teach mode ended; only an explicit, confirmed handoff returns it to CAN control.

  const handoff = device?.fresh ? device.teaching : null;

  $('teach-return').hidden = !handoff?.return_required;

  $('teach-return').disabled = !idle() || !handoff?.can_return || !!handoff?.handoff_in_progress;

  $('teach-return').title = handoff?.can_return ? '' : (handoff?.return_reason || '');

  $('range-preview').hidden = !outside;

  $('range-preview').textContent = `${corrections.map(j => `J${j.index}: ${fixed(j.actual_deg)}° → ${fixed(j.range_entry_target_deg)}°`).join(' · ')}. ${device?.range_entry_deg > 3 ? 'Correction exceeds 3°; inspect the pose and calibration before motion.' : 'Move into range makes only these corrections at 1% controller speed. Other joints keep their measured targets.'}`;

  if (device?.fresh && idle()) {

    cards.forEach((c, i) => { if (!c.touched && document.activeElement !== c.input && document.activeElement !== c.slider) { c.input.value = fixed(device.joints[i].actual_deg); c.slider.value = c.input.value; } });

  }

  const speedLimitsReady = device?.joints.every(j => j.limits_fresh && Number.isFinite(j.max_speed_rad_s) && j.max_speed_rad_s > 0);

  const speedCeiling = speedLimitsReady ? Math.min(...device.joints.map(j => j.max_speed_rad_s * 180 / Math.PI)) : null;

  $('speed').disabled = !speedLimitsReady || !idle();

  $('speed-rate').textContent = speedLimitsReady ? `${fixed(speedCeiling * Number($('speed').value) / 100,2)}°/s target · ${fixed(speedCeiling,2)}°/s at 100%` : 'Waiting for fresh reported joint speed limits';

  const movable = canMove() && speedLimitsReady && Number($('speed').value) > 0;

  cards.forEach((card, i) => {

    const joint = device?.joints[i]; const fresh = joint?.fresh;

    card.element.querySelector('.readout strong').textContent = `${fixed(joint?.actual_deg)}${Number.isFinite(joint?.actual_deg) ? '°' : ''}`;

    card.element.querySelector('.joint-state').textContent = !fresh ? (joint?.actual_deg == null ? 'NO DATA' : 'STALE') : joint.enabled === true ? 'ENABLED' : joint.enabled === false ? 'DISABLED' : 'DRIVER STALE';

    card.element.querySelector('.readout span').textContent = fresh ? 'degrees measured' : 'no current measurement';

    const limits = joint?.limits_fresh;

    if (limits) { card.slider.min = card.input.min = joint.min_deg; card.slider.max = card.input.max = joint.max_deg; }

    card.slider.disabled = !limits || !movable;

    const range = card.element.querySelector('.range-labels'); range.children[0].textContent = limits ? `${joint.min_deg}°` : 'Read joint limits'; range.children[1].textContent = limits ? `${joint.max_deg}°` : '—';

    card.element.querySelector('.telemetry').textContent = fresh ? `${fixed(joint.velocity_rad_s * 180 / Math.PI,1)}°/s  ·  ${fixed(joint.current_a, 2)} A  ·  ${fixed(joint.temperature, 0)} °C  ·  ${fixed(joint.voltage, 1)} V${joint.fault ? `  ·  FAULT ${joint.fault}` : ''}${joint.target_deg != null ? `  ·  → ${fixed(joint.target_deg)}°` : ''}` : 'Waiting for live motor feedback';

    card.element.querySelectorAll('button').forEach(b => { b.disabled = !movable; });

  });

  const cycleStage = state.motion.sequence_stage;

  const upright = [device?.joints[0]?.actual_deg, 82, -160, 0, -7, device?.joints[5]?.actual_deg];

  const cycleValid = !!device && upright.every((angle, i) => Number.isFinite(angle) && angle >= device.joints[i].min_deg && angle <= device.joints[i].max_deg);

  $('cycle').disabled = !movable || !cycleValid;

  $('cycle').textContent = cycleStage === 'raising' ? 'Raising…' : cycleStage === 'upright' ? 'Upright · pausing…' : cycleStage === 'returning' ? 'Returning…' : '↑ Upright & return';

  const seconds = device?.fresh ? Math.ceil(2 * Math.max(...upright.map((angle, i) => Math.abs(angle - device.joints[i].actual_deg))) / 3 + 3) : null;

  $('cycle-help').textContent = cycleStage ? 'Complete movement in progress. Keep this window focused. Stop or Hold cancels the remaining sequence.' : `Upright → pause 2 seconds → return to the pose captured when clicked. Up to 3°/s${seconds ? ` · about ${seconds} seconds` : ''}. Clear the full swept path first.`;

  $('zero-all').disabled = !movable || !device?.joints.every(j => j.limits_fresh && j.min_deg <= 0 && j.max_deg >= 0); $('pose').disabled = !movable; $('hold').disabled = !(state.motion.active && state.motion.owner === owner && (state.phase === 'moving' || state.phase === 'gripper' || cycleStage));

  $('copy').disabled = !device?.fresh || !idle();

  $('query').disabled = !idle() || !device?.present; $('query').textContent = state.query_active ? 'Reading…' : 'Read firmware & limits';

  $('reconnect').disabled = !idle();

  $('enable').disabled = !idle() || !device?.fresh || !device.firmware_supported || allEnabled || !!device.recovery_required;

  $('enable').textContent = allEnabled ? 'Enabled' : 'Enable robot';

  const powerReady = idle() && device?.fresh && device.firmware_supported;

  $('disable').disabled = !powerReady || allDisabled; $('reset').disabled = !powerReady || device?.reset_pending; $('verify').disabled = !powerReady;

  $('stop-reason').textContent = device?.recovery_required ? (device.stop_reason || 'Recovery required.') + (device.last_fault ? ` Last robot fault: status ${device.last_fault.arm_status}, error ${device.last_fault.error}.` : '') : 'Inspect and support the arm before using recovery.';

  const incident = device?.stop_event;

  const recovering = !!device?.recovery_required || !!device?.reset_pending;

  const statusNames = ['Normal','Emergency stop','No solution','Singularity','Target outside limits','Joint communication fault','Brake not released','Collision','Teaching overspeed','Joint fault','Other fault','Teaching record','Teaching playback','Teaching paused','Controller overtemperature','Resistor overtemperature'];

  $('incident-panel').hidden = !recovering;

  $('incident-title').textContent = incident?.joint ? `Possible obstruction · J${incident.joint}` : 'Movement stopped · recovery required';

  $('incident-reason').textContent = incident?.reason || device?.stop_reason || device?.blocker || '';

  const stoppedJoint = incident?.joints?.find(j => j.joint === incident.joint);

  $('incident-evidence').textContent = stoppedJoint ? `Recorded ${new Date(incident.unix_time * 1000).toLocaleTimeString()}: J${incident.joint} measured ${fixed(stoppedJoint.actual_deg)}°, commanded ${fixed(stoppedJoint.command_deg)}°, final target ${fixed(stoppedJoint.target_deg)}°. Current ${fixed(stoppedJoint.current_a,2)} A. No progress ${fixed(stoppedJoint.no_progress_s,2)} s. Speed ${incident.speed_deg_s}°/s.` : 'No original movement snapshot is available for this older stop. Current feedback is shown below.';

  const teaching = ![0,2,6].includes(device?.status.teach_state);

  const unhealthy = !!device?.status.arm_status || !!device?.status.error || device?.joints.some(j => j.fault);

  const stateText = `Controller: ${statusNames[device?.status.arm_status] || 'Unknown'}; motors ${allDisabled ? 'all disabled' : allEnabled ? 'all enabled' : 'mixed/unknown'}.`;

  $('incident-next').textContent = `${stateText} ${teaching ? 'Controller reports an active teaching state. Reset is blocked; end teaching in the vendor procedure or power-cycle the supported arm, then verify. Restarting the dashboard does not clear a robot stop.' : device?.reset_pending ? 'Reset pending. Wait for healthy, disabled feedback, then verify recovery.' : unhealthy ? 'Clear the obstruction and support the stationary arm before resetting.' : 'Healthy feedback received. Verify recovery; no reset is needed.'}${outside ? ' A joint is also outside its reported range; inspect its pose before any range correction. This is not proof that zero calibration changed.' : ''}`;

  $('incident-reset').disabled = !powerReady || !!device?.reset_pending || teaching || !unhealthy;

  $('incident-verify').disabled = !powerReady || unhealthy || !(allDisabled || (allEnabled && !device.reset_pending));

  $('incident-raw').textContent = JSON.stringify({original_stop:incident || null, live_status:device?.status, joint_faults:device?.joints.map(j=>({joint:j.index,fault:j.fault})), reset_pending:device?.reset_pending},null,2);

  $('recovery').classList.toggle('required', !!device?.recovery_required);

  $('recovery').hidden = !device?.reset_pending && !(device?.fresh && (device.status.arm_status || device.status.error));

  $('gripper-actual').textContent = fixed(device?.gripper?.width_mm, 1);

  const gripper = device?.gripper;

  document.querySelector('.gripper-panel').hidden = !!state.pika?.configured;

  $('gripper-range').textContent = gripper?.configuration_fresh ? `Controller-reported range: 0–${gripper.max_width_mm} mm · raw encoder reading shown` : 'Read gripper configuration first';

  if (gripper?.configuration_fresh) $('width').max = gripper.max_width_mm;

  $('gripper-open').textContent = gripper?.configuration_fresh ? `Open · ${gripper.max_width_mm}` : 'Open';

  for (const id of ['gripper-close', 'gripper-open', 'gripper-move', 'gripper-minus', 'gripper-plus']) $(id).disabled = !canAct() || !gripper?.fresh || !gripper.configuration_fresh;

  $('phase').textContent = state.phase.toUpperCase(); $('message').textContent = state.message;

  $('diagnostics').textContent = device ? JSON.stringify({ adapter: device.id, interface: device.interface, usb_path: device.usb_path, generation: device.generation, bitrate: device.bitrate, bus_state: device.bus_state, connection_error: device.connection_error || null, last_received_seconds_ago: device.last_rx_age_s, received: device.rx_frames, queued: device.tx_frames, kernel_received: device.kernel_rx_packets, kernel_transmitted: device.kernel_tx_packets, local_frames_ignored: device.local_frames, can_errors: device.can_errors, controller: device.status, last_recorded_fault: device.last_fault, acceleration_limits: device.joints.map(j => ({ joint: j.index, rad_s2: j.max_acceleration_rad_s2, fresh: j.acceleration_fresh })), firmware: device.firmware || null, build: device.build || null, recovery_required: device.recovery_required, reset_pending: device.reset_pending }, null, 2) : 'No CAN adapters detected.';

}

// Remember preferences only; never restore enable state, targets or commands.

const settingRanges = {step:[0.001,5],speed:[0,100],tolerance:[0.05,1]};

function showSettingValues() {

  $('step-value').textContent = `${Number($('step').value)}°`;

  $('tolerance-value').textContent = `${Number($('tolerance').value)}°`;

  $('speed-percent').textContent = `${Number($('speed').value)}%`;

}

try {

  const saved = JSON.parse(localStorage.getItem('piper.motion.settings.v1') || '{}');

  for (const [id,[min,max]] of Object.entries(settingRanges)) {

    if (typeof saved[id] === 'number' && Number.isFinite(saved[id]) && saved[id]>=min && saved[id]<=max) $(id).value=saved[id];

  }

} catch { /* Use defaults when storage is unavailable or invalid. */ }

showSettingValues();

for (const id of Object.keys(settingRanges)) $(id).addEventListener('input', () => {

  showSettingValues();

  try { localStorage.setItem('piper.motion.settings.v1', JSON.stringify(Object.fromEntries(Object.keys(settingRanges).map(key=>[key,Number($(key).value)])))); } catch {}

});

$('speed').addEventListener('input', render);

$('device').onchange = () => perform(api.select, { device_id: $('device').value, owner });

$('query').onclick = () => perform(api.query); $('reconnect').onclick = () => perform(api.reconnect);

$('stop').onclick = stopAll;

// Enabling never clears a recovery latch: that is the separate Verify recovery step.

$('enable').onclick = () => command('enable');

$('disable').onclick = () => { window.dispatchEvent(new Event('piper-robot-disable')); return command('disable'); };

$('reset').onclick = () => command('reset'); $('verify').onclick = () => command('verify-reset'); $('hold').onclick = () => command('hold');

$('incident-reset').onclick = () => command('reset');

// The recovery latch is cleared only here, by the verification that matches the measured motor state.

$('incident-verify').onclick = () => {

  if (!device?.joints) return showError('No arm feedback is available to verify.');

  if (device.reset_pending) return command('verify-reset');

  return command(device.joints.every(j => j.enabled === true) ? 'verify-current' : 'verify-disabled');

};

$('align').onclick = () => command('align');

$('teach-return').onclick = () => command(device?.teaching?.return_requires_zero ? 'teach-return-zero' : 'teach-return');

$('cycle').onclick = () => command('cycle');

$('zero-all').onclick = () => command('pose', { degrees:[0,0,0,0,0,0], synchronized:true });
$('pose').onclick = () => { if (cards.some(c => c.input.value === '')) return showError('Enter all six target angles.'); command('pose', { degrees: cards.map(c => Number(c.input.value)) }); };

$('copy').onclick = () => { if (device?.joints) cards.forEach((card, i) => { card.input.value = fixed(device.joints[i].actual_deg); card.slider.value = card.input.value; card.touched = true; }); };

function moveGripper(width_mm) { return command('gripper', { width_mm, effort_nm: Number($('gripper-effort').value), allow_closed_offset: (device?.gripper?.width_mm < 0 && device.gripper.width_mm >= -2) }); }

$('gripper-open').onclick = () => moveGripper(device?.gripper?.max_width_mm); $('gripper-close').onclick = () => moveGripper(0);

$('gripper-minus').onclick = () => { if (device?.gripper) moveGripper(Math.max(0, device.gripper.width_mm - Number($('gripper-step').value))); };

$('gripper-plus').onclick = () => { if (device?.gripper) moveGripper(Math.min(device.gripper.max_width_mm, device.gripper.width_mm + Number($('gripper-step').value))); };

$('gripper-move').onclick = () => { if ($('width').value === '') return showError('Enter the requested gripper opening.'); moveGripper(Number($('width').value)); };



document.addEventListener('keydown', event => {

  if (event.key === 'Escape') { event.preventDefault(); stopAll(); return; }

  if (/INPUT|SELECT|TEXTAREA/.test(event.target.tagName) || event.repeat) return;

  if (/^[1-6]$/.test(event.key)) chooseJoint(Number(event.key));

  if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); jog(selectedJoint, event.key === 'ArrowRight' ? 1 : -1); }

});

/* Leaving ends whatever lease exists, including one whose first request is

   still in flight: that request has no id yet, so perform() releases it when

   its reply names one. */

function leaving() {

  if (lease.phase === 'none' || lease.phase === 'stopping') return;

  const ending = lease;

  setLease('none');

  if (ending.request_id) release(ending.request_id, device?.id);

}

// Local camera UI is shared by both backends and runs in bench.mjs.

window.addEventListener('pagehide', leaving); window.addEventListener('blur', leaving);

document.addEventListener('visibilitychange', () => { if (document.hidden) leaving(); });

setInterval(async () => {

  if (!lease.request_id || lease.phase === 'stopping' || performance.now() - lastPoll > 600 || document.hidden || !document.hasFocus()) return;

  const beating = lease;

  try { await send(api.heartbeat, { ...arm(), request_id: beating.request_id }); } catch (error) {

    if (lease.request_id === beating.request_id && lease.phase !== 'stopping') setLease('none');

    // Completion can race the last scheduled heartbeat; the next state poll

    // supplies the result. A missing active lease is normal in that case.

    if (!error.message.includes('No matching active control lease')) showError(error.message);

  }

}, 200);

async function poll() {

  try {

    const response = await api.state({}, { timeout: 1000 });

    if (!response.ok) throw Error('State request failed.');

    const next = await response.json();

    if (state && state.token !== next.token) { setLease('none'); revokeApproval(); }

    state = next; identify(next); lastPoll = performance.now();

    if (lease.phase === 'leased' && state.motion.request_id >= lease.request_id && (!state.motion.active || state.motion.owner !== owner)) setLease('none');

    render();

    // pika.mjs follows this poll instead of polling /api/state itself.

    window.dispatchEvent(new CustomEvent('piper-state', { detail: state }));

  } catch {

    window.dispatchEvent(new CustomEvent('piper-state', { detail: null }));

    $('connection').textContent = 'Controller offline · reconnecting'; $('connection').className = 'connection bad';

    $('blocker').textContent = 'Connection to the C server was lost. The control lease will expire; verify the arm has stopped.'; $('blocker').classList.remove('ready');

    document.querySelectorAll('button:not(#stop):not(#safety-continue):not(#safety-cancel)').forEach(b => { if (!b.closest('#camera-panel, .pika-panel')) b.disabled = true; });

  } finally { setTimeout(poll, 200); }

}

poll();
