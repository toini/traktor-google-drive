// This version detects FLAC and WAV and uses dedicated decoding strategies
// FLAC decoder must be loaded via:
// <script>
//   window.FLAC_SCRIPT_LOCATION = "https://cdn.jsdelivr.net/npm/libflacjs@5/dist/";
// </script>
// <script src="https://cdn.jsdelivr.net/npm/libflacjs@5/dist/libflac.min.wasm.js"></script>

let audioContext = null;
let currentSource = null;
let currentBuffer = null;

window.secureStreamToAudio = async (fileId, token, mime = "audio/mpeg", seekSeconds = 0) => {
    console.log("Starting secure stream", fileId, mime);

    if (audioContext == null || audioContext.state === "closed") {
        audioContext = new AudioContext();
    }
    if (currentSource) {
        currentSource.stop();
    }

    if (mime === "audio/flac") {
        return streamFlac(fileId, token, seekSeconds);
    } else if (mime === "audio/wav" || mime === "audio/wave" || mime === "audio/x-wav") {
        return streamWav(fileId, token, seekSeconds);
    }

    console.error("Only FLAC and WAV are currently supported in secureStreamToAudio");
};

// FLAC decoding using barebones libflac.js API + AudioContext
async function streamFlac(fileId, token, seekSeconds = 0) {
    console.log("streamFlac called");

    const flacReady = () => new Promise(resolve => {
        if (Flac.isReady()) resolve();
        else Flac.on('ready', () => resolve());
    });

    if (typeof Flac === 'undefined') {
        console.error("libflac.js is not loaded");
        return;
    }

    await flacReady();
    console.log("FLAC decoder ready");

    const context = audioContext;

    const response = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
        headers: { Authorization: `Bearer ${token}` }
    });

    const decoder = Flac.create_libflac_decoder(true);
    if (!decoder) {
        console.error("Failed to create FLAC decoder");
        return;
    }

    const reader = response.body.getReader();
    const flacData = [];
    while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        console.log("push value one by one; length", value.length);
        if (value) for (let i = 0; i < value.length; i++) flacData.push(value[i]);
    }

    const data = new Uint8Array(flacData);
    let offset = 0;

    const read_callback_fn = (bufferSize) => {
        const end = offset >= data.length ? -1 : Math.min(offset + bufferSize, data.length);
        if (end === -1) return { buffer: null, readDataLength: 0, error: false };
        const chunk = data.subarray(offset, end);
        const readLen = end - offset;
        offset = end;
        return { buffer: chunk, readDataLength: readLen, error: false };
    };

    const pcmChunks = [];
    let metadata = null;

    const write_callback_fn = (channelsBuffer, frame) => {
        console.log("push to pcmChunks", metadata?.bitsPerSample);

        const bps = metadata?.bitsPerSample ?? 16;
        const scale = 1 / (1 << (bps - 1));

        const converted = channelsBuffer.map(intBuf => {
            const floatBuf = new Float32Array(intBuf.length);
            for (let i = 0; i < intBuf.length; i++) {
                floatBuf[i] = intBuf[i] * scale;
            }
            return floatBuf;
        });

        pcmChunks.push(converted);
        console.log("First few PCM samples of channel 0:", converted[0].slice(0, 10));
    };

    const metadata_callback_fn = (meta) => {
        console.log("metadata_callback_fn", meta);
        metadata = meta;
    };

    const error_callback_fn = (code, msg) => {
        console.error("FLAC decode error", code, msg);
    };

    Flac.FLAC__stream_decoder_set_metadata_respond(decoder);

    const status_decoder = Flac.init_decoder_stream(
        decoder,
        read_callback_fn,
        write_callback_fn,
        error_callback_fn,
        metadata_callback_fn
    );

    if (status_decoder !== 0) {
        console.error("Failed to init FLAC decoder");
        return;
    }

    const processSuccess = Flac.FLAC__stream_decoder_process_until_end_of_stream(decoder);
    Flac.FLAC__stream_decoder_finish(decoder);
    Flac.FLAC__stream_decoder_delete(decoder);

    console.log("pcmChunks.length", pcmChunks.length);
    console.log("metadata", metadata);

    if (!pcmChunks.length || !metadata || !metadata.sampleRate || !metadata.channels) {
        console.error("No PCM data or metadata", { pcmChunksLength: pcmChunks.length, metadata });
        return;
    }

    const channels = metadata.channels;
    const totalSamples = pcmChunks.reduce((acc, chunk) => acc + chunk[0].length, 0);
    const buffer = context.createBuffer(channels, totalSamples, metadata.sampleRate);

    for (let ch = 0; ch < channels; ch++) {
        const chData = buffer.getChannelData(ch);
        let pos = 0;
        for (const chunk of pcmChunks) {
            chData.set(chunk[ch], pos);
            pos += chunk[ch].length;
        }
    }

    currentBuffer = buffer;
    currentSource = context.createBufferSource();
    currentSource.buffer = buffer;
    currentSource.connect(context.destination);

    await context.resume();
    currentSource.onended = () => console.log("Playback finished");

    const seekOffset = Math.min(seekSeconds, buffer.duration);
    console.log("Starting playback from", seekOffset, "of", buffer.duration);
    currentSource.start(0, seekOffset);
}

// WAV decoding using AudioContext.decodeAudioData
async function streamWav(fileId, token, seekSeconds = 0) {
    const context = audioContext;

    const response = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
        headers: { Authorization: `Bearer ${token}` }
    });

    const buffer = await response.arrayBuffer();
    const audioBuffer = await context.decodeAudioData(buffer);

    currentBuffer = audioBuffer;
    currentSource = context.createBufferSource();
    currentSource.buffer = audioBuffer;
    currentSource.connect(context.destination);

    await context.resume();
    currentSource.onended = () => console.log("WAV Playback finished");

    const offset = Math.min(seekSeconds, audioBuffer.duration);
    console.log("Starting WAV playback from", offset, "of", audioBuffer.duration);
    currentSource.start(0, offset);
}

// Public helper to trigger seeking externally
window.seekToSecond = (seconds) => {
    if (!currentBuffer || !audioContext) return;

    if (currentSource) currentSource.stop();

    const context = audioContext;
    currentSource = context.createBufferSource();
    currentSource.buffer = currentBuffer;
    currentSource.connect(context.destination);
    currentSource.onended = () => console.log("Seeked playback ended");

    context.resume();
    const offset = Math.min(seconds, currentBuffer.duration);
    console.log("Seeking to second", offset);
    currentSource.start(0, offset);
};

window.getCurrentDuration = () => currentBuffer?.duration ?? 0;
