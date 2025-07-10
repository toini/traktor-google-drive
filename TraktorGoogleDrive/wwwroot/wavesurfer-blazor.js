window.initWaveSurfer = async function (containerId, wavUrl, peaksUrl, duration) {
    if (!window.WaveSurfer) {
        await import('https://unpkg.com/wavesurfer.js@7/dist/wavesurfer.esm.js');
    }
    const peaksRes = await fetch(peaksUrl);
    const flatPeaks = await peaksRes.json();
    const waveform = WaveSurfer.create({
        container: `#${containerId}`,
        waveColor: 'lightgray',
        progressColor: 'orange',
        height: 128,
        barWidth: 1,
        duration,
        url: wavUrl,
        peaks: [flatPeaks],
    });
    return waveform;
};
window.waveSurferPlayPause = function (waveform) {
    if (waveform) waveform.playPause();
};
window.waveSurferSeek = function (waveform, seconds) {
    if (waveform) waveform.setTime(seconds);
};
