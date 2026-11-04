# Audio test fixtures

`neutral-1s.mp3` is a synthetic, non-musical regression fixture generated solely for BeatzOnKeys tests.

- Signal: 523.25 Hz sine, 44.1 kHz, stereo, 1 second, volume multiplier 0.45
- Encoding: libmp3lame, constant 128 kbit/s
- SHA-256: `F60A5F9D1327B62CEF399E83C8FD967925071930866353457E6DBD9E2C07428C`

Generation command:

```powershell
ffmpeg -hide_banner -loglevel error -y `
  -f lavfi -i "sine=frequency=523.25:sample_rate=44100:duration=1" `
  -af "volume=0.45" -ac 2 -c:a libmp3lame -b:a 128k `
  -metadata "comment=Generated neutral BeatzOnKeys regression fixture" `
  neutral-1s.mp3
```

The fixture is committed so CI can exercise real MP3 seek/decoder behavior without installing ffmpeg.
