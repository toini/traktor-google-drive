window.setAudioSrc = async (audioElement, fileId, token) => {
    try {
        const res = await fetch(`https://www.googleapis.com/drive/v3/files/${fileId}?alt=media`, {
            headers: {
                Authorization: `Bearer ${token}`
            }
        });

        if (!res.ok) throw new Error(`Fetch failed: ${res.status}`);

        const blob = await res.blob();
        const objectUrl = URL.createObjectURL(blob);
        audioElement.src = objectUrl;
        await audioElement.play();
    } catch (err) {
        console.error("setAudioSrc failed:", err);
    }
};
