// This version detects FLAC and uses a dedicated WASM decoder (libflac.js)
// FLAC decoder must be loaded via:
// <script>
//   window.FLAC_SCRIPT_LOCATION = "https://cdn.jsdelivr.net/npm/libflacjs@5/dist/";
// </script>
// <script src="https://cdn.jsdelivr.net/npm/libflacjs@5/dist/libflac.min.wasm.js"></script>

let audioContext = null;

window.secureStreamToAudio = async (fileId, token, mime = "audio/mpeg") => {
    console.log("Starting secure stream", fileId, mime);

    if (mime === "audio/flac") {
        return streamFlac(fileId, token);
    }

    console.error("Only FLAC is currently supported in secureStreamToAudio");
};

// FLAC decoding using barebones libflac.js API + AudioContext
async function streamFlac(fileId, token) {
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

    if (audioContext == null || audioContext.state === "closed") {
        audioContext = new AudioContext();
        document.body.addEventListener('click', () => {
            if (audioContext.state !== "running") audioContext.resume();
        }, { once: true });
    }
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

    const source = context.createBufferSource();
    source.buffer = buffer;
    source.connect(context.destination);

    await context.resume();
    source.onended = () => console.log("Playback finished");
    console.log("Starting playback", context.currentTime, buffer.duration);

    source.start();

    // Optional: hook up your own playback UI and controls using AudioContext time.
}