const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const html = readFileSync(path.join(__dirname, '../src/OpenWinSidecar.Service/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

function viewer() {
    const elements = new Map();
    const frames = [], draws = [], sockets = [], decoders = [], errors = [];
    let now = 1000;
    function element(id) {
        if (!elements.has(id)) elements.set(id, {
            value: id === 'codec-select' ? 'hevc' : id === 'res-select' ? '2360x1640' : '0',
            width: 64, height: 48, style: { setProperty() {} },
            classList: { add() {}, remove() {}, toggle() {} }, addEventListener() {},
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
    }
    class Decoder {
        static async isConfigSupported() { return { supported: true }; }
        constructor(callbacks) { this.callbacks = callbacks; this.state = 'unconfigured'; this.decodeQueueSize = 0; this.chunks = []; decoders.push(this); }
        configure() { this.state = 'configured'; }
        close() { this.state = 'closed'; }
        decode(chunk) { this.chunks.push(chunk); }
    }
    const context = vm.createContext({
        console: { log() {}, warn(...e) { errors.push(e); }, error(...e) { errors.push(e); } },
        document: { getElementById: element, addEventListener() {}, visibilityState: 'visible', body: element('body') },
        navigator: { userAgent: 'test', maxTouchPoints: 0 },
        location: { protocol: 'http:', host: 'localhost:8080' },
        localStorage: { getItem() { return null; } },
        performance: { now: () => now },
        setTimeout() {}, clearTimeout() {}, setInterval() {},
        requestAnimationFrame: fn => frames.push(fn),
        WebSocket: Socket, VideoDecoder: Decoder, EncodedVideoChunk: class { constructor(o) { Object.assign(this, o); } },
        Blob, URL,
        createImageBitmap: async () => ({ width: 64, height: 48, closed: false, close() { this.closed = true; } }),
        atob: x => Buffer.from(x, 'base64').toString('binary'),
    });
    context.window = context;
    context.addEventListener = () => {};
    context.matchMedia = () => ({ matches: false });
    vm.runInContext(script, context);
    return { context, draws, sockets, decoders, errors,
        run: code => vm.runInContext(code, context),
        tick: () => frames.shift()(),
        advance: ms => now += ms,
    };
}

function configure(v) {
    v.run("pendingHvcC = new Uint8Array([1, 2, 3]).buffer; initHevcDecoder(); needsHevcKeyframe = false;");
}

test('ordinary presets explicitly request 60 Hz; 120 Hz remains opt-in', () => {
    const v = viewer();
    assert.equal(v.run('getOptimalResolution().hz'), 60);
    v.run("resSelect.value = '2360x1640@120'");
    assert.equal(v.run('getOptimalResolution().hz'), 120);
});

test('repeated resize/fullscreen sync preserves decoder references and deduplicates requests', () => {
    const v = viewer();
    configure(v);
    v.run('syncResolutionNow(); syncResolutionNow(); initHevcDecoder();');
    assert.equal(v.decoders.length, 1);
    assert.equal(v.decoders[0].state, 'configured');
    assert.equal(v.run('needsHevcKeyframe'), false);
    assert.deepEqual(v.sockets[0].sent, ['set_res:2360,1640,60']);
});

test('a new server description reconfigures exactly once', () => {
    const v = viewer();
    configure(v);
    v.run('pendingHvcC = new Uint8Array([4, 5, 6]).buffer; initHevcDecoder(); initHevcDecoder();');
    assert.equal(v.decoders.length, 2);
    assert.equal(v.decoders[0].state, 'closed');
    assert.equal(v.run('needsHevcKeyframe'), true);
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

test('a single decoder error rebuilds the decoder and resyncs instead of degrading', () => {
    const v = viewer();
    configure(v);
    assert.equal(v.decoders.length, 1);
    v.run(`videoDecoder.callbacks.error(new Error('Decoder failure'));`);
    // Fresh decoder object, same description — no permanent JPEG fallback.
    assert.equal(v.decoders.length, 2);
    assert.equal(v.decoders[1].state, 'configured');
    assert.deepEqual(v.sockets[0].sent.slice(-2), ['decerr:Decoder failure', 'forceidr']);
    assert.equal(v.run(`codecSelect.value`), 'hevc');
    assert.equal(v.run('needsHevcKeyframe'), true);
});

test('repeated decoder errors within a minute fall back to JPEG', () => {
    const v = viewer();
    configure(v);
    for (let i = 0; i < 4; i++) v.run(`videoDecoder.callbacks.error(new Error('x'));`);
    assert.equal(v.run(`codecSelect.value`), 'intra');
    assert.ok(v.sockets[0].sent.includes('codec:intra'));
});
