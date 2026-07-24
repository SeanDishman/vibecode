"""Generate VibeCode's built-in notification sounds.

Thirty short chimes, none longer than a couple of seconds. Everything here is
synthesised from scratch with the standard library, so the whole set is ours to
ship with no third-party licence attached to any of it.

Filenames are `NN-kebab-case.wav`; the app turns that into the display name, so
adding a sound is just dropping another entry in SOUNDS and re-running this.

    python _gen_sounds.py
"""
import math
import os
import random
import struct
import wave

OUT = os.path.dirname(os.path.abspath(__file__))
SR = 32000

random.seed(7)          # deterministic: re-running must not churn every file


# ── plumbing ──────────────────────────────────────────────────────────────────
def write_wav(name, samples, rate=SR):
    data = bytearray()
    for s in samples:
        v = max(-1.0, min(1.0, s))
        data += struct.pack("<h", int(v * 32767))
    with wave.open(os.path.join(OUT, name), "w") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(data)


def normalize(sig, target_rms=0.115, max_peak=0.92):
    """Match loudness by RMS rather than peak, so a soft pad and a sharp tick
    sit at the same perceived level instead of the tick sounding twice as loud."""
    if not sig:
        return sig
    rms = math.sqrt(sum(v * v for v in sig) / len(sig)) or 1e-9
    g = target_rms / rms
    peak = max(abs(v) for v in sig) * g
    if peak > max_peak:
        g *= max_peak / peak
    return [v * g for v in sig]


def shape(sig, attack=0.005, release=0.03):
    """Ramp both ends. Without it every sound starts and stops on a click."""
    n = len(sig)
    a = max(1, int(attack * SR))
    r = max(1, int(release * SR))
    out = []
    for i, v in enumerate(sig):
        g = min(1.0, i / a)
        if i > n - r:
            g *= max(0.0, (n - i) / r)
        out.append(v * g)
    return out


def mix(*layers):
    n = max(len(x) for x in layers)
    out = [0.0] * n
    for layer in layers:
        for i, v in enumerate(layer):
            out[i] += v
    return out


def delay(sig, seconds, gain=1.0, total=None):
    """Place a layer later in the buffer — used to sequence notes."""
    pad = int(seconds * SR)
    out = [0.0] * (pad + len(sig)) if total is None else [0.0] * total
    for i, v in enumerate(sig):
        if pad + i < len(out):
            out[pad + i] += v * gain
    return out


def silence(seconds):
    return [0.0] * int(seconds * SR)


# ── voices ────────────────────────────────────────────────────────────────────
def fm_bell(freq, dur, ratio=1.41, index=5.0, decay=3.5, idx_decay=6.0):
    """Two-operator FM bell. The modulation index decays faster than the
    amplitude, which is what gives a bell its bright strike and pure tail."""
    n = int(dur * SR)
    out = []
    for i in range(n):
        t = i / SR
        ie = index * math.exp(-idx_decay * t)
        m = math.sin(2 * math.pi * freq * ratio * t)
        out.append(math.sin(2 * math.pi * freq * t + ie * m) * math.exp(-decay * t))
    return shape(out, attack=0.003)


def bar(freq, dur, partials=((1.0, 1.0), (3.93, 0.34), (9.55, 0.11)), decay=7.0):
    """A struck bar (marimba, xylophone). Bar partials are inharmonic — roughly
    1 : 3.9 : 9.5 — which is exactly what stops it sounding like an organ."""
    n = int(dur * SR)
    out = []
    for i in range(n):
        t = i / SR
        v = 0.0
        for mult, amp in partials:
            v += math.sin(2 * math.pi * freq * mult * t) * amp * math.exp(-decay * mult ** 0.5 * t)
        out.append(v)
    return shape(out, attack=0.002)


def dc_block(sig, r=0.995):
    """One-pole DC blocker.

    Karplus-Strong needs this. Its loop filter averages adjacent samples, which
    has unity gain at 0 Hz, so whatever DC the initial noise burst happens to
    carry is preserved for the life of the note instead of decaying with it. Left
    in, it eats headroom (the normaliser then clamps on peak and the note comes
    out quieter than the rest of the set) and can thump a speaker on playback.
    """
    out = []
    x1 = y1 = 0.0
    for x in sig:
        y1 = x - x1 + r * y1
        x1 = x
        out.append(y1)
    return out


def pluck(freq, dur, damp=0.996, tone=0.5):
    """Karplus-Strong: a burst of noise round a delay line. Cheap, and the most
    convincing string-like tone you can get in a few lines."""
    n = int(dur * SR)
    ln = max(2, int(SR / freq))
    buf = [random.uniform(-1, 1) for _ in range(ln)]
    # Start from a zero-mean burst, then soften it or the attack is all fizz.
    mean = sum(buf) / ln
    buf = [v - mean for v in buf]
    for _ in range(2):
        buf = [(buf[i] + buf[(i - 1) % ln]) * 0.5 for i in range(ln)]
    out = []
    idx = 0
    for _ in range(n):
        v = buf[idx]
        nxt = buf[(idx + 1) % ln]
        buf[idx] = (v * tone + nxt * (1 - tone)) * damp
        out.append(v)
        idx = (idx + 1) % ln
    return shape(dc_block(out), attack=0.001)


def tone(freq, dur, decay=8.0, vib=0.0, vib_rate=6.0, bend=1.0):
    """Plain sine with optional vibrato and a pitch bend (bend = end/start)."""
    n = int(dur * SR)
    out = []
    phase = 0.0
    for i in range(n):
        t = i / SR
        f = freq * (bend ** (t / max(dur, 1e-6)))
        f *= 1.0 + vib * math.sin(2 * math.pi * vib_rate * t)
        phase += 2 * math.pi * f / SR
        out.append(math.sin(phase) * math.exp(-decay * t))
    return shape(out, attack=0.004)


def noise(dur, decay=30.0, lp=0.5):
    """Low-passed noise burst — ticks, clicks, swooshes."""
    n = int(dur * SR)
    out = []
    z = 0.0
    for i in range(n):
        z = z * lp + random.uniform(-1, 1) * (1 - lp)
        out.append(z * math.exp(-decay * i / SR))
    return shape(out, attack=0.001)


def sweep_noise(dur, f0, f1, q=6.0):
    """Noise through a sweeping resonant band-pass: a swoosh."""
    n = int(dur * SR)
    out = []
    y1 = y2 = 0.0
    for i in range(n):
        t = i / n
        f = f0 + (f1 - f0) * t
        w = 2 * math.pi * f / SR
        alpha = math.sin(w) / (2 * q)
        a0 = 1 + alpha
        b0, a1, a2 = alpha / a0, (-2 * math.cos(w)) / a0, (1 - alpha) / a0
        x = random.uniform(-1, 1)
        y = b0 * x - a1 * y1 - a2 * y2
        y2, y1 = y1, y
        out.append(y * math.sin(math.pi * t))
    return shape(out)


# Equal-tempered helper: note number relative to A4 = 440.
def hz(semitones_from_a4):
    return 440.0 * (2 ** (semitones_from_a4 / 12.0))


C5, D5, E5, G5, A5, C6, E6, G6 = (hz(n) for n in (3, 5, 7, 10, 12, 15, 19, 22))


# ── the set ───────────────────────────────────────────────────────────────────
def bells():
    yield "01-soft-bell", fm_bell(A5, 1.7, ratio=1.41, index=3.2, decay=3.0)
    yield "02-glass-bell", fm_bell(E6, 1.5, ratio=2.76, index=4.5, decay=4.2)
    yield "03-tubular", fm_bell(hz(-2), 2.6, ratio=1.02, index=7.0, decay=1.7)
    yield "04-temple-bell", fm_bell(hz(-9), 3.0, ratio=1.73, index=8.5, decay=1.4)
    yield "05-music-box", mix(bar(C6, 1.1, decay=9.0),
                              delay(bar(G6, 1.0, decay=9.0), 0.14, 0.7))


def mallets():
    yield "06-marimba", bar(C5, 1.0, decay=7.0)
    yield "07-vibraphone", [v * (0.82 + 0.18 * math.sin(2 * math.pi * 5.2 * i / SR))
                            for i, v in enumerate(bar(G5, 1.6, decay=3.4))]
    yield "08-wood-block", mix(noise(0.05, decay=90, lp=0.25), tone(1500, 0.09, decay=48))
    yield "09-kalimba", pluck(G5, 1.1, damp=0.9955, tone=0.62)
    yield "10-xylophone", bar(C6, 0.55, partials=((1.0, 1.0), (3.93, 0.5), (9.55, 0.2)), decay=11.0)


def motifs():
    yield "11-rise", mix(fm_bell(E5, 0.7, index=2.6, decay=6.0),
                         delay(fm_bell(A5, 1.2, index=2.6, decay=4.0), 0.12))
    yield "12-fall", mix(fm_bell(A5, 0.7, index=2.6, decay=6.0),
                         delay(fm_bell(E5, 1.2, index=2.6, decay=4.0), 0.12))
    yield "13-major-third", mix(fm_bell(C5, 1.5, index=2.4, decay=3.4),
                                fm_bell(E5, 1.5, index=2.4, decay=3.4))
    yield "14-perfect-fifth", mix(fm_bell(C5, 1.6, index=2.2, decay=3.2),
                                  fm_bell(G5, 1.6, index=2.2, decay=3.2))
    yield "15-arpeggio", mix(bar(C5, 1.3, decay=5.5),
                             delay(bar(E5, 1.2, decay=5.5), 0.10),
                             delay(bar(G5, 1.3, decay=5.0), 0.20))
    yield "16-question", mix(tone(D5, 0.5, decay=7.0),
                             delay(tone(A5, 0.7, decay=5.0, bend=1.06), 0.13))
    yield "17-chime-pair", mix(fm_bell(G5, 1.0, ratio=1.41, index=3.0, decay=4.5),
                               delay(fm_bell(C6, 1.4, ratio=1.41, index=3.0, decay=3.6), 0.16))


def digital():
    yield "18-blip", tone(1200, 0.10, decay=34)
    yield "19-pop", tone(620, 0.14, decay=26, bend=0.34)
    yield "20-tick", noise(0.045, decay=110, lp=0.15)
    yield "21-click", mix(noise(0.03, decay=160, lp=0.1), tone(2400, 0.05, decay=90))
    yield "22-ping", tone(2000, 0.42, decay=13)
    yield "23-sonar", mix(tone(760, 0.5, decay=9),
                          delay(tone(760, 0.5, decay=9), 0.20, 0.45),
                          delay(tone(760, 0.5, decay=9), 0.40, 0.20))


def textures():
    yield "24-water-drop", mix(tone(430, 0.22, decay=17, bend=2.6),
                               noise(0.02, decay=200, lp=0.4))
    yield "25-bubble", mix(tone(360, 0.10, decay=30, bend=2.2),
                           delay(tone(470, 0.09, decay=32, bend=2.2), 0.07, 0.8),
                           delay(tone(600, 0.10, decay=30, bend=2.2), 0.14, 0.65))
    yield "26-swoosh", sweep_noise(0.45, 400, 3200)
    yield "27-whistle-up", tone(760, 0.5, decay=5.0, vib=0.012, vib_rate=7.0, bend=2.0)
    yield "28-soft-pad", shape(mix(tone(C5, 1.9, decay=1.5),
                                   tone(G5, 1.9, decay=1.6),
                                   tone(E6, 1.9, decay=2.0)), attack=0.22, release=0.35)
    yield "29-harp-pluck", mix(pluck(C5, 1.4, damp=0.9968),
                               delay(pluck(G5, 1.3, damp=0.9968), 0.09, 0.85),
                               delay(pluck(E6, 1.2, damp=0.9965), 0.18, 0.7))
    yield "30-crystal", mix(fm_bell(E6, 1.8, ratio=3.46, index=3.0, decay=3.0),
                            fm_bell(E6 * 1.004, 1.8, ratio=3.46, index=3.0, decay=3.0))


SOUNDS = list(bells()) + list(mallets()) + list(motifs()) + list(digital()) + list(textures())

if __name__ == "__main__":
    longest = 0.0
    for name, sig in SOUNDS:
        sig = normalize(sig)
        write_wav(name + ".wav", sig)
        secs = len(sig) / SR
        longest = max(longest, secs)
        print(f"{name + '.wav':24} {secs:4.2f}s  {os.path.getsize(os.path.join(OUT, name + '.wav')):>7} bytes")
    total = sum(os.path.getsize(os.path.join(OUT, f)) for f in os.listdir(OUT) if f.endswith(".wav"))
    print(f"\n{len(SOUNDS)} sounds, longest {longest:.2f}s, {total / 1024:.0f} KB total")
