// A key-value store of byte arrays in IndexedDB, for what is too big for
// localStorage (the collection.nml text, the Drive file index). Bytes, not
// strings: a byte[] crosses the .NET interop boundary as binary, while a
// multi-MB string is JSON-encoded and decoded on the way, which took longer
// than downloading the file again. Every call settles: a private window,
// cleared site data or a blocked IndexedDB gives null / false, and the app then
// simply does the work again.

const DB_NAME = 'traktor-cache';
const STORE = 'kv';

let opening;

function open() {
    opening ??= new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(STORE);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error('IndexedDB blocked'));
    });
    // A failed open is not cached: the next call tries again.
    opening.catch(() => { opening = undefined; });
    return opening;
}

function run(mode, action) {
    return open().then((db) => new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, mode);
        const request = action(tx.objectStore(STORE));
        tx.oncomplete = () => resolve(request.result);
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
    }));
}

export async function get(key) {
    try {
        const value = await run('readonly', (store) => store.get(key));
        return value instanceof Uint8Array ? value : null;
    } catch {
        return null;
    }
}

export async function set(key, bytes) {
    try {
        await run('readwrite', (store) => store.put(bytes, key));
        return true;
    } catch {
        return false; // quota, private mode: next load just does the work again
    }
}

export async function remove(key) {
    try {
        await run('readwrite', (store) => store.delete(key));
        return true;
    } catch {
        return false;
    }
}
