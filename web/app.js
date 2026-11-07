'use strict';

const $ = id => document.getElementById(id);

const names = ['Base', 'Shoulder', 'Elbow', 'Wrist roll', 'Wrist pitch', 'Tool roll'];

const owner = crypto.randomUUID();

let state, device, selectedJoint = 1, lease = null, lastPoll = 0, sending = false, selectedId = '', seededGeneration = 0;

let toastTimer;

const cards = [];

let controlApproved = false, approvalPending = null;

window.confirmPiperControl = async () => {

  if (controlApproved) return true;

  if (approvalPending) return approvalPending;

  approvalPending = new Promise(resolve => {

    const dialog = $('safety-prompt');

    $('safety-continue').disabled = false; $('safety-cancel').disabled = false;

    const finish = approved => { controlApproved = approved; dialog.close(); approvalPending = null; resolve(approved); };

    $('safety-continue').onclick = () => finish(true);

    $('safety-cancel').onclick = () => finish(false);

    dialog.oncancel = event => { event.preventDefault(); finish(false); };

    dialog.showModal();

  });

  return approvalPending;

};

function showError(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { $('toast').hidden = true; }, 7000); }

function fixed(value, digits = 3) { return Number.isFinite(value) ? value.toFixed(digits) : '—'; }

function chooseJoint(index) { selectedJoint = index; cards.forEach((c, i) => c.element.classList.toggle('selected', i + 1 === index)); $('active-joint').textContent = `J${index} SELECTED`; }

function values() { return { standalone: controlApproved, clear: controlApproved, supported: controlApproved, reset_completed: true, speed_percent: Number($('speed').value), tolerance_deg: Number($('tolerance').value) }; }

async function post(route, body = {}) {

  if (!state) throw Error('The controller server is unavailable.');

  const response = await fetch(route, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token }, body: JSON.stringify({ device_id: device?.id, generation: device?.generation, owner, ...values(), ...body }), signal: AbortSignal.timeout(1500) });

  const result = await response.json();

  if (!response.ok) throw Error(result.error || 'Request failed.');

  return result;

}

async function perform(route, body = {}) {

  if (sending && route !== '/api/stop') return;

  sending = true;

  try {

    const result = await post(route, body);

    if (result.request_id) lease = { request_id: result.request_id, owner };

    if (route === '/api/stop') lease = null;

    $('message').textContent = result.message;

    return result;

  } catch (error) { showError(error.message); }

  finally { sending = false; }

}

async function command(action, fields = {}) { if (!await window.confirmPiperControl()) return; return perform('/api/command', { action, ...fields }); }

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

  if (selectedId !== state.selected) { selectedId = state.selected; seededGeneration = 0; cards.forEach(c => { c.touched = false; c.input.value = ''; }); controlApproved = false; }

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

  const conditions = controlApproved;

  const outside = device?.range_entry_deg > 0;

  let blocker = device?.blocker || (!device ? 'Connect a USB-CAN adapter to begin.' : !allEnabled ? (conditions ? 'Ready to enable motors. Enabling sends no joint-position target.' : 'Motors are off. Click Enable motors to begin.') : outside ? `Joint controls blocked: ${device.joints.filter(j => j.actual_deg < j.min_deg || j.actual_deg > j.max_deg).map(j => `J${j.index} ${fixed(j.actual_deg)}° outside ${j.min_deg}…${j.max_deg}°`).join('; ')}. Clear the path, then use Move into range.` : !controlApproved ? 'Ready to move.' : 'Ready to move. Choose a joint and enter an angle, or use − / + step.');

  if (device?.recovery_required && allDisabled && !device.reset_pending) blocker = 'Motors are off. Enable robot verifies the idle state and clears the saved stop before enabling.';

  if (state.query_active) blocker = 'Reading settings for this connection… controls will be ready shortly.';

  $('blocker').textContent = blocker; $('blocker').classList.toggle('ready', canMove());

  $('recover').hidden = true;

  $('align').hidden = !outside || !allEnabled || !!device?.recovery_required;

  $('align').disabled = !canAct() || device?.range_entry_deg > 3;

  const corrections = device?.joints.filter(j => Number.isFinite(j.range_entry_target_deg) && Math.abs(j.range_entry_target_deg - j.actual_deg) > .0005) || [];

  $('range-preview').hidden = !outside;

  $('range-preview').textContent = `${corrections.map(j => `J${j.index}: ${fixed(j.actual_deg)}° → ${fixed(j.range_entry_target_deg)}°`).join(' · ')}. ${device?.range_entry_deg > 3 ? 'Correction exceeds 3°; inspect the pose and calibration before motion.' : 'Move into range makes only these corrections at 1% controller speed. Other joints keep their measured targets.'}`;

  if (device?.fresh && idle()) {

    cards.forEach((c, i) => { if (!c.touched && document.activeElement !== c.input && document.activeElement !== c.slider) { c.input.value = fixed(device.joints[i].actual_deg); c.slider.value = c.input.value; } });

    seededGeneration = device.generation;

  }

  const speedLimitsReady = device?.joints.every(j => j.limits_fresh && Number.isFinite(j.max_speed_rad_s) && j.max_speed_rad_s > 0);

  const speedCeiling = speedLimitsReady ? Math.min(...device.joints.map(j => j.max_speed_rad_s * 180 / Math.PI)) : null;

  $('speed').disabled = !speedLimitsReady || !idle();

  $('speed-percent').textContent = `${$('speed').value}%`;

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

  $('enable').disabled = !idle() || !device?.fresh || !device.firmware_supported || (allEnabled && !device.recovery_required);

  $('enable').textContent = device?.recovery_required ? 'Enable controls' : allEnabled ? 'Enabled' : 'Enable robot';

  const powerReady = idle() && device?.fresh && device.firmware_supported;

  $('disable').disabled = !powerReady || allDisabled; $('reset').disabled = !powerReady || device?.reset_pending; $('verify').disabled = !powerReady;

  $('recover').disabled = !powerReady || !allDisabled;

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

  $('incident-verify').disabled = !powerReady || !allDisabled || unhealthy;

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

  try { localStorage.setItem('piper.motion.settings.v1', JSON.stringify(Object.fromEntries(Object.keys(settingRanges).map(key=>[key,Number($(key).value)])))); } catch {}

});

$('tolerance').oninput = () => { $('tolerance-value').textContent = `${Number($('tolerance').value)}°`; };

$('step').oninput = () => { $('step-value').textContent = `${Number($('step').value)}°`; };

$('speed').oninput = () => { $('speed-percent').textContent = `${$('speed').value}%`; render(); };

$('device').onchange = () => perform('/api/select', { device_id: $('device').value });

$('query').onclick = () => perform('/api/query'); $('reconnect').onclick = () => perform('/api/reconnect');

$('stop').onclick = () => perform('/api/stop');

$('enable').onclick = async () => {

  if (!await window.confirmPiperControl()) return;

  if (device?.recovery_required) {

    const cleared = await command(device.reset_pending ? 'verify-reset' : 'verify-current');

    if (!cleared) return;

  }

  if (!device.joints.every(j => j.enabled === true)) return command('enable');

};

$('disable').onclick = () => { window.dispatchEvent(new Event('piper-robot-disable')); return command('disable'); };

$('reset').onclick = () => command('reset'); $('verify').onclick = () => command('verify-reset'); $('hold').onclick = () => command('hold');

$('incident-reset').onclick = () => command('reset');

$('incident-verify').onclick = () => command(device?.reset_pending ? 'verify-reset' : 'verify-disabled');

$('recover').onclick = () => command('verify-disabled'); $('align').onclick = () => command('align');

$('cycle').onclick = () => command('cycle');

$('zero-all').onclick = () => command('pose', { degrees:[0,0,0,0,0,0], synchronized:true });
$('pose').onclick = () => { if (cards.some(c => c.input.value === '')) return showError('Enter all six target angles.'); command('pose', { degrees: cards.map(c => Number(c.input.value)) }); };

$('copy').onclick = () => cards.forEach((card, i) => { card.input.value = fixed(device.joints[i].actual_deg); card.slider.value = card.input.value; card.touched = true; });

function moveGripper(width_mm) { return command('gripper', { width_mm, effort_nm: Number($('gripper-effort').value), allow_closed_offset: (device?.gripper?.width_mm < 0 && device.gripper.width_mm >= -2) }); }

$('gripper-open').onclick = () => moveGripper(device?.gripper?.max_width_mm); $('gripper-close').onclick = () => moveGripper(0);

$('gripper-minus').onclick = () => moveGripper(Math.max(0, device.gripper.width_mm - Number($('gripper-step').value)));

$('gripper-plus').onclick = () => moveGripper(Math.min(device.gripper.max_width_mm, device.gripper.width_mm + Number($('gripper-step').value)));

$('gripper-move').onclick = () => { if ($('width').value === '') return showError('Enter the requested gripper opening.'); moveGripper(Number($('width').value)); };



document.addEventListener('keydown', event => {

  if (event.key === 'Escape') { event.preventDefault(); perform('/api/stop'); return; }

  if (/INPUT|SELECT|TEXTAREA/.test(event.target.tagName) || event.repeat) return;

  if (/^[1-6]$/.test(event.key)) chooseJoint(Number(event.key));

  if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); jog(selectedJoint, event.key === 'ArrowRight' ? 1 : -1); }

});

function leaving() {

  if (!lease || !state) return;

  const ending = { ...lease, device_id: device?.id };

  lease = null;

  fetch('/api/stop', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token }, body: JSON.stringify(ending), keepalive: true }).catch(() => {});

}

// Local camera UI is shared by both backends and runs in bench.mjs.

window.addEventListener('pagehide', leaving); window.addEventListener('blur', leaving);

document.addEventListener('visibilitychange', () => { if (document.hidden) leaving(); });

setInterval(async () => {

  if (!lease || performance.now() - lastPoll > 600 || document.hidden || !document.hasFocus()) return;

  try { await post('/api/heartbeat', lease); } catch (error) {

    lease = null;

    // Completion can race the last scheduled heartbeat; the next state poll

    // supplies the result. A missing active lease is normal in that case.

    if (!error.message.includes('No matching active control lease')) showError(error.message);

  }

}, 200);

async function poll() {

  try {

    const response = await fetch('/api/state', { signal: AbortSignal.timeout(1000), cache: 'no-store' });

    if (!response.ok) throw Error('State request failed.');

    const next = await response.json();

    if (state && state.token !== next.token) { lease = null; seededGeneration = 0; controlApproved = false; }

    state = next; lastPoll = performance.now();

    if (lease && state.motion.request_id >= lease.request_id && (!state.motion.active || state.motion.owner !== owner)) lease = null;

    render();



  } catch {

    $('connection').textContent = 'Controller offline · reconnecting'; $('connection').className = 'connection bad';

    $('blocker').textContent = 'Connection to the C server was lost. The control lease will expire; verify the arm has stopped.'; $('blocker').classList.remove('ready');

    document.querySelectorAll('button:not(#stop):not(#safety-continue):not(#safety-cancel)').forEach(b => { if (!b.closest('#camera-panel, .pika-panel')) b.disabled = true; });

  } finally { setTimeout(poll, 200); }

}

poll();
