'use strict';
const $ = id => document.getElementById(id);
const names = ['Base', 'Shoulder', 'Elbow', 'Wrist roll', 'Wrist pitch', 'Tool roll'];
const owner = crypto.randomUUID();
let state, device, selectedJoint = 1, lease = null, lastPoll = 0, sending = false, selectedId = '', seededGeneration = 0;
let toastTimer, lastInfoAttempt = 0, infoIdentity = '', infoLoading = false;
const cards = [];
function showError(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { $('toast').hidden = true; }, 7000); }
function fixed(value, digits = 3) { return Number.isFinite(value) ? value.toFixed(digits) : '—'; }
function chooseJoint(index) { selectedJoint = index; cards.forEach((c, i) => c.element.classList.toggle('selected', i + 1 === index)); $('active-joint').textContent = `J${index} SELECTED`; }
function values() { return { standalone: $('standalone').checked, clear: $('clear').checked, supported: $('supported').checked, reset_completed: $('reset-completed').checked, speed_deg_s: Number($('speed').value), tolerance_deg: Number($('tolerance').value) }; }
async function post(route, body = {}) {
  if (!state) throw Error('The C server is unavailable.');
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
  } catch (error) { showError(error.message); }
  finally { sending = false; }
}
function command(action, fields = {}) { return perform('/api/command', { action, ...fields }); }
function jog(index, sign) {
  const step = Number($('step').value);
  if (!Number.isFinite(step) || step <= 0 || step > 5) return showError('Jog step must be greater than 0 and at most 5°.');
  if (!canMove()) return showError(device?.blocker || 'Enable motors and confirm the motion path first.');
  command('jog', { joint: index, degrees: sign * step });
}
for (let i = 1; i <= 6; i++) {
  const element = document.createElement('section'); element.className = 'joint';
  element.innerHTML = `<div class="joint-top"><h3><b>J${i}</b>${names[i - 1]}</h3><span class="joint-state">NO DATA</span></div><div class="readout"><strong>—</strong><span>degrees measured</span></div><div class="target"><label for="target-${i}">Target · °<input id="target-${i}" type="number" step="0.001" placeholder="Angle"></label><button class="primary move" aria-label="Move joint ${i}">Move</button></div><input class="slider" type="range" min="-180" max="180" step="0.1" value="0" disabled aria-label="Joint ${i} target angle"><div class="range-labels"><span>Limits unknown</span><span>—</span></div><div class="jog-row"><button class="minus" aria-label="Jog joint ${i} negative">− step</button><button class="zero" aria-label="Zero joint ${i}">Zero</button><button class="plus" aria-label="Jog joint ${i} positive">+ step</button></div><div class="telemetry">Waiting for motor feedback</div>`;
  $('joints').append(element);
  const card = { element, input: element.querySelector('input[type=number]'), slider: element.querySelector('.slider'), touched: false };
  element.addEventListener('pointerdown', () => chooseJoint(i));
  card.input.addEventListener('input', () => { card.touched = true; if (card.input.value !== '') card.slider.value = card.input.value; });
  card.slider.addEventListener('input', () => { card.touched = true; card.input.value = Number(card.slider.value).toFixed(3); });
  element.querySelector('.move').onclick = () => { if (card.input.value === '' || !Number.isFinite(Number(card.input.value))) return showError('Enter a target angle first.'); command('joint', { joint: i, degrees: Number(card.input.value) }); };
  element.querySelector('.minus').onclick = () => jog(i, -1);
  element.querySelector('.plus').onclick = () => jog(i, 1);
  element.querySelector('.zero').onclick = () => command('zero', { joint: i });
  cards.push(card);
}
chooseJoint(1);
function idle() { return !!state && !state.motion.active && !state.query_active && !infoLoading; }
function canAct() { return idle() && !!device && !device.blocker && device.joints.every(j => j.enabled === true) && $('clear').checked && $('standalone').checked && lastPoll > performance.now() - 600; }
function canMove() { return canAct() && device.range_entry_deg === 0; }
function render() {
  if (!state) return;
  device = state.devices.find(d => d.id === state.selected);
  if (selectedId !== state.selected) { selectedId = state.selected; seededGeneration = 0; cards.forEach(c => { c.touched = false; c.input.value = ''; }); for (const id of ['standalone', 'clear', 'supported', 'reset-completed', 'gripper-offset']) $(id).checked = false; }
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
  const conditions = $('standalone').checked && $('clear').checked && $('supported').checked;
  const outside = device?.range_entry_deg > 0;
  let blocker = device?.blocker || (!device ? 'Connect a USB-CAN adapter to begin.' : !allEnabled ? (conditions ? 'Ready to enable motors. Enabling sends no joint-position target.' : 'Motors are off. Confirm the three conditions under Before control, then Enable motors.') : outside ? 'Motors are enabled. Move into range once before using individual joint controls.' : !$('clear').checked || !$('standalone').checked ? 'Motors are enabled. Confirm the path is clear and this arm has no other controller.' : 'Ready to move. Choose a joint and enter an angle, or use − / + step.');
  if (device?.recovery_required && allDisabled && !device.reset_pending) blocker = 'Motors are off. Confirm support and standalone operation, then Verify motors off to clear the saved recovery lock.';
  if (state.query_active || infoLoading) blocker = 'Refreshing firmware and limits… controls will be ready shortly.';
  $('blocker').textContent = blocker; $('blocker').classList.toggle('ready', canMove());
  $('recover').hidden = !device?.recovery_required || !allDisabled || device.reset_pending;
  $('align').hidden = !outside || !allEnabled || !!device?.recovery_required;
  $('align').disabled = !canAct() || device?.range_entry_deg > 3;
  const corrections = device?.joints.filter(j => Number.isFinite(j.range_entry_target_deg) && Math.abs(j.range_entry_target_deg - j.actual_deg) > .0005) || [];
  $('range-preview').hidden = !outside;
  $('range-preview').textContent = `${corrections.map(j => `J${j.index}: ${fixed(j.actual_deg)}° → ${fixed(j.range_entry_target_deg)}°`).join(' · ')}. ${device?.range_entry_deg > 3 ? 'Correction exceeds 3°; inspect the pose and calibration before motion.' : 'Move into range makes only these corrections at 1% controller speed. Other joints keep their measured targets.'}`;
  if (device?.fresh && idle()) {
    cards.forEach((c, i) => { if (!c.touched && document.activeElement !== c.input && document.activeElement !== c.slider) { c.input.value = fixed(device.joints[i].actual_deg); c.slider.value = c.input.value; } });
    seededGeneration = device.generation;
  }
  const movable = canMove();
  cards.forEach((card, i) => {
    const joint = device?.joints[i]; const fresh = joint?.fresh;
    card.element.querySelector('.readout strong').textContent = `${fixed(joint?.actual_deg)}${Number.isFinite(joint?.actual_deg) ? '°' : ''}`;
    card.element.querySelector('.joint-state').textContent = !fresh ? (joint?.actual_deg == null ? 'NO DATA' : 'STALE') : joint.enabled === true ? 'ENABLED' : joint.enabled === false ? 'DISABLED' : 'DRIVER STALE';
    card.element.querySelector('.readout span').textContent = fresh ? 'degrees measured' : 'no current measurement';
    const limits = joint?.limits_fresh;
    if (limits) { card.slider.min = card.input.min = joint.min_deg; card.slider.max = card.input.max = joint.max_deg; }
    card.slider.disabled = !limits;
    const range = card.element.querySelector('.range-labels'); range.children[0].textContent = limits ? `${joint.min_deg}°` : 'Read joint limits'; range.children[1].textContent = limits ? `${joint.max_deg}°` : '—';
    card.element.querySelector('.telemetry').textContent = fresh ? `${fixed(joint.current_a, 2)} A  ·  ${fixed(joint.temperature, 0)} °C  ·  ${fixed(joint.voltage, 1)} V${joint.fault ? `  ·  FAULT ${joint.fault}` : ''}${joint.target_deg != null ? `  ·  → ${fixed(joint.target_deg)}°` : ''}` : 'Waiting for live motor feedback';
    card.element.querySelectorAll('button').forEach(b => { b.disabled = !movable; });
  });
  const cycleStage = state.motion.sequence_stage;
  const upright = [device?.joints[0]?.actual_deg, 82, -160, 0, -7, device?.joints[5]?.actual_deg];
  const cycleValid = !!device && upright.every((angle, i) => Number.isFinite(angle) && angle >= device.joints[i].min_deg && angle <= device.joints[i].max_deg);
  $('cycle').disabled = !movable || !cycleValid;
  $('cycle').textContent = cycleStage === 'raising' ? 'Raising…' : cycleStage === 'upright' ? 'Upright · pausing…' : cycleStage === 'returning' ? 'Returning…' : '↑ Upright & return';
  const seconds = device?.fresh ? Math.ceil(2 * Math.max(...upright.map((angle, i) => Math.abs(angle - device.joints[i].actual_deg))) / 3 + 3) : null;
  $('cycle-help').textContent = cycleStage ? 'Complete movement in progress. Keep this window focused. Stop or Hold cancels the remaining sequence.' : `Upright → pause 2 seconds → return to the pose captured when clicked. Up to 3°/s${seconds ? ` · about ${seconds} seconds` : ''}. Clear the full swept path first.`;
  $('pose').disabled = !movable; $('hold').disabled = !(state.motion.active && state.motion.owner === owner && (state.phase === 'moving' || state.phase === 'gripper' || cycleStage));
  $('copy').disabled = !device?.fresh || !idle();
  $('query').disabled = !idle() || !device?.present; $('query').textContent = state.query_active ? 'Reading…' : 'Read firmware & limits';
  $('reconnect').disabled = !idle();
  $('enable').disabled = !idle() || !device || !!device.blocker || !device.joints.every(j => j.enabled === false) || !$('supported').checked || !$('standalone').checked || !$('clear').checked;
  const powerReady = idle() && device?.fresh && device.firmware_supported && $('supported').checked && $('standalone').checked;
  $('disable').disabled = !powerReady || allDisabled; $('reset').disabled = !powerReady || device?.reset_pending; $('verify').disabled = !powerReady || !$('reset-completed').checked;
  $('recover').disabled = !powerReady || !allDisabled;
  $('stop-reason').textContent = device?.recovery_required ? (device.stop_reason || 'Recovery required.') + (device.last_fault ? ` Last robot fault: status ${device.last_fault.arm_status}, error ${device.last_fault.error}.` : '') : 'Inspect and support the arm before using recovery.';
  $('recovery').classList.toggle('required', !!device?.recovery_required);
  $('gripper-actual').textContent = fixed(device?.gripper?.width_mm, 1);
  const gripper = device?.gripper;
  $('gripper-range').textContent = gripper?.configuration_fresh ? `Controller-reported range: 0–${gripper.max_width_mm} mm · raw encoder reading shown` : 'Read gripper configuration first';
  if (gripper?.configuration_fresh) $('width').max = gripper.max_width_mm;
  $('gripper-open').textContent = gripper?.configuration_fresh ? `Open · ${gripper.max_width_mm}` : 'Open';
  for (const id of ['gripper-close', 'gripper-open', 'gripper-move']) $(id).disabled = !canAct() || !gripper?.fresh || !gripper.configuration_fresh;
  $('phase').textContent = state.phase.toUpperCase(); $('message').textContent = state.message;
  $('diagnostics').textContent = device ? JSON.stringify({ adapter: device.id, interface: device.interface, usb_path: device.usb_path, generation: device.generation, bitrate: device.bitrate, bus_state: device.bus_state, connection_error: device.connection_error || null, last_received_seconds_ago: device.last_rx_age_s, received: device.rx_frames, queued: device.tx_frames, kernel_received: device.kernel_rx_packets, kernel_transmitted: device.kernel_tx_packets, local_frames_ignored: device.local_frames, can_errors: device.can_errors, controller: device.status, last_recorded_fault: device.last_fault, acceleration_limits: device.joints.map(j => ({ joint: j.index, rad_s2: j.max_acceleration_rad_s2, fresh: j.acceleration_fresh })), firmware: device.firmware || null, build: device.build || null, recovery_required: device.recovery_required, reset_pending: device.reset_pending }, null, 2) : 'No CAN adapters detected.';
}
$('device').onchange = () => perform('/api/select', { device_id: $('device').value });
$('query').onclick = () => perform('/api/query'); $('reconnect').onclick = () => perform('/api/reconnect');
$('stop').onclick = () => perform('/api/stop'); $('enable').onclick = () => command('enable'); $('disable').onclick = () => command('disable');
$('reset').onclick = () => command('reset'); $('verify').onclick = () => command('verify-reset'); $('hold').onclick = () => command('hold');
$('recover').onclick = () => command('verify-disabled'); $('align').onclick = () => command('align');
$('cycle').onclick = () => command('cycle');
$('pose').onclick = () => { if (cards.some(c => c.input.value === '')) return showError('Enter all six target angles.'); command('pose', { degrees: cards.map(c => Number(c.input.value)) }); };
$('copy').onclick = () => cards.forEach((card, i) => { card.input.value = fixed(device.joints[i].actual_deg); card.slider.value = card.input.value; card.touched = true; });
function moveGripper(width_mm) { return command('gripper', { width_mm, effort_nm: Number($('gripper-effort').value), allow_closed_offset: $('gripper-offset').checked }); }
$('gripper-open').onclick = () => moveGripper(device?.gripper?.max_width_mm); $('gripper-close').onclick = () => moveGripper(0);
$('gripper-move').onclick = () => { if ($('width').value === '') return showError('Enter the requested gripper opening.'); moveGripper(Number($('width').value)); };
for (const id of ['standalone', 'clear', 'supported', 'reset-completed', 'gripper-offset']) $(id).onchange = render;
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
// Video capture runs independently of the CAN/control loop. Only fresh JPEGs
// are served; never label an old displayed image as live after a disconnect.
let cameraBusy = false, cameraImageAt = 0, cameraUrl = '', cameraFrameLoading = false;
function renderCamera() {
  const camera = state?.camera;
  $('camera-start').disabled = cameraBusy || !camera?.configured || camera.running;
  $('camera-stop').disabled = cameraBusy || !camera?.running;
  const live = camera?.fresh && cameraImageAt > performance.now() - 1500;
  $('camera-empty').hidden = !!live;
  if (live) $('camera-status').textContent = `LIVE · ${camera.name} · frame ${camera.sequence}`;
  else {
    const status = camera?.error || (!camera?.configured ? 'No camera configured.' : camera.running ? 'Waiting for fresh video…' : 'Camera is off');
    $('camera-empty').textContent = status;
    $('camera-status').textContent = status;
  }
  if (!camera?.running) { $('camera-image').hidden = true; cameraImageAt = 0; }
}
async function setCamera(enabled) {
  cameraBusy = true; renderCamera();
  try { await post('/api/camera', { enabled }); }
  catch (error) { showError(error.message); }
  finally { cameraBusy = false; renderCamera(); }
}
async function pollCamera() {
  if (cameraFrameLoading || !state?.camera?.running || document.hidden) return;
  cameraFrameLoading = true;
  let nextUrl = '';
  try {
    const response = await fetch('/api/camera/frame', { cache: 'no-store', signal: AbortSignal.timeout(1200) });
    if (!response.ok) throw Error('No fresh frame');
    nextUrl = URL.createObjectURL(await response.blob());
    const img = $('camera-image'); img.src = nextUrl; await img.decode();
    if (cameraUrl) URL.revokeObjectURL(cameraUrl);
    cameraUrl = nextUrl; nextUrl = ''; cameraImageAt = performance.now();
    img.hidden = !state?.camera?.running;
  } catch { cameraImageAt = 0; }
  finally {
    if (nextUrl) URL.revokeObjectURL(nextUrl);
    cameraFrameLoading = false; renderCamera();
  }
}
$('camera-start').onclick = () => setCamera(true);
$('camera-stop').onclick = () => setCamera(false);
$('camera-enlarge').onclick = () => {
  const expanded = $('camera-panel').classList.toggle('expanded');
  $('camera-enlarge').textContent = expanded ? 'Shrink' : 'Enlarge';
  $('camera-enlarge').setAttribute('aria-expanded', String(expanded));
};
setInterval(pollCamera, 200);
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
    if (state && state.token !== next.token) { lease = null; seededGeneration = 0; infoIdentity = ''; for (const id of ['standalone', 'clear', 'supported', 'reset-completed', 'gripper-offset']) $(id).checked = false; }
    state = next; lastPoll = performance.now(); infoLoading = false;
    if (lease && state.motion.request_id >= lease.request_id && (!state.motion.active || state.motion.owner !== owner)) lease = null;
    render(); renderCamera();
    // Read-only discovery/refresh, once per connection then before limits age
    // out. Never enable, reset, clear recovery, or resume a motion automatically.
    const identity = `${state.token}:${device?.id}:${device?.generation}`;
    if (identity !== infoIdentity) { infoIdentity = identity; lastInfoAttempt = 0; }
    const needsInfo = device?.fresh && (!device.firmware || !device.gripper.configuration_fresh || device.gripper.configuration_age_s > 45 || device.joints.some(j => !j.limits_fresh || j.limits_age_s > 45 || !j.acceleration_fresh || j.acceleration_age_s > 45));
    if (needsInfo && idle() && !sending && (!lastInfoAttempt || performance.now() - lastInfoAttempt > 30000)) {
      lastInfoAttempt = performance.now(); infoLoading = true; render();
      await post('/api/query').catch(() => {}); // Next authoritative poll releases the UI lock.
    }
  } catch {
    $('connection').textContent = 'C server unavailable'; $('connection').className = 'connection bad';
    $('blocker').textContent = 'Connection to the C server was lost. The control lease will expire; verify the arm has stopped.'; $('blocker').classList.remove('ready');
    document.querySelectorAll('button:not(#stop)').forEach(b => { if (!b.closest('#camera-panel')) b.disabled = true; });
    cameraImageAt = 0; $('camera-empty').hidden = false; $('camera-empty').textContent = 'Server unavailable · video is not live'; $('camera-status').textContent = 'Server unavailable';
  } finally { setTimeout(poll, 200); }
}
poll();
