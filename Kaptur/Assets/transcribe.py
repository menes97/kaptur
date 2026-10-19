"""Kaptur transcript yardımcısı.

faster-whisper ile bir video/ses dosyasını <out-base>.txt ve <out-base>.srt dosyalarına çevirir.
Stdout'a satır başına bir JSON olay yazar; Kaptur (C#) bunları okuyup ilerlemeyi gösterir:
  {"event": "loading", ...}  {"event": "start", ...}  {"event": "progress", "pct": 0.42}
  {"event": "done", "txt": ..., "srt": ...}  {"event": "error", "message": ...}
"""
import argparse
import importlib
import json
import os
import sys

# Kaptur bizi `python -I` ile çalıştırır; -I, PYTHONIOENCODING'i yok saydığı için UTF-8'i burada sabitle.
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def emit(**kw):
    print(json.dumps(kw, ensure_ascii=False), flush=True)


def add_nvidia_dlls():
    # pip'teki nvidia-cublas-cu12 / nvidia-cudnn-cu12 paketleri DLL'lerini site-packages/nvidia/<paket>/bin
    # altına koyar; CTranslate2 bunları PATH üzerinden yükler.
    for name in ("cublas", "cudnn"):
        try:
            mod = importlib.import_module(f"nvidia.{name}")
        except ImportError:
            continue
        for root in getattr(mod, "__path__", []):
            d = os.path.join(root, "bin")
            if os.path.isdir(d):
                os.environ["PATH"] = d + os.pathsep + os.environ.get("PATH", "")
                os.add_dll_directory(d)


def patch_pyav():
    # faster-whisper 1.2.1 hâlâ av.open(..., metadata_errors="ignore") çağırıyor; PyAV 15+ bu parametreyi
    # kaldırdı. Eski PyAV'ın Windows için hazır paketi olmadığından sürüm sabitlemek yerine parametreyi düşürüyoruz.
    import inspect

    import av

    try:
        accepts = "metadata_errors" in inspect.signature(av.open).parameters
    except (TypeError, ValueError):  # Cython fonksiyonunun imzası okunamazsa yamala
        accepts = False
    if accepts:
        return
    original = av.open

    def open_compat(*args, **kwargs):
        kwargs.pop("metadata_errors", None)
        return original(*args, **kwargs)

    av.open = open_compat


def timestamp(sec):
    ms = int(round(sec * 1000))
    h, ms = divmod(ms, 3_600_000)
    m, ms = divmod(ms, 60_000)
    s, ms = divmod(ms, 1000)
    return f"{h:02}:{m:02}:{s:02},{ms:03}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("--out-base", required=True)
    ap.add_argument("--model", default="large-v3-turbo")
    ap.add_argument("--language", default="auto")
    ap.add_argument("--device", default="auto", choices=["auto", "cuda", "cpu"])
    a = ap.parse_args()

    add_nvidia_dlls()
    patch_pyav()
    import ctranslate2
    from faster_whisper import WhisperModel

    device = a.device
    if device == "auto":
        device = "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
    compute = "float16" if device == "cuda" else "int8"
    emit(event="loading", device=device, compute=compute, model=a.model)
    model = WhisperModel(a.model, device=device, compute_type=compute)

    language = None if a.language in ("", "auto") else a.language
    segments, info = model.transcribe(a.input, language=language, beam_size=5, vad_filter=True)
    emit(event="start", language=info.language, probability=round(info.language_probability, 3),
         duration=info.duration)

    lines, srt = [], []
    for seg in segments:
        text = seg.text.strip()
        if not text:
            continue
        lines.append(text)
        srt.append(f"{len(srt) + 1}\n{timestamp(seg.start)} --> {timestamp(seg.end)}\n{text}\n")
        if info.duration:
            emit(event="progress", pct=min(1.0, seg.end / info.duration))

    txt_path, srt_path = a.out_base + ".txt", a.out_base + ".srt"
    with open(txt_path, "w", encoding="utf-8") as f:
        # Müzik ya da sessiz videolarda VAD hiç konuşma bulamaz; boş dosya yerine bunu açıkça yaz.
        f.write("\n".join(lines) + "\n" if lines else "[Konuşma algılanmadı]\n")
    with open(srt_path, "w", encoding="utf-8") as f:
        f.write("\n".join(srt))
    emit(event="done", txt=txt_path, srt=srt_path, language=info.language, segments=len(srt))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # noqa: BLE001 — hatayı C# tarafına okunur biçimde ilet
        emit(event="error", message=f"{type(e).__name__}: {e}")
        sys.exit(1)
