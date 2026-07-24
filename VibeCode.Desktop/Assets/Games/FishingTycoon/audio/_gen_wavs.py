"""Generate custom ambient WAV samples for Fishing Tycoon."""
import math
import os
import random
import struct
import wave

OUT = os.path.dirname(os.path.abspath(__file__))
SR = 22050


def write_wav(path, samples, rate=SR):
    data = bytearray()
    for s in samples:
        v = max(-1.0, min(1.0, s))
        data += struct.pack("<h", int(v * 32767))
    with wave.open(path, "w") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(data)


def noise():
    return random.random() * 2 - 1


def gen_ocean():
    n = int(SR * 4.0)
    samples = []
    lp = 0.0
    for i in range(n):
        t = i / SR
        lp = lp * 0.92 + noise() * 0.08
        swell = 0.55 + 0.45 * math.sin(t * 0.7) * math.sin(t * 0.23 + 1.2)
        crest = 0.0
        if math.sin(t * 1.1) > 0.92:
            crest = (math.sin(t * 1.1) - 0.92) * 4 * abs(noise()) * 0.25
        samples.append(
            (lp * 0.55 + noise() * 0.04 + noise() * 0.02 * math.sin(t * 3)) * swell * 0.35
            + crest * 0.15
        )
    write_wav(os.path.join(OUT, "ocean.wav"), samples)


def gen_rain():
    n = int(SR * 2.5)
    rain = []
    for _ in range(n):
        drops = 0.0
        for _ in range(3):
            if random.random() < 0.08:
                drops += (random.random() * 2 - 1) * 0.3
        rain.append(noise() * 0.12 + drops * 0.08)
    lp = 0.0
    out = []
    for s in rain:
        lp = lp * 0.97 + s * 0.03
        out.append((s - lp) * 0.9)
    write_wav(os.path.join(OUT, "rain.wav"), out)


def gen_thunder():
    n = int(SR * 1.8)
    th = []
    for i in range(n):
        t = i / SR
        env = math.exp(-t * 2.8)
        rumble = math.sin(2 * math.pi * (38 + t * 8) * t) * 0.5 * math.exp(-t * 1.2)
        crack = noise() * (1 - t / 0.08) if t < 0.08 else 0.0
        body = noise() * 0.35 * env
        boom = math.sin(2 * math.pi * 55 * t) * math.exp(-t * 3.5) * 0.7
        th.append((crack * 0.55 + body + rumble + boom) * 0.55)
    write_wav(os.path.join(OUT, "thunder.wav"), th)


def gen_motor():
    n = int(SR * 1.2)
    motor = []
    for i in range(n):
        t = i / SR
        base = math.sin(2 * math.pi * 72 * t) * 0.35
        over = math.sin(2 * math.pi * 144 * t) * 0.12
        putter = math.sin(2 * math.pi * 9 * t) * 0.08
        motor.append((base + over + putter + noise() * 0.015) * 0.28)
    write_wav(os.path.join(OUT, "motor.wav"), motor)


def gen_splash():
    n = int(SR * 0.35)
    splash = []
    for i in range(n):
        t = i / SR
        env = math.exp(-t * 12)
        splash.append((noise() * 0.6 + math.sin(2 * math.pi * 420 * t) * 0.2) * env * 0.5)
    write_wav(os.path.join(OUT, "splash.wav"), splash)


def gen_sell():
    n = int(SR * 0.4)
    ding = []
    for i in range(n):
        t = i / SR
        env = math.exp(-t * 6)
        ding.append(
            (math.sin(2 * math.pi * 880 * t) * 0.4 + math.sin(2 * math.pi * 1320 * t) * 0.2)
            * env
            * 0.4
        )
    write_wav(os.path.join(OUT, "sell.wav"), ding)


def _biquad_bp(sig, f0, q, sr=SR):
    """Resonant band-pass, used to put vocal-tract formants on the raw buzz."""
    w0 = 2 * math.pi * f0 / sr
    alpha = math.sin(w0) / (2 * q)
    a0 = 1 + alpha
    b0, b1, b2 = alpha / a0, 0.0, -alpha / a0
    a1, a2 = (-2 * math.cos(w0)) / a0, (1 - alpha) / a0
    out = []
    x1 = x2 = y1 = y2 = 0.0
    for x in sig:
        y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, x
        y2, y1 = y1, y
        out.append(y)
    return out


def _gull_cry(dur, f_hi, f_lo):
    """One harsh 'aaow'.

    A gull is not a whistle. The character comes from a dense harmonic stack
    (a sawtooth, near enough) pushed through nasal formants and clipped, plus
    a fast tremolo — the rasp is what makes the ear hear a bird rather than a
    synth sweep. Pure sine partials, which is what this used to be, read as a
    theremin.
    """
    n = int(SR * dur)
    raw = []
    phase = 0.0
    for i in range(n):
        k = i / n
        # Pitch breaks upward off the attack, then falls the whole way down.
        f = f_hi + (f_lo - f_hi) * (k ** 0.7)
        f *= 0.70 + 0.30 * min(1.0, k / 0.10)
        f *= 1.0 + 0.04 * math.sin(2 * math.pi * 26 * (i / SR))   # tremolo
        phase += 2 * math.pi * f / SR
        v = 0.0
        for h in range(1, 13):                                     # harmonic stack
            v += math.sin(phase * h) / (h ** 0.85)
        raw.append(v / 3.2 + noise() * 0.09)

    f1 = _biquad_bp(raw, 1150, 5.0)
    f2 = _biquad_bp(raw, 2500, 7.0)
    f3 = _biquad_bp(raw, 3900, 8.0)
    out = []
    for i in range(n):
        k = i / n
        v = math.tanh((raw[i] * 0.40 + f1[i] * 1.05 + f2[i] * 0.9 + f3[i] * 0.5) * 1.7)
        env = min(1.0, k / 0.035) * min(1.0, (1 - k) / 0.20) * (0.55 + 0.45 * (1 - k))
        out.append(v * env)
    return out


def gen_gull():
    """Herring gull long call — two harsh descending cries, the second weaker."""
    parts = [(0.00, 0.30, 1180, 760, 1.00), (0.44, 0.34, 1030, 660, 0.78)]
    n = int(SR * 1.02)
    out = [0.0] * n
    for start, dur, f_hi, f_lo, amp in parts:
        cry = _gull_cry(dur, f_hi, f_lo)
        s = int(SR * start)
        for i, v in enumerate(cry):
            if s + i < n:
                out[s + i] += v * amp * 0.30
    write_wav(os.path.join(OUT, "gull.wav"), out)


if __name__ == "__main__":
    gen_ocean()
    gen_rain()
    gen_thunder()
    gen_motor()
    gen_splash()
    gen_sell()
    gen_gull()
    for f in sorted(os.listdir(OUT)):
        if f.endswith(".wav"):
            p = os.path.join(OUT, f)
            print(f, os.path.getsize(p))
