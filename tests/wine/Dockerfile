# Windows-build smoke tests without Windows: Wine (Direct3D 11 -> OpenGL) on a virtual display with Mesa's
# software renderer. Catches crashes, shader compile errors and wrong pixels in the Direct3D renderer.
# It can't test AMD's driver or hardware video decoding: those still need a real Windows PC / ROG Ally.
#   docker build --platform linux/amd64 -t xstream-wine tests/wine
#   tests/wine/run.sh
FROM --platform=linux/amd64 ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends \
      wine wine64 xvfb xauth mesa-utils libgl1-mesa-dri ca-certificates \
    && rm -rf /var/lib/apt/lists/*
ENV WINEDEBUG=-all WINEPREFIX=/wine LIBGL_ALWAYS_SOFTWARE=1
# Create the Wine prefix once at build time (first start is slow).
RUN xvfb-run -a wineboot --init && wineserver -w || true
WORKDIR /work
