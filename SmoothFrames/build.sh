#!/bin/bash
# cross-compile from Linux with zig (pip download ziglang)
zig c++ -target x86_64-windows-gnu -O2 -std=c++17 smoothgen.cpp -o SmoothFrames.exe -ld3d11 -ldxgi -ldcomp -luser32 -lwinmm -lgdi32
