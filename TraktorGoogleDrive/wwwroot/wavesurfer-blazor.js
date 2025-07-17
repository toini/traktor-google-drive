window.initWaveSurfer = async function (containerId, wavUrl, duration, peaksUrl) {
    console.log('Init WaveSurfer:', containerId, wavUrl, duration, peaksUrl);

    if (!window.WaveSurfer) {
        const mod = await import('https://unpkg.com/wavesurfer.js@7/dist/wavesurfer.esm.js');
        window.WaveSurfer = mod.default || mod.WaveSurfer;
    }
    let peaks = undefined;
    if (peaksUrl) {
        const peaksRes = await fetch(peaksUrl);
        peaks = [await peaksRes.json()];
    } else {
        // Dummy peaks: flat waveform
        peaks = [Array(Math.max(128, Math.floor(duration))).fill(0)];
    }
    const config = {
        container: `#${containerId}`,
        waveColor: 'lightgray',
        progressColor: 'orange',
        height: 128,
        barWidth: 1,
        duration,
        url: wavUrl
    };
    if (peaks) {
        config.peaks = peaks;
    }
    console.log('Creating WaveSurfer instance with config:', config);
    const waveform = WaveSurfer.create(config);
    console.log('WaveSurfer instance created:', waveform);
    return waveform;
};
window.waveSurferPlayPause = function (waveform) {
    if (waveform) waveform.playPause();
};
window.waveSurferSeek = function (waveform, seconds) {
    if (waveform) waveform.setTime(seconds);
};
