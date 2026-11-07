# Source-built Windows USB transport

Upstream: https://github.com/HubertD/candle_dll
Commit: cad8d2885106d5bca5c8aa1c0fe6ad9e3afd90fd
License: LGPL-3.0-or-later (LICENSE); accompanying GPL terms in COPYING.

Local changes, 2026-10-27:
- piper_adapter.c exposes the small Cando-compatible ABI used by the controller.
- OUT pipe transfers time out after 20 ms and abort the pipe on failure.
- `cando_frame_send_pumped` submits one overlapped OUT transfer and services
  ordered IN completions on the same thread while it waits. The adapter shares
  64 buffers between CAN RX, host TX and echoes; completed IN reads must be
  requeued so its OUT endpoint can resume. No position transfer is retried.
- Failed or canceled OUT operations retain their OVERLAPPED/frame storage until
  completion is verified; uncertain operations prevent another submission.
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

The receive-pumping core requires the matching updated transport DLL and rejects
an older DLL without `cando_frame_send_pumped`. Install the two DLLs together.
Run `tools/Test-WindowsTransport.ps1` to exercise the actual production transport
with fake WinUSB and receive callbacks; it opens no hardware. The fixtures cover
receive backpressure, unchanged timeouts, cancellation, incomplete/partial
transfers, safe oversized-record discard and local-echo filtering.

Current receive recovery behavior: normal opens do not abort/reset/flush IN;
queued packets must complete so firmware can release their buffers. WinUSB
AUTO_SUSPEND is explicitly disabled while monitoring. Serious receive errors
include the transport error and packet length, and ordered URB readiness can be
inspected through the local wrapper diagnostics. Known USB stalls use the
separate fixed-device recovery service. See firmware/README.md for the tested
adapter reset guard.
