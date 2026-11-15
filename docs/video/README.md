# AI watermark for the demo video

The Space Apps rule on the use of AI asks for a **visible watermark** on images and video that contain AI-generated
content. Our lander models are AI-generated (Meshy AI concept image → Microsoft TRELLIS.2), so every clip that shows a
lander needs the mark.

Files (transparent PNG, the label in the bottom-right corner):

- `ai-watermark-1920x1080.png` — for a 1080p video
- `ai-watermark-1280x720.png` — for a 720p video

How to use: put the PNG on a track above the video for the whole length (or at least for every shot with a lander),
at 100 % size and position 0,0. In ffmpeg:

```bash
ffmpeg -i demo.mp4 -i docs/video/ai-watermark-1920x1080.png -filter_complex "overlay=0:0" -c:a copy demo-watermarked.mp4
```

Recording the app itself is not enough: its in-app badge is in the 3D view, and a crop or zoom can cut it out.

Other sizes: `node tools/optimize-models/watermark.mjs overlay out.png 2560`. New screenshots that show a lander:
`node tools/optimize-models/watermark.mjs stamp docs/screenshots/<file>.png`.
