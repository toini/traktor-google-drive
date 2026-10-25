// Cast Web Sender. The Cast device fetches the media URL itself, so it needs the
// absolute proxy URL — a relative one only works for <audio> in this page. That
// URL carries a ticket from the server, never the Google token (see below).

const SENDER_SDK = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1';

let dotnet = null;
let player = null;
let controller = null;
let currentId = null;
let available = false;
let lastKey = '';

const framework = () => window.cast?.framework;
const context = () => framework()?.CastContext.getInstance();

function snapshot() {
    const session = available ? context()?.getCurrentSession() : null;
    return {
        available,
        connected: !!player?.isConnected,
        deviceName: session?.getCastDevice?.()?.friendlyName ?? null,
        state: player?.playerState ?? null,
        fileId: currentId,
        currentTime: player?.currentTime ?? 0,
        duration: player?.duration ?? 0,
        // IDLE means both "nothing loaded yet" and "finished"; only the media
        // session can tell them apart, and auto-advance needs to.
        idleReason: session?.getMediaSession?.()?.idleReason ?? null,
    };
}

// ANY_CHANGE fires many times a second; collapse it to one push per visible
// change so a 500-row playlist is not re-rendered on every frame.
function push() {
    const s = snapshot();
    const key = [s.available, s.connected, s.deviceName, s.state, s.idleReason, s.fileId,
                 Math.floor(s.currentTime), Math.floor(s.duration)].join('|');
    if (key === lastKey) return;
    lastKey = key;
    // Nothing awaits this, so without the catch a rejected interop call is silent.
    dotnet?.invokeMethodAsync('OnCastChanged', s)
        ?.catch((e) => console.error('cast: OnCastChanged failed', e));
}

function fail(detail) {
    dotnet?.invokeMethodAsync('OnCastFailed', String(detail ?? 'unknown'));
}

function start() {
    const cast = framework();
    const chromeCast = window.chrome?.cast;
    if (!cast || !chromeCast) return;

    context().setOptions({
        receiverApplicationId: chromeCast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
        autoJoinPolicy: chromeCast.AutoJoinPolicy.ORIGIN_SCOPED,
    });

    player = new cast.RemotePlayer();
    controller = new cast.RemotePlayerController(player);
    // ANY_CHANGE rather than the individual property events: the set of event
    // names is large and this one cannot drift out of date.
    controller.addEventListener(cast.RemotePlayerEventType.ANY_CHANGE, push);

    available = true;
    push();
}

export function init(dotNetRef) {
    dotnet = dotNetRef;
    if (framework()) { start(); return; }

    // The SDK announces itself through this global, so it has to exist before
    // the script runs.
    window.__onGCastApiAvailable = (ok) => (ok ? start() : push());

    const script = document.createElement('script');
    script.src = SENDER_SDK;
    // A browser without Cast (Firefox, Safari) never resolves this; report so
    // the button can say so instead of spinning.
    script.onerror = () => push();
    document.head.appendChild(script);
}

export async function connect() {
    if (!available) return;
    try {
        await context().requestSession();
    } catch (e) {
        // "cancel" is the user dismissing the device chooser, not a failure.
        const code = e?.code ?? e;
        if (code !== 'cancel') fail(e?.description ?? code);
    }
    push();
}

export function disconnect() {
    if (!available) return;
    context().endCurrentSession(true);
    currentId = null;
    push();
}

export async function load(fileId, url, contentType, title, artist, startTime) {
    if (!available) return false;
    const session = context().getCurrentSession();
    if (!session) return false;

    const media = new window.chrome.cast.media.MediaInfo(url, contentType);
    media.streamType = window.chrome.cast.media.StreamType.BUFFERED;

    const metadata = new window.chrome.cast.media.MusicTrackMediaMetadata();
    metadata.title = title ?? '';
    metadata.artist = artist ?? '';
    media.metadata = metadata;

    const request = new window.chrome.cast.media.LoadRequest(media);
    request.autoplay = true;
    request.currentTime = startTime || 0;

    currentId = fileId;
    try {
        await session.loadMedia(request);
        push();
        return true;
    } catch (code) {
        currentId = null;
        fail(code);
        push();
        return false;
    }
}

// Cast media tickets. The device's URL carries a ticket, never the Google token:
// the token goes to the server in the Authorization header, the ticket and its
// renew key in the body, so none of them lands in a URL or a request log.
const TICKETS = '/api/cast/tickets';

async function ticketCall(path, token, body) {
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers.Authorization = `Bearer ${token}`;
    return fetch(path, { method: 'POST', headers, body: JSON.stringify(body), cache: 'no-store' });
}

/** { status, ticket, renewKey, expiresAt } — status 0 when the server was unreachable. */
export async function mintTicket(fileId, token, tokenExpiresAt) {
    try {
        const r = await ticketCall(TICKETS, token, { fileId, tokenExpiresAt: tokenExpiresAt || null });
        if (!r.ok) return { status: r.status, ticket: null, renewKey: null, expiresAt: 0 };
        const body = await r.json();
        return { status: r.status, ticket: body.ticket, renewKey: body.renewKey, expiresAt: body.expiresAt };
    } catch (e) {
        console.error('cast: minting a ticket failed', e);
        return { status: 0, ticket: null, renewKey: null, expiresAt: 0 };
    }
}

/** HTTP status: 204 renewed, 410 ticket past its cap, 403 refused, 0 unreachable. */
export async function renewTicket(ticket, renewKey, token, tokenExpiresAt) {
    try {
        const r = await ticketCall(`${TICKETS}/renew`, token, { ticket, renewKey, tokenExpiresAt: tokenExpiresAt || null });
        return r.status;
    } catch (e) {
        console.error('cast: renewing the ticket failed', e);
        return 0;
    }
}

/** Best effort: an unrevoked ticket still dies with its token. */
export function revokeTicket(ticket, renewKey) {
    ticketCall(`${TICKETS}/revoke`, null, { ticket, renewKey })
        .catch((e) => console.warn('cast: revoking the ticket failed', e));
}

export function playOrPause() {
    if (player?.isConnected) controller?.playOrPause();
}

export function stop() {
    if (player?.isConnected) controller?.stop();
    currentId = null;
    push();
}

export function seek(seconds) {
    if (!player?.isConnected) return;
    player.currentTime = seconds;
    controller.seek();
}

export function dispose() {
    controller = null;
    player = null;
    dotnet = null;
    available = false;
}
