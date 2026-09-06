// Credentials arrive only through stdin and are never included in results/errors.
// This probes native static MP4 playback, not the jellyfin-web device-profile flow.
const fs = require('node:fs');

(async () => {
    const input = JSON.parse(fs.readFileSync(0, 'utf8'));
    const { chromium } = require(input.playwrightModule);
    const browser = await chromium.launch({ executablePath: input.chromium || undefined, headless: true });
    try {
        const page = await browser.newPage();
        const result = await page.evaluate(async ({ url }) => {
            const video = document.createElement('video');
            video.muted = true;
            video.autoplay = true;
            document.body.appendChild(video);
            const start = performance.now();
            const times = {};
            return await new Promise(resolve => {
                let finished = false;
                let timer;
                function finish(status) {
                    if (finished) return;
                    finished = true;
                    clearTimeout(timer);
                    video.pause();
                    video.removeAttribute('src');
                    video.load();
                    resolve({ status, ...times });
                }
                timer = setTimeout(() => finish('timeout'), 60000);
                video.addEventListener('loadedmetadata', () => {
                    times.loaded_metadata_ms = performance.now() - start;
                    times.video_width = video.videoWidth;
                    times.video_height = video.videoHeight;
                });
                video.addEventListener('playing', () => { times.playing_ms = performance.now() - start; }, { once: true });
                video.addEventListener('error', () => finish('media_error_' + (video.error?.code || 0)));
                if (!video.requestVideoFrameCallback) return finish('frame_callback_unsupported');
                video.requestVideoFrameCallback(() => {
                    times.first_frame_callback_ms = performance.now() - start;
                    finish('ok');
                });
                video.src = url;
                video.play().catch(() => finish('play_rejected'));
            });
        }, { url: input.url });
        process.stdout.write(JSON.stringify(result));
    } finally {
        await browser.close();
    }
})().catch(() => {
    process.stderr.write('Browser probe failed; private URLs omitted.\n');
    process.exitCode = 1;
});
