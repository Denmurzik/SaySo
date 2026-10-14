"""Генератор звуковых тем SaySo: для каждой из 8 тем пишет start.wav, stop.wav и error.wav.

Результат (48 кГц, моно, 16 бит) лежит в src/SaySo.Core/Assets/Sounds/<id>/ и встраивается в SaySo.Core как ресурс.
Скрипт детерминирован (шум «комнаты» и «тапа» берётся из фиксированных зёрен), поэтому повторный запуск
даёт те же байты. Нужны numpy и scipy.

Запуск из корня репозитория:
    python tools/sounds/generate_sounds.py src/SaySo.Core/Assets/Sounds
Необязательный ключ --demo добавляет в каталог вывода файлы <id>-demo.wav (start, stop и error подряд) для прослушивания.
"""
import os
import sys

import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

SR = 48000


def env(n, attack_ms, tau_ms):
    t = np.arange(n) / SR
    a = np.clip(t / (attack_ms / 1000), 0, 1)
    a = np.sin(a * np.pi / 2) ** 2  # мягкая атака
    return a * np.exp(-t / (tau_ms / 1000))


def tone(freq, dur_ms, partials, attack_ms=3, tau_ms=150, start_ms=0):
    """partials: [(ratio, gain, tau_scale)]"""
    n = int(SR * dur_ms / 1000)
    t = np.arange(n) / SR
    out = np.zeros(n)
    for ratio, gain, tau_scale in partials:
        out += gain * np.sin(2 * np.pi * freq * ratio * t) * env(n, attack_ms, tau_ms * tau_scale)
    pad = np.zeros(int(SR * start_ms / 1000))
    return np.concatenate([pad, out])


def fm(freq, dur_ms, ratio, index, idx_tau_ms, tau_ms, attack_ms=2, start_ms=0):
    n = int(SR * dur_ms / 1000)
    t = np.arange(n) / SR
    idx = index * np.exp(-t / (idx_tau_ms / 1000))
    mod = idx * np.sin(2 * np.pi * freq * ratio * t)
    out = np.sin(2 * np.pi * freq * t + mod) * env(n, attack_ms, tau_ms)
    pad = np.zeros(int(SR * start_ms / 1000))
    return np.concatenate([pad, out])


def sweep(f0, f1, dur_ms, tau_ms, attack_ms=2, start_ms=0):
    n = int(SR * dur_ms / 1000)
    t = np.arange(n) / SR
    k = np.log(f1 / f0) / (dur_ms / 1000 * 0.35)
    f = f0 * np.exp(np.minimum(t * k, np.log(f1 / f0)))
    phase = 2 * np.pi * np.cumsum(f) / SR
    out = (np.sin(phase) + 0.12 * np.sin(2 * phase)) * env(n, attack_ms, tau_ms)
    pad = np.zeros(int(SR * start_ms / 1000))
    return np.concatenate([pad, out])


def mix(*parts):
    n = max(len(p) for p in parts)
    out = np.zeros(n)
    for p in parts:
        out[: len(p)] += p
    return out


def room(x, wet=0.12):
    """Короткий мягкий «хвост» комнаты: затухающий шум, свёрнутый со звуком."""
    n = int(SR * 0.35)
    rng = np.random.default_rng(7)
    ir = rng.standard_normal(n) * np.exp(-np.arange(n) / (SR * 0.08))
    ir = sosfilt(butter(2, 5000, "low", fs=SR, output="sos"), ir)
    ir /= np.sqrt(np.sum(ir ** 2))
    tail = np.convolve(x, ir)
    dry = np.concatenate([x, np.zeros(len(tail) - len(x))])
    return dry + wet * tail


def finish(x, peak_db):
    x = sosfilt(butter(2, 9000, "low", fs=SR, output="sos"), x)  # убрать резкость
    x = sosfilt(butter(1, 120, "high", fs=SR, output="sos"), x)
    fade = int(SR * 0.01)
    x[-fade:] *= np.linspace(1, 0, fade)
    # обрезать тишину в конце
    thr = np.max(np.abs(x)) * 10 ** (-60 / 20)
    last = np.max(np.nonzero(np.abs(x) > thr)) + fade
    x = x[: min(len(x), last)]
    x = x / np.max(np.abs(x)) * 10 ** (peak_db / 20)
    return x


def lp(x, hz, order=2):
    return sosfilt(butter(order, hz, "low", fs=SR, output="sos"), x)


def thump(freq, dur_ms, tau_ms, drop=0.18, drop_ms=25, attack_ms=6, start_ms=0, body=0.25):
    """Мягкий низкий удар: синус с лёгким спадом высоты и небольшим «телом» на октаву выше."""
    n = int(SR * dur_ms / 1000)
    t = np.arange(n) / SR
    f = freq * (1 + drop * np.exp(-t / (drop_ms / 1000)))
    ph = 2 * np.pi * np.cumsum(f) / SR
    out = (np.sin(ph) + body * np.sin(2 * ph)) * env(n, attack_ms, tau_ms)
    return np.concatenate([np.zeros(int(SR * start_ms / 1000)), out])


def felt(freq, dur_ms, tau_ms, attack_ms=7, start_ms=0):
    """Молоточек в войлоке: основной тон + слабые нижние гармоники, быстро гаснущие верхи."""
    return tone(freq, dur_ms, [(1.0, 1.0, 1.0), (2.0, 0.18, 0.45), (3.0, 0.05, 0.25)],
                attack_ms=attack_ms, tau_ms=tau_ms, start_ms=start_ms)


def tap(freq, dur_ms, tau_ms, start_ms=0):
    """Тёплый «тап»: короткий отфильтрованный шум-щелчок + мягкий тон корпуса."""
    n = int(SR * dur_ms / 1000)
    t = np.arange(n) / SR
    rng = np.random.default_rng(3)
    click = lp(rng.standard_normal(n) * np.exp(-t / 0.004), 1800) * 0.35
    body = np.sin(2 * np.pi * freq * t) * env(n, 4, tau_ms)
    out = click + body
    return np.concatenate([np.zeros(int(SR * start_ms / 1000)), out])


def kalimba(freq, dur_ms, tau_ms, start_ms=0):
    return tone(freq, dur_ms, [(1.0, 1.0, 1.0), (5.4, 0.06, 0.12), (2.0, 0.08, 0.4)],
                attack_ms=4, tau_ms=tau_ms, start_ms=start_ms)


def soft(x, cutoff, wet):
    return lp(room(x, wet), cutoff)


GLASS = [(1.0, 1.0, 1.0), (2.0, 0.35, 0.6), (2.76, 0.12, 0.35), (4.0, 0.05, 0.25)]
MUTED = [(1.0, 1.0, 1.0), (2.0, 0.22, 0.5), (3.0, 0.06, 0.3)]


def note(midi):
    return 440.0 * 2 ** ((midi - 69) / 12)


SETS = {
    # 1. Стекло: два лёгких стеклянных тона вверх / вниз
    "1-steklo": {
        "start": lambda: room(mix(tone(note(88), 500, GLASS, tau_ms=140),
                                  tone(note(95), 600, GLASS, tau_ms=180, start_ms=75)), 0.10),
        "stop": lambda: room(mix(tone(note(95), 500, GLASS, tau_ms=140),
                                 tone(note(88), 600, GLASS, tau_ms=180, start_ms=75)), 0.10),
        "error": lambda: room(mix(tone(note(69), 600, MUTED, attack_ms=6, tau_ms=170),
                                  tone(note(64), 800, MUTED, attack_ms=6, tau_ms=230, start_ms=130)), 0.12),
    },
    # 2. Капля: мягкий «пузырёк» с подъёмом / опусканием высоты
    "2-kaplya": {
        "start": lambda: room(sweep(620, 1240, 260, tau_ms=70), 0.08),
        "stop": lambda: room(sweep(1240, 700, 260, tau_ms=70), 0.08),
        "error": lambda: room(mix(sweep(520, 380, 260, tau_ms=80),
                                  sweep(430, 300, 320, tau_ms=95, start_ms=150)), 0.10),
    },
    # 3. Дерево: тёплая маримба (FM), два удара
    "3-derevo": {
        "start": lambda: room(mix(fm(note(84), 450, 4.0, 1.6, 18, 110),
                                  fm(note(91), 520, 4.0, 1.6, 18, 140, start_ms=85)), 0.12),
        "stop": lambda: room(mix(fm(note(91), 450, 4.0, 1.6, 18, 110),
                                 fm(note(84), 520, 4.0, 1.6, 18, 140, start_ms=85)), 0.12),
        "error": lambda: room(mix(fm(note(62), 600, 4.0, 1.2, 20, 160),
                                  fm(note(57), 800, 4.0, 1.2, 20, 220, start_ms=140)), 0.14),
    },
    # 4. Колокольчик: один чистый звон с длинным мягким хвостом
    "4-kolokolchik": {
        "start": lambda: room(fm(note(93), 1100, 3.5, 1.1, 60, 320), 0.16),
        "stop": lambda: room(fm(note(86), 1100, 3.5, 1.1, 60, 300), 0.16),
        "error": lambda: room(mix(fm(note(69), 1000, 3.5, 0.9, 70, 260),
                                  fm(note(65), 1300, 3.5, 0.9, 70, 340, start_ms=160)), 0.18),
    },
    # 5. Войлок (по умолчанию): молоточек в войлоке, средний регистр
    "5-voylok": {
        "start": lambda: soft(mix(felt(note(72), 600, 170), felt(note(76), 700, 200, start_ms=90)), 2200, 0.12),
        "stop": lambda: soft(mix(felt(note(76), 600, 170), felt(note(72), 700, 200, start_ms=90)), 2200, 0.12),
        "error": lambda: soft(mix(felt(note(62), 700, 200), felt(note(58), 900, 260, start_ms=150)), 1800, 0.14),
    },
    # 6. Подушка: низкий мягкий «тук», как по подушке
    "6-podushka": {
        "start": lambda: soft(mix(thump(330, 300, 80), thump(415, 320, 90, start_ms=85)), 1500, 0.08),
        "stop": lambda: soft(mix(thump(415, 300, 80), thump(330, 320, 90, start_ms=85)), 1500, 0.08),
        "error": lambda: soft(mix(thump(220, 380, 110), thump(175, 420, 130, start_ms=150)), 1200, 0.10),
    },
    # 7. Тап: тёплый тап с корпусом, как мягкая клавиша
    "7-tap": {
        "start": lambda: soft(mix(tap(523, 300, 70), tap(659, 320, 80, start_ms=80)), 2500, 0.08),
        "stop": lambda: soft(mix(tap(659, 300, 70), tap(523, 320, 80, start_ms=80)), 2500, 0.08),
        "error": lambda: soft(mix(tap(349, 340, 90), tap(294, 380, 110, start_ms=140)), 2000, 0.10),
    },
    # 8. Калимба: мягкая калимба, низкий регистр
    "8-kalimba": {
        "start": lambda: soft(mix(kalimba(note(67), 700, 220), kalimba(note(71), 800, 260, start_ms=90)), 2800, 0.14),
        "stop": lambda: soft(mix(kalimba(note(71), 700, 220), kalimba(note(67), 800, 260, start_ms=90)), 2800, 0.14),
        "error": lambda: soft(mix(kalimba(note(59), 800, 260), kalimba(note(55), 1000, 320, start_ms=150)), 2200, 0.16),
    },
}

PEAK = {"start": -14.0, "stop": -14.0, "error": -12.0}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    demo_enabled = "--demo" in sys.argv[1:]
    if len(args) != 1:
        sys.exit("Использование: python generate_sounds.py <каталог вывода> [--demo]")
    out = args[0]

    os.makedirs(out, exist_ok=True)
    for name, sounds in SETS.items():
        folder = os.path.join(out, name)
        os.makedirs(folder, exist_ok=True)
        demo = []
        for kind in ("start", "stop", "error"):
            x = finish(sounds[kind](), PEAK[kind])
            wavfile.write(os.path.join(folder, f"{kind}.wav"), SR, (x * 32767).astype(np.int16))
            demo += [x, np.zeros(int(SR * 0.9))]
        if demo_enabled:
            wavfile.write(os.path.join(out, f"{name}-demo.wav"), SR, (np.concatenate(demo) * 32767).astype(np.int16))
    print("ok")


if __name__ == "__main__":
    main()
