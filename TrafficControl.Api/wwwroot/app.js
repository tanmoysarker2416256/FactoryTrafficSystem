// Plain JavaScript dashboard. Rule: the browser never decides traffic behaviour.
// It only (1) shows what the backend says and (2) sends intents/events to the backend.

const $ = (id) => document.getElementById(id);
const COLORS = { RED: '#d93636', YELLOW: '#e9b500', GREEN: '#2e9d46', UNKNOWN: '#8a8f98' };
const app = { junction: null, status: null, lastEvent: null, seq: Date.now(), tick: 0, noticeTimer: null };

// Small DOM helper. Uses text nodes only, so text coming from the API can never inject HTML.
function h(tag, props = {}, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
        if (k === 'class') el.className = v;
        else if (k.startsWith('on')) el[k] = v;
        else el.setAttribute(k, v);
    }
    for (const kid of kids.flat()) el.append(kid instanceof Node ? kid : String(kid));
    return el;
}

const newId = () => 'evt-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);

// ---------- API wrapper: never throws, always returns { ok, status, data, network } ----------
async function api(path, options = {}) {
    try {
        const res = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
        let data = null;
        try { data = await res.json(); } catch { /* empty or non-JSON body */ }
        setLive(true);
        return { ok: res.ok, status: res.status, data, network: false };
    } catch {
        setLive(false);
        return { ok: false, status: 0, data: null, network: true };
    }
}

function problemText(r) {
    if (r.network) return 'Backend unavailable.';
    const d = r.data;
    if (!d) return `Request failed (HTTP ${r.status}).`;
    if (d.errors) return Object.entries(d.errors).map(([k, v]) => `${k}: ${[].concat(v).join(', ')}`).join('; ');
    return d.message || d.detail || d.title || `Request failed (HTTP ${r.status}).`;
}

function setLive(on) {
    const el = $('live');
    el.textContent = on ? 'live' : 'backend unavailable';
    el.className = 'pill ' + (on ? 'on' : 'off');
}

function notify(text, ok) {
    const el = $('notice');
    el.textContent = text;
    el.className = 'notice ' + (ok ? 'ok' : 'err');
    clearTimeout(app.noticeTimer);
    app.noticeTimer = setTimeout(() => el.classList.add('hidden'), 5000);
}

// ---------- loading ----------
async function loadJunctions() {
    const r = await api('/api/junctions');
    if (!r.ok) return;
    const sel = $('junction');
    sel.replaceChildren(...r.data.map((j) => h('option', { value: j.junction_id }, `${j.junction_id} - ${j.name}`)));
    if (!app.junction || !r.data.some((j) => j.junction_id === app.junction)) app.junction = r.data[0]?.junction_id ?? null;
    sel.value = app.junction ?? '';
}

async function poll() {
    if (!app.junction) await loadJunctions();
    if (!app.junction) return;

    const r = await api(`/api/junctions/${encodeURIComponent(app.junction)}/status`);
    if (r.ok) {
        app.status = r.data;
        renderStatus(r.data);
    } else if (r.status === 404) {
        notify(`Junction '${app.junction}' no longer exists.`, false);
        app.junction = null;
    } else if (!r.network) {
        notify(problemText(r), false);
    }

    if (app.tick++ % 2 === 0 && app.junction) {
        const hr = await api(`/api/junctions/${encodeURIComponent(app.junction)}/history?limit=40`);
        if (hr.ok) renderHistory(hr.data);
    }
}

// ---------- rendering ----------
function renderStatus(s) {
    // Intersection
    const cross = $('cross');
    cross.replaceChildren(
        cell('NORTH', s), cell('WEST', s),
        h('div', { class: 'hub' }, s.phase.replace('_', ' ')),
        cell('EAST', s), cell('SOUTH', s));

    // Banners
    const banner = $('banner');
    if (s.mode === 'EMERGENCY') {
        const e = s.emergencies.map((x) => `${x.vehicle_id} from ${x.direction}`).join(', ');
        setBanner(banner, 'emergency', `EMERGENCY MODE: ${e}. Current stage: ${s.stage}, phase ${s.phase}.`);
    } else if (s.mode === 'DEGRADED') {
        setBanner(banner, 'degraded', 'DEGRADED: the controller is not trusted. Signals are held at ALL RED until it confirms.');
    } else if (s.mode === 'MANUAL') {
        setBanner(banner, 'manual', `MANUAL OVERRIDE active for phase ${s.manual?.phase ?? '?'} (expires ${s.manual ? new Date(s.manual.expires_at).toLocaleTimeString() : '?'}).`);
    } else {
        banner.classList.add('hidden');
    }

    // Status table
    const pending = s.pending_command;
    const rows = [
        ['Mode', h('span', { class: 'badge ' + s.mode }, s.mode)],
        ['Phase / stage', `${s.phase} / ${s.stage} (${s.stage_elapsed_seconds}s)` + (s.target_phase ? `, next: ${s.target_phase}` : '')],
        ['Controller', h('span', { class: 'badge ' + s.controller_status }, s.controller_status)],
        ['Pending command', pending ? `${pending.command_id} (${pending.purpose}, attempt ${pending.attempt})` : 'none'],
        ['Desired', signalText(s.desired_signals)],
        ['Actual (confirmed)', signalText(s.actual_signals)],
    ];
    $('status').replaceChildren(h('div', { class: 'kv' }, rows.flatMap(([k, v]) => [h('b', {}, k), h('span', {}, v)])));

    // Alerts: backend alerts plus things the UI can derive from backend fields
    const alerts = s.alerts.map((a) => `${a.code}${a.direction ? ' (' + a.direction + ')' : ''}: ${a.message}`);
    if (s.desired_actual_mismatch && !pending) alerts.push('Desired and actual signal state differ.');
    if (pending) alerts.push(`Waiting for controller ACK of ${pending.command_id}.`);
    for (const [d, v] of Object.entries(s.actual_signals)) if (v === 'UNKNOWN') alerts.push(`Unknown physical state: ${d}`);
    if (s.offline_sensors.length) alerts.push('Sensor offline: ' + s.offline_sensors.join(', '));
    $('alerts').replaceChildren(...(alerts.length
        ? alerts.map((a) => h('li', { class: 'alert' }, a))
        : [h('li', { class: 'empty' }, 'No active alerts')]));

    // Queues
    $('queues').replaceChildren(...Object.keys(s.queues).map((d) => {
        const vehicles = s.queue_vehicles[d] ?? [];
        return h('div', { class: 'q' },
            h('div', {}, d), h('div', { class: 'n' }, s.queues[d]),
            h('ul', {}, vehicles.map((v) => h('li', {},
                h('span', {}, `${v.vehicle_id} · ${v.vehicle_type} · ${v.waiting_seconds}s`),
                h('button', { class: 'mini secondary', onclick: () => sendSensor('VEHICLE_CLEARED', d, v.vehicle_id) }, 'clear')))));
    }));

    $('manualState').textContent = s.mode === 'MANUAL' ? 'Manual override is ACTIVE.' : `Mode is ${s.mode}.`;
}

function cell(dir, s) {
    const desired = s.desired_signals[dir] ?? 'UNKNOWN';
    const actual = s.actual_signals[dir] ?? 'UNKNOWN';
    return h('div', { class: 'cell ' + dir.toLowerCase() },
        h('div', { class: 'dir' }, dir),
        h('div', { class: 'lamp', style: `background:${COLORS[actual]};border-color:${COLORS[desired]}`, title: `desired ${desired} / actual ${actual}` }),
        h('div', { class: 'sub' }, `want ${desired} · is ${actual}`));
}

function setBanner(el, kind, text) { el.className = 'banner ' + kind; el.textContent = text; }
function signalText(map) { return Object.entries(map).map(([d, v]) => `${d[0]}:${v}`).join('  '); }

function renderHistory(items) {
    $('history').replaceChildren(...(items.length ? items.map((e) => h('li', {},
        h('span', { class: 't' }, new Date(e.timestamp).toLocaleTimeString()),
        h('span', { class: 'k' }, e.event_type),
        e.message)) : [h('li', { class: 'empty' }, 'No activity yet')]));
}

// ---------- actions (intents and events only) ----------
async function sendSensor(eventType, direction, vehicleId, resend = false) {
    let body;
    if (resend && app.lastEvent) {
        body = app.lastEvent;                       // exact same event_id: the backend must treat it as a duplicate
    } else {
        body = {
            event_id: newId(), junction_id: app.junction, direction, event_type: eventType,
            vehicle_id: vehicleId, sequence_no: ++app.seq, timestamp: new Date().toISOString(),
        };
        if (eventType === 'VEHICLE_ARRIVED') body.vehicle_type = $('simType').value;
        app.lastEvent = body;
    }
    const r = await api('/api/sensor-events', { method: 'POST', body: JSON.stringify(body) });
    notify(r.ok ? `${r.data.outcome}: ${r.data.message}` : 'Sensor event failed: ' + problemText(r), r.ok);
    poll();
}

async function sendCommand(command, direction) {
    const body = { command, requested_by: 'dashboard-admin' };
    if (direction) body.direction = direction;
    const r = await api(`/api/junctions/${encodeURIComponent(app.junction)}/commands`, { method: 'POST', body: JSON.stringify(body) });
    notify(r.ok ? `${r.data.outcome}: ${r.data.message}` : 'Command failed: ' + problemText(r), r.ok);
    poll();
}

async function sendDeviceStatus() {
    const type = $('devType').value;
    const body = {
        event_id: newId(), junction_id: app.junction, device_type: type,
        status: $('devStatus').value, timestamp: new Date().toISOString(),
    };
    if (type !== 'SIGNAL_CONTROLLER') body.direction = $('devDir').value;
    const r = await api('/api/device-status', { method: 'POST', body: JSON.stringify(body) });
    notify(r.ok ? `${r.data.outcome}: ${r.data.message}` : 'Status failed: ' + problemText(r), r.ok);
    poll();
}

async function sendAck() {
    const pending = app.status?.pending_command;
    if (!pending) { notify('There is no pending controller command to acknowledge.', false); return; }
    const body = { command_id: pending.command_id, junction_id: app.junction, status: $('ackStatus').value };
    if ($('ackState').value) body.actual_state = $('ackState').value;
    const r = await api('/api/controller-events', { method: 'POST', body: JSON.stringify(body) });
    notify(r.ok ? `${r.data.outcome}: ${r.data.message}` : 'ACK failed: ' + problemText(r), r.ok);
    poll();
}

function bumpVehicleId() {
    const m = /^(.*?)(\d+)$/.exec($('simVehicle').value);
    $('simVehicle').value = m ? m[1] + (Number(m[2]) + 1) : 'VH-' + Date.now() % 100000;
}

// ---------- wiring ----------
$('junction').onchange = (e) => { app.junction = e.target.value; app.status = null; poll(); };
$('arriveBtn').onclick = () => sendSensor('VEHICLE_ARRIVED', $('simDir').value, $('simVehicle').value.trim());
$('clearBtn').onclick = () => sendSensor('VEHICLE_CLEARED', $('simDir').value, $('simVehicle').value.trim());
$('dupBtn').onclick = () => (app.lastEvent ? sendSensor(null, null, null, true) : notify('Send an event first.', false));
$('newIdBtn').onclick = bumpVehicleId;
$('manualBtn').onclick = () => sendCommand('MANUAL_GREEN_REQUEST', $('manualDir').value);
$('autoBtn').onclick = () => sendCommand('RETURN_TO_AUTOMATIC');
$('devBtn').onclick = sendDeviceStatus;
$('ackBtn').onclick = sendAck;
$('autoAck').onchange = async (e) => {
    const r = await api('/api/simulator/auto-ack', { method: 'POST', body: JSON.stringify({ auto_ack: e.target.checked }) });
    notify(r.ok ? `Auto-ACK is ${r.data.auto_ack ? 'ON' : 'OFF'}` : problemText(r), r.ok);
};

(async function start() {
    const sim = await api('/api/simulator');
    if (sim.ok) $('autoAck').checked = sim.data.auto_ack;
    await loadJunctions();
    await poll();
    setInterval(poll, 1000);          // polling: simple, robust, no connection state to recover (see README for why not SSE/WebSocket)
    setInterval(loadJunctions, 15000);
})();
