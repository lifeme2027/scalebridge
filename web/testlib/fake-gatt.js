// 伪造的 Web Bluetooth GATT 栈（只用于单元测试；它不是真机，通过它不代表真机验证）。
class Target {
  #l = new Map();
  addEventListener(t, f) { if (!this.#l.has(t)) this.#l.set(t, new Set()); this.#l.get(t).add(f); }
  removeEventListener(t, f) { this.#l.get(t)?.delete(f); }
  listenerCount(t) { return this.#l.get(t)?.size ?? 0; }
  fire(t) { for (const f of [...(this.#l.get(t) ?? [])]) f({ target: this }); }
}
const dv = (bytes) => new DataView(Uint8Array.from(bytes).buffer);
const domErr = (name, message = name) => Object.assign(new Error(message), { name });

export class FakeCharacteristic extends Target {
  constructor(value = null) { super(); this.value = value ? dv(value) : null; this.notifying = false; this.readable = value !== null; }
  async readValue() { if (!this.readable) throw domErr('NotSupportedError'); return this.value; }
  async startNotifications() { this.notifying = true; return this; }
  async stopNotifications() { this.notifying = false; return this; }
  push(bytes) { this.value = dv(bytes); this.fire('characteristicvaluechanged'); }
}

export class FakeService {
  constructor(chars) { this.chars = chars; }
  async getCharacteristic(uuid) { const c = this.chars[uuid]; if (!c) throw domErr('NotFoundError'); return c; }
}

export class FakeDevice extends Target {
  constructor({ id = 'dev1', name = 'Fake Scale', services = {}, connectFailures = 0 } = {}) {
    super();
    this.id = id; this.name = name; this.services = services;
    this.connectFailures = connectFailures; this.connectCalls = 0; this.connected = false;
    const self = this;
    this.gatt = {
      get connected() { return self.connected; },
      async connect() {
        self.connectCalls++;
        if (self.connectFailures > 0) { self.connectFailures--; throw domErr('NetworkError', 'GATT Error: connection failed'); }
        self.connected = true;
        return { getPrimaryService: async (u) => { const s = self.services[u]; if (!s) throw domErr('NotFoundError'); return s; } };
      },
      disconnect() { if (self.connected) { self.connected = false; setTimeout(() => self.fire('gattserverdisconnected'), 0); } },
    };
  }
  drop() { this.connected = false; this.fire('gattserverdisconnected'); }
}

export class FakeBluetooth {
  constructor(deviceOrError) { this.next = deviceOrError; this.requests = []; }
  async requestDevice(opts) {
    this.requests.push(opts);
    if (this.next instanceof Error) throw this.next;
    return this.next;
  }
  async getAvailability() { return true; }
}

export { domErr };
