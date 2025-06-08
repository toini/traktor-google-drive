let audioContext = null;
let currentSource = null;
let currentBuffer = null;
let startTime = 0;
let offsetAtStart = 0;

window.secureStreamToAudio = async (fileId, token, mime = "audio/mpeg", seekSeconds = 0) => {
    if (!audioContext || audioContext.state === "closed")
        audioContext = new AudioContext();
    if (currentSource)
        currentSource.stop();

    if (mime === "audio/flac")
        return streamFlac(fileId, token, seekSeconds);
    if (mime === "audio/wav" || mime === "audio/wave" || mime === "audio/x-wav")
        return streamWav(fileId, token, seekSeconds);
    if (mime.startsWith("audio/")) {
        return streamViaBlob(fileId, token, mime);
    }

    console.error("Unsupported mime type", mime);
};

async function streamWav(fileId, token, seekSeconds = 0) {
    const context = audioContext;
    const res = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
        headers: { Authorization: `Bearer ${token}` }
    });
    const buf = await res.arrayBuffer();
    const decoded = await context.decodeAudioData(buf);

    currentBuffer = decoded;
    currentSource = context.createBufferSource();
    currentSource.buffer = decoded;
    currentSource.connect(context.destination);
    await context.resume();

    offsetAtStart = Math.min(seekSeconds, decoded.duration);
    startTime = context.currentTime;
    currentSource.start(0, offsetAtStart);
}

async function streamFlac(fileId, token, seekSeconds = 0) {
    if (typeof Flac === 'undefined') {
        console.error("libflac.js not loaded");
        return;
    }

    await new Promise(resolve => {
        if (Flac.isReady()) resolve();
        else Flac.on('ready', resolve);
    });

    const context = audioContext;
    const res = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
        headers: { Authorization: `Bearer ${token}` }
    });
    const reader = res.body.getReader();
    const chunks = [];
    while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        chunks.push(...value);
    }
    const data = new Uint8Array(chunks);
    let offset = 0;
    const decoder = Flac.create_libflac_decoder(true);

    const pcmChunks = [];
    let metadata = null;

    const read_cb = size => {
        const end = offset >= data.length ? -1 : Math.min(offset + size, data.length);
        if (end === -1) return { buffer: null, readDataLength: 0, error: false };
        const chunk = data.subarray(offset, end);
        offset = end;
        return { buffer: chunk, readDataLength: chunk.length, error: false };
    };

    const write_cb = (chBufs) => {
        const scale = 1 / (1 << ((metadata?.bitsPerSample ?? 16) - 1));
        const converted = chBufs.map(buf => {
            const out = new Float32Array(buf.length);
            for (let i = 0; i < buf.length; i++) out[i] = buf[i] * scale;
            return out;
        });
        pcmChunks.push(converted);
    };

    const meta_cb = m => metadata = m;
    const err_cb = (c, m) => console.error("FLAC error", c, m);

    Flac.FLAC__stream_decoder_set_metadata_respond(decoder);
    const ok = Flac.init_decoder_stream(decoder, read_cb, write_cb, err_cb, meta_cb);
    if (ok !== 0) return;

    Flac.FLAC__stream_decoder_process_until_end_of_stream(decoder);
    Flac.FLAC__stream_decoder_finish(decoder);
    Flac.FLAC__stream_decoder_delete(decoder);

    const ch = metadata.channels;
    const rate = metadata.sampleRate;
    const len = pcmChunks.reduce((sum, c) => sum + c[0].length, 0);
    const buf = context.createBuffer(ch, len, rate);

    for (let i = 0; i < ch; i++) {
        const chanData = buf.getChannelData(i);
        let pos = 0;
        for (const c of pcmChunks) {
            chanData.set(c[i], pos);
            pos += c[i].length;
        }
    }

    currentBuffer = buf;
    currentSource = context.createBufferSource();
    currentSource.buffer = buf;
    currentSource.connect(context.destination);
    await context.resume();

    offsetAtStart = Math.min(seekSeconds, buf.duration);
    startTime = context.currentTime;
    currentSource.start(0, offsetAtStart);
}

async function streamViaBlob(fileId, token, mime) {
    const res = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
        headers: { Authorization: `Bearer ${token}` }
    });

    const blob = await res.blob();
    const url = URL.createObjectURL(blob);

    const audio = new Audio();
    audio.src = url;
    audio.type = mime;
    audio.controls = true;
    audio.autoplay = true;

    document.body.appendChild(audio); // or replace existing player
}

window.seekToSecond = (seconds) => {
    if (!audioContext || !currentBuffer) return;
    if (currentSource) currentSource.stop();

    const ctx = audioContext;
    currentSource = ctx.createBufferSource();
    currentSource.buffer = currentBuffer;
    currentSource.connect(ctx.destination);
    currentSource.onended = () => console.log("Seek end");

    offsetAtStart = Math.min(seconds, currentBuffer.duration);
    startTime = ctx.currentTime;
    ctx.resume();
    currentSource.start(0, offsetAtStart);
};

window.getCurrentDuration = () => currentBuffer?.duration ?? 0;
window.getCurrentTime = () => {
    if (!audioContext || !currentBuffer) return 0;
    return offsetAtStart + (audioContext.currentTime - startTime);
};
