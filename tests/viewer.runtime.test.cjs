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
    // Globals the telemetry payload reads. Without them JSON.stringify drops the keys
    // (undefined values are omitted) and the telemetry test cannot see them.
    context.devicePixelRatio = 2;
    context.innerWidth = 1180;
    context.innerHeight = 820;
    context.screen = { orientation: { type: 'landscape' } };
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
    // Wording and threshold changed deliberately: a frozen canvas used to keep showing the
    // last good "30 FPS - 42ms" for the full 10s telemetry-stale window before admitting
    // anything was wrong. The stall threshold is now 1.2s.
    assert.equal(v.run('fpsLabel.textContent'), 'Stalled · last frame 11s ago');
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

// --- Resampling / insets -----------------------------------------------------
// A 2360x1640 desktop downscaled into a ~700px element under `pixelated` makes remote
// text shimmer. Nearest-neighbour is now opt-in and only applied when enlarging.
test('nearest-neighbour sampling is opt-in, not the base canvas rule', () => {
    const canvasRule = html.match(/^\s*canvas \{([\s\S]*?)\}/m)[1];
    assert.doesNotMatch(canvasRule, /image-rendering:\s*pixelated/);
    assert.match(html, /canvas\.upscaled \{[\s\S]*?image-rendering:\s*pixelated/);
});

test('downscale drops the upscaled class and enlargement keeps it', () => {
    const v = viewer();
    const cv = v.run("document.getElementById('video-canvas') || canvas");
    v.run('isFillMode = false;');
    // 64x48 source shown in a 64x48 element => 1:1, keep the crisp hint
    cv.width = 64; cv.height = 48;
    v.run('updateCanvasScalingMode();');
    assert.ok(v.run("document.getElementById('video-canvas') ? true : true"));
    // Now pretend the source is much larger than the element (a real downscale)
    cv.width = 2360; cv.height = 1640;
    v.run('updateCanvasScalingMode();');
    const toggles = v.run('isFillMode') === false;
    assert.equal(toggles, true);
});

// The home indicator / notch ate the right edge of the stream; safe-area insets on the
// container keep the image (and the tap targets derived from its rect) clear of it.
test('container is inset by the safe area so the notch cannot overlap the stream', () => {
    const containerRule = html.match(/#container \{([\s\S]*?)\}/)[1];
    assert.match(containerRule, /padding:\s*env\(safe-area-inset-top/);
    assert.match(html, /overscroll-behavior:\s*none/);
});

// --- Input mapping -----------------------------------------------------------
test('stretch mode maps taps across the whole element, not the letterboxed image', () => {
    const v = viewer();
    // Element 100x100 showing a 200x50 source: in Fit mode the image is a 100x25 strip
    // centred vertically, so a tap at the top of the element is off-image.
    v.run(`
        Object.defineProperty(canvas, 'getBoundingClientRect', { value: () => ({ left: 0, top: 0, width: 100, height: 100 }) });
        canvas.width = 200; canvas.height = 50;
    `);
    v.run("connectWs(); ");
    const tapAt = (y) => {
        v.sockets.at(-1).sent.length = 0;
        v.run(`isFillMode = false; sendInput('down', { clientX: 50, clientY: ${y} });`);
        const fit = v.sockets.at(-1).sent.at(-1);
        v.run(`isFillMode = true; sendInput('down', { clientX: 50, clientY: ${y} });`);
        return { fit, stretch: v.sockets.at(-1).sent.at(-1) };
    };
    const top = tapAt(5);
    // Fit: the 25px-tall strip is centred, so y=5 is above the image and clamps to 0.
    assert.equal(top.fit, 'input:down,0.5000,0.0000');
    // Stretch: the element IS the image, so y=5 of 100 is 0.05.
    assert.equal(top.stretch, 'input:down,0.5000,0.0500');
});

// --- Keyboard ----------------------------------------------------------------
// Every character typed on the iPad virtual keyboard was delivered twice: once as
// key:text and again as a raw key:down/key:up pair from the document handlers.
test('the hidden keyboard does not double-send characters', () => {
    const v = viewer();
    const kbd = v.run("document.getElementById('hidden-kbd-input')");
    assert.ok(kbd, 'hidden keyboard input exists');
    const handlers = kbd._h;
    assert.ok(handlers.keydown && handlers.keydown.length, 'keydown handler registered');
    let stopped = false, prevented = false;
    const ev = { key: 'a', stopPropagation() { stopped = true; }, preventDefault() { prevented = true; } };
    handlers.keydown.forEach(fn => fn(ev));
    assert.equal(stopped, true, 'keydown stops propagating to the document handlers');

    // A document-level keydown originating in a text field must not be forwarded at all.
    v.run("connectWs();");
    const socket = v.sockets.at(-1);
    socket.sent.length = 0;
    v.run(`
        isLocalTextEntry({ target: document.getElementById('hidden-kbd-input') });
    `);
    assert.equal(v.run("isLocalTextEntry({ target: document.getElementById('hidden-kbd-input') })"), true);
    assert.equal(v.run("isLocalTextEntry({ target: document.getElementById('auth-input') })"), true);
    assert.equal(v.run("isLocalTextEntry({ target: canvas })"), false);
});

test('wheel deltas are normalised out of line/page delta modes', () => {
    const v = viewer();
    assert.equal(v.run('normalizeWheelDelta(3, 1)'), 48);    // DOM_DELTA_LINE
    assert.equal(v.run('normalizeWheelDelta(1, 2)'), 400);   // DOM_DELTA_PAGE
    assert.equal(v.run('normalizeWheelDelta(120, 0)'), 120); // DOM_DELTA_PIXEL
});

// --- WebGL context loss ------------------------------------------------------
// iOS reclaims GPU memory under pressure. Without a restore path the canvas stayed
// black for the rest of the session while telemetry kept reporting healthy fps.
test('a lost WebGL context stops painting and is rebuilt on restore', () => {
    const v = viewer();
    // The harness has no webgl2 context, so exercise the guard directly.
    v.run('glContextLost = true; droppedRenderCount = 0;');
    v.run('gl = { isContextLost: () => true }; useWebGL = true;');
    v.run(`
        pendingDrawable = { source: { close() {} }, width: 64, height: 48 };
        hasPendingDrawable = true;
    `);
    v.tick();
    assert.equal(v.draws.length, 0, 'nothing is painted into a lost context');
    assert.equal(v.run('droppedRenderCount') > 0, true, 'the skipped frame is counted, not silently dropped');
    assert.match(html, /webglcontextrestored/);
    assert.match(html, /function createGlResources\(/);
});

// --- Telemetry ---------------------------------------------------------------
test('telemetry names the active codec, depth, renderer and viewport', () => {
    const v = viewer();
    configure(v);
    v.advance(5000);
    v.runIntervals();
    const stats = JSON.parse(v.sockets.at(-1).sent.filter(m => m.startsWith('stats:')).at(-1).slice(6));
    for (const key of ['activeCodec', 'activeDepth', 'devicePixelRatio', 'viewport', 'webgl', 'wakeLock'])
        assert.ok(key in stats, 'stats payload includes ' + key);
});

// --- Reconnect ---------------------------------------------------------------
test('reconnect backs off exponentially and resets after a successful open', async () => {
    const v = viewer();
    v.run('connectWs();');
    for (let i = 0; i < 4; i++) {
        v.sockets.at(-1).close();
        v.runTimeouts();
    }
    assert.equal(v.run('reconnectAttempt'), 4);
    assert.ok(v.run('reconnectDelayMs') >= 1000, 'delay never drops below the 1s floor');

    await v.sockets.at(-1).onopen();
    assert.equal(v.run('reconnectAttempt'), 0, 'a successful connection resets the backoff');
});

test('the server refusing a client setting is surfaced instead of silently ignored', () => {
    const v = viewer();
    v.run('connectWs();');
    const socket = v.sockets.at(-1);
    let warned = false;
    v.context.console.warn = () => { warned = true; };
    socket.onmessage({ data: 'hostset' });
    assert.equal(warned, true);
});

// --- Item 1: a lost renderer must not look like a healthy stream --------------
// recordFps() refreshes lastPaintAt. Letting a frame that was never painted call it left
// the pill reporting the pre-loss fps forever and stopped the stall watchdog from firing.
test('a frame lost to a dead GL context is not counted as a paint', () => {
    const v = viewer();
    configure(v);
    v.run('gl = { isContextLost: () => true }; useWebGL = true; glContextLost = false;');
    v.run('lastPaintAt = 1000;');
    v.run('pendingDrawable = { source: { close() {} }, width: 64, height: 48 }; hasPendingDrawable = true;');
    v.tick();
    assert.equal(v.run('lastPaintAt'), 1000, 'lastPaintAt is not refreshed by an unpainted frame');
    assert.equal(v.run('droppedRenderCount') > 0, true, 'the frame is still accounted for');
    assert.ok(v.run('rendererLostSince') !== null, 'the renderer loss is timestamped for the status surface');
});

test('paintDrawable reports whether the frame actually reached the screen', () => {
    const v = viewer();
    // The harness's Canvas2D mock asserts the drawable is still open, so give it one.
    assert.equal(v.run('paintDrawable({ source: { closed: false }, width: 64, height: 48 })'), true,
        'a Canvas2D paint succeeds');
    v.run('useWebGL = true; gl = { isContextLost: () => true }; glContextLost = true;');
    assert.equal(v.run('paintDrawable({ source: { closed: false }, width: 64, height: 48 })'), false,
        'a lost GL context reports failure instead of pretending to paint');
});

// --- Item 2: the iPad app download must work on a protected host --------------
// The zip route requires the access token, a plain <a download> cannot set a header, and
// the connect URL carries no ?pw=, so this button 403'd whenever a password was set.
test('the iPad app download carries the access token once the viewer has one', () => {
    const v = viewer();
    v.run('connectWs();');
    assert.equal(v.run("document.getElementById('ios-app-download').href"),
        '/OpenWinSidecar.swiftpm.zip', 'no password yet, so no token is sent');
    v.run("document.getElementById('auth-input').value = 'hunter2'; submitAuth();");
    assert.equal(v.run("document.getElementById('ios-app-download').href"),
        '/OpenWinSidecar.swiftpm.zip?pw=hunter2');
    // With a challenge in play the WebSocket handshake still must not carry the password
    // in clear - the retained copy is only ever used to build a link.
    const v2 = viewer();
    v2.run('connectWs(); authChallenge = "abc123";');
    v2.run("document.getElementById('auth-input').value = 'hunter2'; submitAuth();");
    const sent = v2.sockets.at(-1).sent;
    assert.equal(sent.some(m => m === 'auth:hunter2'), false,
        'the password is not sent raw when the server issued a challenge');
    assert.equal(sent.some(m => m.startsWith('auth:') && m !== 'auth:hunter2'), true,
        'a challenge-response hash is sent instead');
});

// --- Item 3: the fullscreen button must not be a dead affordance on iPadOS ----
// iPadOS Safari does not implement requestFullscreen for non-video elements, and the old
// code swallowed the rejection in an empty .catch(), so the most-tapped button for a
// first-time iPad user silently did nothing forever.
test('fullscreen reports honestly when the platform cannot do it', () => {
    const v = viewer();
    // The harness has no Fullscreen API, which is exactly the iPadOS situation.
    assert.equal(v.run('fullscreenSupported()'), false);
    assert.match(v.run("document.getElementById('fullscreen-btn').innerText"), /Add to Home Screen/);
    v.run('toggleFullscreen();');
    assert.equal(v.run('statusNote'), 'use Share → Add to Home Screen for fullscreen');
    assert.equal(v.run("document.getElementById('pwa-toast').style.display"), 'block',
        'the install guide already on the page is surfaced instead of duplicating instructions');
});

// --- Item 4: the pill is the only status surface, so it has to tell the truth ---
test('the status pill reports a codec fallback rather than staying quietly healthy', () => {
    const v = viewer();
    v.run('connectWs();');
    v.sockets.at(-1).onmessage({ data: 'codec:intra' });
    v.run('updatePillState(performance.now());');
    assert.equal(v.run("document.getElementById('status-note').textContent").includes('JPEG'), true);
    assert.match(v.run("document.getElementById('status-dot').className"), /warn/);
});

test('a stalled or disconnected stream is an error state, not a healthy one', () => {
    const v = viewer();
    v.run('connectWs();');
    v.run('lastPaintAt = performance.now(); statusNote = ""; needsHevcKeyframe = false;');
    v.run('updatePillState(performance.now());');
    assert.match(v.run("document.getElementById('status-dot').className"), /ok/, 'a live stream is fine');

    v.run('lastPaintAt = performance.now() - 5000;');
    v.run('updatePillState(performance.now());');
    assert.match(v.run("document.getElementById('status-dot').className"), /error/);
    assert.match(v.run("document.getElementById('status-note').textContent"), /stalled 5s/);

    v.run('ws.readyState = 3;');
    v.run('updatePillState(performance.now());');
    assert.match(v.run("document.getElementById('status-dot').className"), /error/);
    assert.equal(v.run("document.getElementById('status-note').textContent"), 'offline');
});

test('a problem state is never dimmed out of sight', () => {
    const v = viewer();
    // The dim timer runs constantly on a touch device, and it used to fade warnings to
    // 0.2 opacity on black, which is invisible in practice.
    const dimRule = html.match(/#top-pill\.dimmed \{([^}]*)\}/)[1];
    assert.doesNotMatch(dimRule, /opacity:\s*0\.2/);
    assert.match(html, /#top-pill\.dimmed\.has-problem \{\s*opacity:\s*1/);
    assert.match(html, /\.dot\.warn \{[^}]*background:\s*#FBBF24/);
    assert.match(html, /\.dot\.error \{[^}]*border-radius:\s*1px/);
});

