# Source-built Windows USB transport

Upstream: https://github.com/HubertD/candle_dll
Commit: cad8d2885106d5bca5c8aa1c0fe6ad9e3afd90fd
License: LGPL-3.0-or-later (LICENSE); accompanying GPL terms in COPYING.

Local changes, 2026-10-27:
- piper_adapter.c exposes the small Cando-compatible ABI used by the controller.
- OUT pipe transfers time out after 20 ms and abort the pipe on failure.
- Read requeue accepts both immediate and pending completion.
- Receive drains URBs in submission order so multi-frame replies stay ordered.
- Receive accepts 20-byte and 24-byte records, zeroing omitted timestamps.
- Adapter startup requests hardware timestamps, matching the vendor ABI.
- Close aborts/cancels pending I/O before releasing events and buffers; if I/O
  does not complete, resources are retained rather than freed while in use.
- Preserve caller echo IDs and correct wide-path buffer character capacity.
- Enumeration is read-only and does not create I/O in temporary device lists.
- Open failure releases handles only after pending I/O is canceled.
- Optional legacy timestamp-control request is omitted.

Build independently from repository root:

gcc -shared -static-libgcc -std=c17 -O2 -g -DCANDLE_API_LIBRARY -DUNICODE -D_UNICODE -Ivendor/candle -o piper-cando.dll vendor/candle/candle.c vendor/candle/candle_ctrl_req.c vendor/candle/piper_adapter.c -lsetupapi -lwinusb -lole32

This DLL is dynamically loaded beside piper-core.dll and can be rebuilt or
replaced independently. The proprietary vendor cando.dll is not loaded.

Current receive recovery behavior: normal opens do not abort/reset/flush IN;
queued packets must complete so firmware can release their buffers. WinUSB
AUTO_SUSPEND is explicitly disabled while monitoring. Serious receive errors
include the transport error and packet length, and ordered URB readiness can be
inspected through the local wrapper diagnostics. Known USB stalls use the
separate fixed-device recovery service. See firmware/README.md for the tested
adapter reset guard.
