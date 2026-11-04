#!/usr/bin/env python3
"""One-file Basic Pitch bridge used by the BeatzOnKeys C# process wrapper."""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import sys
import traceback
import uuid


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: transcribe.py <input-audio> <output-midi>", file=sys.stderr)
        return 2

    source = Path(sys.argv[1]).expanduser().resolve()
    target = Path(sys.argv[2]).expanduser().resolve()
    if not source.is_file():
        print(f"Input audio does not exist: {source}", file=sys.stderr)
        return 3

    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.parent / f".basic-pitch-{uuid.uuid4().hex}"
    temporary.mkdir(parents=True, exist_ok=False)

    try:
        print("BOK_PROGRESS loading-model", flush=True)
        from basic_pitch import ICASSP_2022_MODEL_PATH
        from basic_pitch.inference import Model, predict_and_save

        model = Model(ICASSP_2022_MODEL_PATH)
        print("BOK_PROGRESS transcribing", flush=True)
        predict_and_save(
            audio_path_list=[source],
            output_directory=temporary,
            save_midi=True,
            sonify_midi=False,
            save_model_outputs=False,
            save_notes=False,
            model_or_model_path=model,
        )

        candidates = sorted(temporary.glob("*.mid")) + sorted(temporary.glob("*.midi"))
        if not candidates:
            print("Basic Pitch did not create a MIDI file.", file=sys.stderr)
            return 4

        if target.exists():
            target.unlink()
        shutil.move(str(candidates[0]), str(target))
        print(f"BOK_RESULT {target}", flush=True)
        return 0
    except Exception:
        traceback.print_exc(file=sys.stderr)
        return 1
    finally:
        shutil.rmtree(temporary, ignore_errors=True)


if __name__ == "__main__":
    os.environ.setdefault("PYTHONUTF8", "1")
    raise SystemExit(main())
