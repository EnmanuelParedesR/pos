// Where the Lite signer keeps the key and its signing log. The browser's IndexedDB stores the CryptoKey object itself (structured clone):
// a non-extractable key stays non-extractable and its bytes never reach JavaScript. memoryStore() is the same contract for Node and the tests.

/** @typedef {{ getSigner(): Promise<any>, putSigner(v: any): Promise<void>, deleteSigner(): Promise<void>, addLog(e: object): Promise<void>, log(): Promise<object[]>,
 *   getDevice(): Promise<any>, putDevice(v: any): Promise<void>, deleteDevice(): Promise<void> }} SignerStore
 *  TK-321: the selling device's own key (device.js) is kept beside the signer's, the same way. */

/** shortcut: not run by the Node tests (no IndexedDB there; fake-indexeddb is Apache-2.0, not a small MIT dev dependency); TK-324 tests it in Safari and Chrome. */
export function indexedDbStore(name = 'abastalia-lite-signer', idb = globalThis.indexedDB) {
  let db;
  const open = () => db ??= new Promise((resolve, reject) => {
    const request = idb.open(name, 1);
    request.onupgradeneeded = () => {
      request.result.createObjectStore('keys');
      request.result.createObjectStore('log', { autoIncrement: true });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  const run = async (store, mode, op) => {
    const tx = (await open()).transaction(store, mode);
    const request = op(tx.objectStore(store));
    return new Promise((resolve, reject) => {
      tx.oncomplete = () => resolve(request.result);
      tx.onerror = tx.onabort = () => reject(tx.error);
    });
  };
  return {
    getSigner: () => run('keys', 'readonly', s => s.get('signer')),
    putSigner: value => run('keys', 'readwrite', s => s.put(value, 'signer')),
    deleteSigner: () => run('keys', 'readwrite', s => s.delete('signer')),
    getDevice: () => run('keys', 'readonly', s => s.get('device')),
    putDevice: value => run('keys', 'readwrite', s => s.put(value, 'device')),
    deleteDevice: () => run('keys', 'readwrite', s => s.delete('device')),
    addLog: entry => run('log', 'readwrite', s => s.add(entry)),
    log: () => run('log', 'readonly', s => s.getAll()),
  };
}

/** @returns {SignerStore} kept in memory: lost with the page (Node, tests, the .NET comparison's CLI). */
export function memoryStore() {
  let signer, device;
  const log = [];
  return {
    getSigner: async () => signer,
    putSigner: async value => { signer = value; },
    deleteSigner: async () => { signer = undefined; },
    getDevice: async () => device,
    putDevice: async value => { device = value; },
    deleteDevice: async () => { device = undefined; },
    addLog: async entry => { log.push(structuredClone(entry)); },
    log: async () => structuredClone(log),
  };
}
