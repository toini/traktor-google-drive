window.secureStreamToAudio = async (audioElement, fileId, token) => {

    console.log("Starting secure stream", fileId);
    console.log("audioElement.readyState", audioElement.readyState);


    const mediaSource = new MediaSource();
    audioElement.src = URL.createObjectURL(mediaSource);

    const estimatedBitrate = 192000; // bits per second (192 kbps MP3)
    const bytesPerSecond = estimatedBitrate / 8;

    mediaSource.addEventListener("sourceopen", async () => {
        const mimeCodec = 'audio/mpeg';
        const sourceBuffer = mediaSource.addSourceBuffer(mimeCodec);

        const seekTo = audioElement.dataset.seekstart
            ? parseFloat(audioElement.dataset.seekstart)
            : 0;

        const byteOffset = Math.floor(bytesPerSecond * seekTo);

        try {
            // Warmup: fetch initial chunk from seek offset
            const warmupRes = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
                headers: {
                    Authorization: `Bearer ${token}`,
                    Range: `bytes=${byteOffset}-${byteOffset + 65535}`
                }
            });

            if (!warmupRes.ok) throw new Error(`Warmup failed: ${warmupRes.status}`);

            const warmupData = await warmupRes.arrayBuffer();
            sourceBuffer.appendBuffer(warmupData);

            // Begin full progressive streaming from seek offset
            const fullRes = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
                headers: {
                    Authorization: `Bearer ${token}`,
                    Range: `bytes=${byteOffset}-`
                }
            });

            if (!fullRes.ok) throw new Error(`HTTP ${fullRes.status} - ${fullRes.statusText}`);

            const reader = fullRes.body.getReader();

            const pump = async () => {
                const { done, value } = await reader.read();
                if (done) {
                    mediaSource.endOfStream();
                    return;
                }

                sourceBuffer.appendBuffer(value);
                sourceBuffer.addEventListener("updateend", pump, { once: true });
            };

            pump();
        } catch (err) {
            console.error("secureStreamToAudio failed:", err);
            mediaSource.endOfStream("decode");
        }
    });
};
