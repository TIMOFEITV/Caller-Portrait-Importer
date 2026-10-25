// Static smoke test for the embedded OBS overlay JavaScript.
// Run: node tests/overlay-smoke.js
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
const path = require('path');
const source = fs.readFileSync(path.join(__dirname, '..', 'OverlayServer.cs'), 'utf8');
const match = source.match(/private const string Page = @"([\s\S]*?)";\s*\}\s*\}\s*$/);
assert(match, 'C# Page constant not found');
const html = match[1].replace(/""/g, '"');
const script = html.match(/<script>([\s\S]*?)<\/script>/);
assert(script, 'Embedded JS not found');
new vm.Script(script[1], { filename: 'OBS overlay inline.js' });

function element() {
    return {
        textContent: '', children: [], className: '',
        classList: { _items: new Set(['off']), toggle(name, enabled) {
            if (enabled) this._items.add(name); else this._items.delete(name);
        }, add(name) { this._items.add(name); }, contains(name) { return this._items.has(name); } },
        append(...items) { this.children.push(...items); },
        replaceChildren() { this.children = []; }
    };
}
const elements = {};
for (const id of ['panel', 'viewer', 'phase', 'codes', 'spoken']) elements[id] = element();
let nextResponse = { active: true, viewer: 'Viewer <img src=x>', phase: 'LIVE', lastSpeech: 'Hi', codes: [
    { label: 'Gift card', value: '1234-5678', claimed: false, revealed: false }
]};
let interval;
const context = {
    document: { getElementById(id) { return elements[id]; }, createElement() { return element(); } },
    fetch: async () => {
        if (nextResponse === null) throw new Error('disconnected');
        return { ok: true, json: async () => nextResponse };
    },
    AbortController, setTimeout: () => 1, clearTimeout: () => {},
    setInterval: fn => { interval = fn; }
};
function drain() { return new Promise(resolve => setTimeout(resolve, 10)); }
(async () => {
    vm.runInNewContext(script[1], context);
    await drain();
    assert(!elements.panel.classList.contains('off'), 'active overlay did not appear');
    assert.strictEqual(elements.viewer.textContent, 'Viewer <img src=x>', 'name not displayed as text');
    assert.strictEqual(elements.codes.children.length, 1, 'code did not render');
    nextResponse = { active: true, viewer: 'Viewer', phase: 'LIVE', lastSpeech: '', codes: [] };
    interval(); await drain();
    assert.strictEqual(elements.codes.children.length, 0, 'code was not removed');
    nextResponse = null;
    for (let i=0;i<3;i++) { interval(); await drain(); }
    assert(elements.panel.classList.contains('off'), 'overlay remained visible when server disconnected');
    console.log('PASS: overlay JS parses, renders text without HTML injection, clears codes, and hides stale state.');
})().catch(e => { console.error(e); process.exitCode = 1; });
