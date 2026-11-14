# Floating `3.11-slim` on purpose: the tag moves on every Python/Debian refresh and each move
# re-runs the ~2 GB torch install below with nothing changed here. Pinning the patch buys back
# that rebuild but freezes base CVE fixes behind a manual bump -- deliberately not worth it.
# 3.11 itself is load-bearing, not incidental: torch 2.1.2 ships cp38-cp311 wheels only.
FROM python:3.11-slim

# build-essential: hdbscan (pulled in transitively -- see below) publishes no linux-aarch64
# wheel, only manylinux x86_64/macOS/win_amd64 plus an sdist; on arm64 pip falls back to
# compiling the sdist's Cython extension, which needs a C/C++ toolchain. Verified empirically
# (2026-07-22): forcing `pip install --no-binary hdbscan hdbscan` on this x86 box with only
# build-essential + numpy present builds and installs hdbscan-0.8.44 cleanly via PEP517 build
# isolation (no extra system libs needed beyond gcc/g++/make) -- the arm64 compile itself is
# unverified until the Pi actually builds this image, but the toolchain requirement is
# architecture-generic.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl libsndfile1 build-essential \
    && rm -rf /var/lib/apt/lists/*

# pip's stock network policy is 5 retries at a 15s read timeout -- a ~99s window in total --
# and it treats an index it could not read as an EMPTY one. Observed on ai370 (2026-07-27):
# five consecutive ReadTimeouts on /whl/cpu/torch/ -- the 376 KB index *listing*, not the
# 200 MB wheel; it serves in ~0.3s from that host when the link is idle -- reported as the
# thoroughly misleading "Could not find a version that satisfies the requirement torch==2.1.2
# (from versions: none)". The trigger is contention rather than a broken path: the documented
# launch command runs `compose up --build` over ~20 services, and BuildKit saturates the
# uplink for longer than pip is willing to wait. Widening the window to 10 x 60s lets the
# build ride out a multi-minute stall instead of dying inside 99 seconds. Set as environment,
# not flags, because pip reads PIP_<OPTION> for every option (verified: a poison value fails
# config parsing) -- so this covers the onnx and gunicorn layers below without repetition.
ENV PIP_DEFAULT_TIMEOUT=60 \
    PIP_RETRIES=10

# torch first (CPU wheels; index serves x86_64 and aarch64), then the wesep/wespeaker
# import tree. torch/torchaudio pinned to 2.1.2 -- the version proven against
# wespeaker@e9bbf73 by the round-1 eval harness (tse_env_setup.sh). This is load-bearing,
# not cosmetic: current torchaudio (verified 2.11.0 against torch 2.13.0+cpu, 2026-07-22)
# dropped its built-in soundfile/sox backend dispatch (torchaudio.list_audio_backends is
# gone) and torchaudio.load() now hard-requires the separate torchcodec package, which
# isn't installed here -- wesep's Extractor.extract_speech calls torchaudio.load()
# directly, so an unpinned torch/torchaudio 500s on every /extract call
# (ModuleNotFoundError: No module named 'torchcodec'). numpy<2 for wespeaker@e9bbf73
# compatibility. wespeaker --no-deps so it cannot drag a different torch or the heavy
# s3prl frontend HEAD added. hdbscan/umap-learn/scikit-learn are NOT dead weight despite the
# extraction path never clustering anything: wesep.cli.extractor -> wesep.models (eagerly
# imports every model incl. bsrnn.py) -> wespeaker.models.speaker_model, and importing any
# wespeaker submodule runs wespeaker/__init__.py first, which unconditionally imports
# wespeaker.cli.speaker -> wespeaker.diar.umap_clusterer -> `import umap` / `import hdbscan`.
# Confirmed empirically (2026-07-22, not just by reading the chain): removing either package
# from a running sidecar and starting app.py raises ModuleNotFoundError before Flask even
# binds -- the app cannot boot without them, so both stay.
#
# Three separate layers rather than one chained RUN, for the same reason the onnx and gunicorn
# layers below are separate: a failed RUN discards everything it did, and with --no-cache-dir
# there is nothing to resume from. Chained, a stall in the PyPI leg or the git clone threw away
# the ~200 MB torch download too and the retry paid for it again. Split, each retry restarts at
# the first leg that actually failed.
RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu "torch==2.1.2" "torchaudio==2.1.2"

# numpy<2 for every install from here on, not only the one that names it: torch 2.1.2 is built
# against NumPy 1.x, and under 2.x every tensor<->array hand-off raises "Numpy is not available" —
# torchaudio.load is one, so every /extract answered 500. onnx (below) pulls ml_dtypes, whose 0.6
# wants numpy>=2, and pip upgraded numpy under torch (prod, 2026-11-12). With the constraint it
# resolves ml_dtypes 0.5.x, which onnx 1.22 accepts. After torch's layer, so that layer stays cached.
RUN echo "numpy<2" > /etc/pip-constraints.txt
ENV PIP_CONSTRAINT=/etc/pip-constraints.txt

RUN pip install --no-cache-dir "numpy<2" soundfile pyyaml scipy silero-vad tqdm \
        kaldiio hdbscan umap-learn scikit-learn "onnxruntime==1.27.0" requests flask

RUN pip install --no-cache-dir --no-deps \
        "wespeaker @ git+https://github.com/wenet-e2e/wespeaker.git@e9bbf73d0fd13db6cf42a6cb2eafb0d7dd0f8e0e"

# torch.onnx.export requires the onnx package at runtime (export happens at container
# startup because the checkpoint lives in the /models volume, not the image).
RUN pip install --no-cache-dir "onnx==1.22.0"

# Production WSGI server -- entrypoint.sh serves app:app with it instead of Flask's
# werkzeug dev server (app.run). Own layer so it never invalidates the torch layer.
RUN pip install --no-cache-dir "gunicorn==26.0.0"

RUN git clone https://github.com/wenet-e2e/wesep /opt/wesep-src \
    && git -C /opt/wesep-src checkout 99eca54b60300d39b9353d93cf285a14bba37854

COPY app.py entrypoint.sh onnx_core.py /opt/tse/
RUN chmod +x /opt/tse/entrypoint.sh

# NUMBA_CACHE_DIR: the compose service runs as PUID (non-root), which cannot write numba's
# default cache location next to site-packages sources — and umap.layouts uses
# @numba.njit(cache=True), which hard-fails at IMPORT ("no locator available") instead of
# degrading, so without a writable cache dir the app cannot even boot under a non-root uid.
ENV WESEP_SRC=/opt/wesep-src \
    MODEL_DIR=/models/wesep-english \
    VOICES_DIR=/voices \
    NUMBA_CACHE_DIR=/tmp/numba-cache

EXPOSE 9098
ENTRYPOINT ["/opt/tse/entrypoint.sh"]
