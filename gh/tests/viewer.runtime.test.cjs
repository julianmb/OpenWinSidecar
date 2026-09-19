const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const html = readFileSync(path.join(__dirname, '../src/OpenWinSidecar.Service/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

function viewer(hostname = '192.168.1.10', localPeer = false) {
    const elements = new Map();
    const frames = [], draws = [], sockets = [], decoders = [], errors = [];
    const docHandlers = {}, intervals = [], timeouts = [];
    let now = 1000;
    function element(id) {
        if (!elements.has(id)) elements.set(id, {
            value: id === 'codec-select' ? 'hevc' : id === 'res-select' ? '2360x1640' : id === 'fps-select' ? '30' : '0',
            width: 64, height: 48, style: { setProperty() {} }, textContent: '', innerText: '',
            classList: { add() {}, remove() {}, toggle() {} },
            addEventListener(type, fn) { ((this._h = this._h || {})[type] = (this._h[type] || [])).push(fn); },
            getBoundingClientRect: () => ({ left: 0, top: 0, width: 64, height: 48 }),
            getContext: type => type === 'webgl2' ? null : { drawImage(source) {
                assert.equal(source.closed, false, 'drawable was closed before paint');
                draws.push(source);
            } },
        });
        return elements.get(id);
    }
    class Socket {
        static OPEN = 1;
        constructor() { this.readyState = 1; this.sent = []; sockets.push(this); }
        send(value) { this.sent.push(value); }
        close() { this.readyState = 3; if (this.onclose) this.onclose(); }
    }
    class Decoder {
        static async isConfigSupported() { return { supported: true }; }
        constructor(callbacks) { this.callbacks = callbacks; this.state = 'unconfigured'; this.decodeQueueSize = 0; this.chunks = []; decoders.push(this); }
        configure() { this.state = 'configured'; }
        close() { this.state = 'closed'; }
        decode(chunk) { this.chunks.push(chunk); this.decodeQueueSize++; }
    }
    const context = vm.createContext({
        console: { log() {}, warn(...e) { errors.push(e); }, error(...e) { errors.push(e); } },
        document: { getElementById: element, addEventListener(type, fn) { ((docHandlers[type] = docHandlers[type] || [])).push(fn); }, visibilityState: 'visible', hidden: false, body: element('body') },
        navigator: { userAgent: 'test', maxTouchPoints: 0 },
        location: { protocol: 'http:', host: hostname + ':8080', hostname },
        localStorage: { getItem() { return null; } },
        performance: { now: () => now },
        setTimeout(fn) { const t = { fn, cleared: false }; t.clear = () => { t.cleared = true; }; timeouts.push(t); return t; },
        clearTimeout(t) { if (t && t.clear) t.clear(); },
        setInterval(fn) { const t = { fn, cleared: false }; t.clear = () => { t.cleared = true; }; intervals.push(t); return t; },
        clearInterval(t) { if (t && t.clear) t.clear(); },
        requestAnimationFrame: fn => frames.push(fn),
        WebSocket: Socket, VideoDecoder: Decoder, EncodedVideoChunk: class { constructor(o) { Object.assign(this, o); } },
        Blob, URL,
        createImageBitmap: async () => ({ width: 64, height: 48, closed: false, close() { this.closed = true; } }),
        atob: x => Buffer.from(x, 'base64').toString('binary'),
    });
    context.window = context;
    context.addEventListener = () => {};
    context.matchMedia = () => ({ matches: false });
    // Defined in-realm: packets must be ArrayBuffers of the viewer's own realm.
    vm.runInContext("function makeHevcPacket(bytes = 32, key = true) { const b = new Uint8Array(bytes); b[0] = 2; b[1] = key ? 1 : 0; return b.buffer; }", context);
    vm.runInContext(script.replace('/*LOCAL_PREVIEW_REQUIRED*/false', String(localPeer)), context);
    return { context, draws, sockets, decoders, errors, docHandlers, intervals, timeouts,
        run: code => vm.runInContext(code, context),
        tick: () => frames.shift()(),
        advance: ms => now += ms,
        hide: () => { context.document.hidden = true; context.document.visibilityState = 'hidden'; fire(docHandlers.visibilitychange); },
        show: () => { context.document.hidden = false; context.document.visibilityState = 'visible'; fire(docHandlers.visibilitychange); },
        runTimeouts: () => { const list = timeouts.filter(t => !t.cleared); timeouts.length = 0; list.forEach(t => t.fn()); },
        runIntervals: () => intervals.forEach(t => t.fn()),
    };
}

function fire(handlers, ev) {
    for (const fn of (handlers || [])) fn(ev);
}

function configure(v) {
    v.run("pendingHvcC = new Uint8Array([1, 2, 3]).buffer; initHevcDecoder(); needsHevcKeyframe = false;");
}

test('viewer has no host-setting controls, including hidden defaults', () => {
    for (const id of ['display', 'fps', 'quality', 'codec', 'depth', 'dpi', 'res', 'zoom'])
        assert.doesNotMatch(html, new RegExp("id='" + id + "-select'"));
});

test('reconnect and authentication never overwrite host settings', async () => {
    const v = viewer();
    v.run('connectWs()');
    const socket = v.sockets.at(-1);
    await socket.onopen();
    await socket.onmessage({ data: 'auth:ok' });
    assert.equal(socket.sent.some(m => /^(display|fps|quality|depth|dpi|set_res|zoom):/.test(m)), false);
});

test('a new server description reconfigures exactly once', () => {
    const v = viewer();
    configure(v);
    v.run('pendingHvcC = new Uint8Array([4, 5, 6]).buffer; initHevcDecoder(); initHevcDecoder();');
    assert.equal(v.decoders.length, 2);
    assert.equal(v.decoders[0].state, 'closed');
    assert.equal(v.run('needsHevcKeyframe'), true);
});

test('receipt timing measures decode and paint separately and clears each stats window', () => {
    const v = viewer();
    configure(v);
    v.run('handleBinaryPacket(makeHevcPacket());');
    v.advance(5);
    v.run(`videoDecoder.callbacks.output({ timestamp: 0, displayWidth: 64, displayHeight: 48,
        closed: false, close() { this.closed = true; } });`);
    v.advance(7);
    v.tick();
    assert.equal(v.draws.length, 1);
    v.advance(5000);
    v.runIntervals();
    const stats = () => JSON.parse(v.sockets.at(-1).sent.filter(m => m.startsWith('stats:')).at(-1).slice(6));
    assert.equal(stats().receiptToDecodeMs, 5);
    assert.equal(stats().receiptToPaintMs, 12);
    assert.equal('decodeToPaintMs' in stats(), false);
    v.advance(5000);
    v.runIntervals();
    assert.equal(stats().receiptToDecodeMs, null);
    assert.equal(stats().receiptToPaintMs, null);
});

test('JPEG bitmap ownership lasts until presentation', async () => {
    const v = viewer();
    v.run('handleBinaryPacket(new Uint8Array(32).buffer);');
    await new Promise(setImmediate);
    v.tick();
    assert.equal(v.draws.length, 1);
    assert.equal(v.draws[0].closed, true);
});

test('reconnect clears old decoder configuration, presentation, and clock offset', () => {
    const v = viewer();
    configure(v);
    v.run('clockOffsetMs = 99999; connectWs();');
    assert.equal(v.decoders[0].state, 'closed');
    assert.equal(v.run('clockOffsetMs'), null);
    assert.equal(v.run('pendingHvcC'), null);
    assert.equal(v.run('videoDecoder'), null);
    assert.equal(v.run('needsHevcKeyframe'), true);
});

test('lost-reference recovery skips deltas and retries a rate-limited keyframe request', () => {
    const v = viewer();
    configure(v);
    v.run(`needsHevcKeyframe = true; const delta = new Uint8Array(32); delta[0] = 2;
        handleBinaryPacket(delta.buffer); handleBinaryPacket(delta.buffer);`);
    assert.deepEqual(v.sockets[0].sent, ['forceidr']);
    assert.equal(v.decoders[0].chunks.length, 0);
    v.advance(9000);
    v.run('handleBinaryPacket(delta.buffer); delta[1] = 1; handleBinaryPacket(delta.buffer);');
    assert.deepEqual(v.sockets[0].sent, ['forceidr', 'forceidr']);
    assert.equal(v.decoders[0].chunks.length, 1);
    assert.equal(v.run('needsHevcKeyframe'), false);
});

function fireInput(v, type, ev) {
    v.run("ws = new WebSocket('ws://test')");
    const handlers = v.run(`(() => { const c = document.getElementById('container'); return c._h['${type}']; })()`);
    for (const fn of handlers) fn(ev);
    return v.sockets[v.sockets.length - 1].sent;
}

function canvasOf(v) { return v.run('canvas'); }
function uiOf(v) { return v.run("document.getElementById('ui-btn')"); }
const point = (target, extra = {}) => Object.assign(
    { target, pointerId: 7, pointerType: 'touch', clientX: 10, clientY: 10, preventDefault() {} }, extra);

test('taps on viewer chrome never inject input to the desktop', () => {
    const v = viewer();
    const ui = uiOf(v);
    let sent = fireInput(v, 'pointerdown', point(ui));
    assert.ok(!sent.some(m => m.startsWith('input:')), 'UI tap must not send input, got: ' + sent);
    sent = fireInput(v, 'pointermove', point(ui, { pointerType: 'mouse' }));
    assert.ok(!sent.some(m => m.startsWith('input:')), 'UI hover must not send input, got: ' + sent);
    sent = fireInput(v, 'wheel', { target: ui, deltaY: 100, preventDefault() {} });
    assert.ok(!sent.some(m => m.startsWith('scroll:')), 'UI wheel must not scroll Windows, got: ' + sent);
});

test('canvas gestures inject, and release off-canvas still lifts the button', () => {
    const v = viewer();
    const cv = canvasOf(v);
    const ui = uiOf(v);
    let sent = fireInput(v, 'pointerdown', point(cv));
    assert.ok(sent.some(m => m.startsWith('input:down,')), 'canvas tap must send down, got: ' + sent);
    // Finger slides onto the pill, then lifts there: the up must still go through,
    // or Windows would keep a stuck button held down.
    sent = fireInput(v, 'pointerup', point(ui));
    assert.ok(sent.some(m => m.startsWith('input:up,')), 'off-canvas release must send up, got: ' + sent);
    // A tap that started on UI must not produce an up either.
    sent = fireInput(v, 'pointerup', point(ui, { pointerId: 9 }));
    assert.ok(!sent.some(m => m.startsWith('input:')), 'UI-only gesture must stay silent, got: ' + sent);
});

test('settings modal links the AGPL source without hijacking the stream tab', () => {
    assert.match(html, /id='source-link'[^>]*href='https:\/\/github\.com\/julianmb\/OpenWinSidecar'/);
    assert.match(html, /id='source-link'[^>]*target='_blank'/);
});

test('two-finger scroll starting on UI never scrolls Windows', () => {
    const v = viewer();
    const ui = uiOf(v);
    const touch = (target, ys) => ({
        target, preventDefault() {},
        touches: ys.map(y => ({ clientX: 10, clientY: y })),
    });
    let sent = fireInput(v, 'touchstart', touch(ui, [10, 30]));
    sent = fireInput(v, 'touchmove', touch(ui, [0, 20]));
    assert.ok(!sent.some(m => m.startsWith('scroll:')), 'UI scroll must not reach Windows, got: ' + sent);
    sent = fireInput(v, 'touchend', { target: ui, touches: [], preventDefault() {} });
    assert.ok(!sent.some(m => m === 'rightclick'), 'UI tap must not right-click Windows, got: ' + sent);

    const cv = canvasOf(v);
    sent = fireInput(v, 'touchstart', touch(cv, [10, 30]));
    sent = fireInput(v, 'touchmove', touch(cv, [0, 10]));
    assert.ok(sent.some(m => m.startsWith('scroll:')), 'canvas scroll must reach Windows, got: ' + sent);
});

test('a single decoder error rebuilds the decoder and resyncs instead of degrading', () => {
    const v = viewer();
    configure(v);
    assert.equal(v.decoders.length, 1);
    v.run(`videoDecoder.callbacks.error(new Error('Decoder failure'));`);
    // Fresh decoder object, same description — no permanent JPEG fallback.
    assert.equal(v.decoders.length, 2);
    assert.equal(v.decoders[1].state, 'configured');
    assert.deepEqual(v.sockets[0].sent.slice(-2), ['decerr:Decoder failure', 'forceidr']);
    assert.equal(v.run(`activeCodec`), 'hevc');
    assert.equal(v.run('needsHevcKeyframe'), true);
});

test('repeated decoder errors within a minute fall back to JPEG', () => {
    const v = viewer();
    configure(v);
    for (let i = 0; i < 4; i++) v.run(`videoDecoder.callbacks.error(new Error('x'));`);
    assert.equal(v.run(`activeCodec`), 'intra');
    assert.ok(v.sockets[0].sent.includes('codec:intra'));
});

test('fresh frames expire and empty stats windows do not retain latency', () => {
    const v = viewer();
    configure(v);
    v.advance(1000);
    v.run(`clockOffsetMs = 0;
        videoDecoder.callbacks.output({ displayWidth: 64, displayHeight: 48,
            timestamp: 1950000, closed: false, close() { this.closed = true; } });`);
    v.tick();
    assert.equal(v.draws.length, 1);
    assert.equal(v.run('fpsLabel.innerText'), '1 FPS');
    assert.equal(v.run('latencySamples'), 1);
    v.advance(4000);
    v.runIntervals();
    let stats = JSON.parse(v.sockets[0].sent.filter(m => m.startsWith('stats:')).pop().slice(6));
    assert.equal(stats.latencyMs, 50);
    v.advance(7000);
    v.runIntervals();
    assert.equal(v.run('fpsLabel.textContent'), 'No recent frames · 11s ago');
    assert.equal(v.run('latLabel.textContent'), '');
    stats = JSON.parse(v.sockets[0].sent.filter(m => m.startsWith('stats:')).pop().slice(6));
    assert.equal(stats.latencyMs, null);
    assert.equal(stats.frameAgeMs, 11000);
});

test('a connection that never paints reports waiting and null latency', () => {
    const v = viewer();
    assert.equal(v.run("fpsLabel.textContent"), '⟳ reconnecting');
    v.runIntervals(); // 1s status tick after ws settles
    assert.equal(v.run("fpsLabel.textContent"), 'Waiting for frames');
    v.runIntervals();
    // The stats timer is the second registered interval (after the 1s status tick);
    // advance the clock so its 5s window has elapsed, then fire it.
    const statsTimer = v.intervals[1];
    assert.ok(statsTimer, 'stats interval must be registered');
    v.advance(5000);
    statsTimer.fn();
    const stats = v.sockets[0].sent.filter(m => m.startsWith('stats:')).pop();
    assert.ok(stats, 'stats message must be sent');
    assert.match(stats, /"latencyMs":null/);
    assert.match(stats, /"frameAgeMs":null/);
});

// ---- Bounded HEVC backlog recovery ----

test('sustained decoder backlog resets and resumes only on a keyframe', () => {
    const v = viewer();
    configure(v);
    // Fill the bounded queue: chunks decode, outputs stage latest-wins.
    for (let i = 0; i < 8; i++) v.run(`handleBinaryPacket(makeHevcPacket(32, false));`);
    assert.equal(v.decoders[0].chunks.length, 8, 'queue accepts up to the bound');
    // The 9th delta finds a full queue: full reset, not unbounded queueing.
    v.run('handleBinaryPacket(makeHevcPacket(32, false));');
    assert.equal(v.decoders.length, 2);
    assert.equal(v.decoders[0].state, 'closed');
    assert.ok(v.sockets[0].sent.includes('forceidr'), 'reset must request a fresh IDR');
    // Deltas keep draining (dropped, waiting for the reference frame)…
    v.run('handleBinaryPacket(makeHevcPacket(32, false));');
    assert.equal(v.decoders[1].chunks.length, 0);
    // …until the server keyframe re-opens the chain.
    v.run('handleBinaryPacket(makeHevcPacket(32, true));');
    assert.equal(v.decoders[1].chunks.length, 1);
    assert.equal(v.run('needsHevcKeyframe'), false);
});

test('backlog recovery failure falls back to JPEG instead of stalling', () => {
    const v = viewer();
    configure(v);
    v.run(`videoDecoder.decodeQueueSize = 8;`);
    v.run(`const oldInit = initHevcDecoder; initHevcDecoder = () => { pendingHvcC = null; return oldInit(); };`);
    v.run('handleBinaryPacket(makeHevcPacket(32, false));');
    assert.equal(v.run('activeCodec'), 'intra');
    assert.ok(v.sockets[0].sent.includes('codec:intra'));
});

// ---- Background suspension ----

test('hiding the tab closes the stream and stays disconnected until visible', () => {
    const v = viewer();
    configure(v);
    assert.equal(v.sockets[0].readyState, 1);
    v.hide();
    v.advance(3000);
    v.runTimeouts();
    assert.equal(v.sockets[0].readyState, 3, 'hidden tab must close its socket');
    assert.equal(v.run('ws'), null);
    assert.equal(v.decoders[0].state, 'closed');
    // A crash/close while hidden must not schedule a reconnect loop.
    v.runTimeouts();
    assert.equal(v.run('ws'), null);
    // Incoming packets while hidden are ignored entirely.
    v.run('handleBinaryPacket(makeHevcPacket());');
    assert.equal(v.draws.length, 0);
    v.show();
    assert.equal(v.sockets.length, 2, 'visible again must reconnect exactly once');
    assert.equal(v.run('ws'), v.sockets[1]);
});

test('late callbacks from a suspended connection cannot touch the new session', () => {
    const v = viewer();
    configure(v);
    v.hide();
    v.advance(3000);
    v.runTimeouts();
    v.show(); // second socket becomes current
    assert.equal(v.run('ws'), v.sockets[1]);
    // The stale socket's onclose fires late: its guard (ws !== socket) must ignore it —
    // no third connection, no rescheduled reconnect, no interference with the new session.
    const sentBefore = v.sockets[1].sent.length;
    v.sockets[0].onclose();
    assert.equal(v.sockets.length, 2, 'no third connection may appear');
    assert.equal(v.sockets[1].sent.length, sentBefore);
});

test('resuming a hidden tab preserves host settings and announces codec capability', async () => {
    const v = viewer();
    configure(v);
    v.run('clockOffsetMs = 0;');
    v.hide();
    v.advance(3000);
    v.runTimeouts();
    v.show();
    await v.run('ws.onopen();');
    const sent = v.sockets[1].sent;
    assert.ok(!sent.some(m => m.startsWith('set_res:')), 'resume must preserve host resolution');
    assert.ok(!sent.some(m => m.startsWith('display:')), 'resume must preserve host display');
    assert.ok(sent.includes('codec:hevc'), 'resume must re-announce the codec');
});


test('repeated backlog recovery cannot restart the encoder more than once per cooldown', () => {
    const v = viewer();
    configure(v);
    for (let i = 0; i < 3; i++) {
        v.run('videoDecoder.decodeQueueSize = 8; handleBinaryPacket(makeHevcPacket(32, true));');
    }
    assert.equal(v.sockets[0].sent.filter(m => m === 'forceidr').length, 1);
    v.advance(8500);
    v.run('videoDecoder.decodeQueueSize = 8; handleBinaryPacket(makeHevcPacket(32, false));');
    assert.equal(v.sockets[0].sent.filter(m => m === 'forceidr').length, 2);
});

test('output and errors from a discarded decoder cannot affect its replacement', () => {
    const v = viewer();
    configure(v);
    const old = v.decoders[0];
    v.run('videoDecoder.decodeQueueSize = 8; handleBinaryPacket(makeHevcPacket(32, false));');
    const frame = { closed: false, close() { this.closed = true; } };
    old.callbacks.output(frame);
    old.callbacks.error(new Error('late error'));
    assert.equal(frame.closed, true);
    assert.equal(v.run('hasPendingDrawable'), false);
    assert.equal(v.decoders.length, 2);
    assert.equal(v.run('hevcErrorCount'), 0);
});

test('JPEG decoding is bounded to one active decode and one latest waiting frame', async () => {
    const v = viewer();
    const resolvers = [];
    v.context.createImageBitmap = () => new Promise(resolve => resolvers.push(resolve));
    v.run('for (let i = 0; i < 20; i++) handleBinaryPacket(new Uint8Array(32).buffer);');
    assert.equal(resolvers.length, 1);
    assert.equal(v.run('jpegDrops'), 18);
    const bitmap = () => ({ width: 64, height: 48, closed: false, close() { this.closed = true; } });
    resolvers[0](bitmap());
    await new Promise(setImmediate);
    assert.equal(resolvers.length, 2);
    v.tick();
    assert.equal(v.draws.length, 1);
    const late = bitmap();
    v.hide();
    v.advance(3000);
    v.runTimeouts();
    v.show();
    resolvers[1](late);
    await new Promise(setImmediate);
    v.tick();
    assert.equal(late.closed, true);
    assert.equal(v.draws.length, 1, 'an old session bitmap must not paint after resume');
});


test('brief tab switch preserves the socket and HEVC references without a keyframe request', () => {
    const v = viewer();
    configure(v);
    v.hide();
    v.advance(1000);
    v.run('handleBinaryPacket(makeHevcPacket(32, false));');
    assert.equal(v.decoders[0].chunks.length, 1);
    v.show();
    v.runTimeouts();
    assert.equal(v.sockets.length, 1);
    assert.equal(v.decoders[0].state, 'configured');
    assert.equal(v.run('needsHevcKeyframe'), false);
    assert.ok(!v.sockets[0].sent.includes('forceidr'));
});

test('return after grace expiry reconnects even if Safari froze the timer', () => {
    const v = viewer();
    configure(v);
    v.hide();
    v.advance(4000);
    v.show();
    assert.equal(v.sockets.length, 2);
    assert.equal(v.decoders[0].state, 'closed');
});

for (const host of ['localhost', '127.0.0.1', '[::1]']) {
    test('local preview requires consent for ' + host, () => {
        const v = viewer(host);
        assert.equal(v.sockets.length, 0);
        v.hide(); v.show(); v.runTimeouts();
        assert.equal(v.sockets.length, 0);
        v.run('startLocalPreview(); startLocalPreview();');
        assert.equal(v.sockets.length, 1);
        assert.equal(v.run('document.getElementById("local-preview-overlay").hidden'), true);
    });
}

test('server-identified local LAN peer also requires preview consent', () => {
    const v = viewer('192.168.1.10', true);
    assert.equal(v.sockets.length, 0);
    v.run('startLocalPreview();');
    assert.equal(v.sockets.length, 1);
});
